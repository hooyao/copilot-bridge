using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract: the two bridge-internal markers T3 stamps on tool_use content_blocks
/// (<c>bridge_input_is_grammar_text</c>, <c>bridge_tool_namespace</c>) MUST NOT reach a
/// Claude Code client. On the Codex route T4 removes them; on the CC→gpt route (Claude
/// Code pointed at a gpt-5.6 <c>/responses</c> backend) there is no T4 — the response is
/// T3 → <see cref="ClaudeCodeOutboundAdapter"/> (client edge) → claude.exe. So the
/// adapter must scrub the markers, while leaving every real field intact and every
/// marker-free event byte-identical.
/// </summary>
/// <remarks>
/// This is the regression guard for the CC→gpt marker LEAK (a real bug: the markers are
/// non-standard content_block keys that would surface to Claude Code as bogus tool-call
/// metadata). Mutation-check: disable the scrub (return the event unchanged) → the leak
/// assertions redden.
/// </remarks>
public class ClaudeCodeMarkerScrubTests
{
    private static readonly ClaudeCodeOutboundAdapter Adapter =
        new(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    private static async Task<List<SseItem<string>>> Run(params SseItem<string>[] input)
    {
        var outp = new List<SseItem<string>>();
        await foreach (var e in Adapter.AdaptStreamAsync(ToAsync(input), default))
            outp.Add(e);
        return outp;
    }

    private static async IAsyncEnumerable<SseItem<string>> ToAsync(SseItem<string>[] items)
    {
        foreach (var i in items) yield return i;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GrammarAndNamespaceMarkers_AreStrippedFromToolUseBlock_RealFieldsSurvive()
    {
        // A CC→gpt tool_use content_block_start carrying BOTH markers (as T3 stamps for a
        // namespaced custom tool — the worst case).
        var evt = new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\","
            + "\"id\":\"call_1\",\"name\":\"exec\",\"input\":{},"
            + "\"bridge_input_is_grammar_text\":true,\"bridge_tool_namespace\":\"collaboration\"}}",
            "content_block_start");

        var outEvt = (await Run(evt))[0];

        // Neither marker survives to the client.
        Assert.DoesNotContain("bridge_input_is_grammar_text", outEvt.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge_tool_namespace", outEvt.Data, StringComparison.Ordinal);

        // Every REAL field of the tool_use block survives.
        using var doc = JsonDocument.Parse(outEvt.Data);
        var cb = doc.RootElement.GetProperty("content_block");
        Assert.Equal("tool_use", cb.GetProperty("type").GetString());
        Assert.Equal("call_1", cb.GetProperty("id").GetString());
        Assert.Equal("exec", cb.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, cb.GetProperty("input").ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("content_block_start", outEvt.EventType);
    }

    [Fact]
    public async Task MarkerFreeEvents_ArePassedThroughByteIdentical()
    {
        // The common case — a real Copilot Anthropic backend never stamps markers, and
        // most CC→gpt events (text deltas, message_start, plain function tools) have
        // none. Those must be returned as the SAME instance (no parse, no rewrite).
        var text = new SseItem<string>(
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}",
            "content_block_delta");
        var plainTool = new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"c2\",\"name\":\"read\",\"input\":{}}}",
            "content_block_start");

        var outp = await Run(text, plainTool);

        // Byte-identical data (and same reference — the scrub's fast path returns evt).
        Assert.Equal(text.Data, outp[0].Data);
        Assert.Equal(plainTool.Data, outp[1].Data);
        Assert.Same(text.Data, outp[0].Data);
        Assert.Same(plainTool.Data, outp[1].Data);
    }

    [Fact]
    public async Task ContentBlockWhoseValueMentionsAMarkerName_IsNotRewritten()
    {
        // Byte-identity contract: a content_block_start whose tool input VALUE happens
        // to contain a marker NAME (e.g. an exec arg mentioning "bridge_tool_namespace")
        // — but has no marker PROPERTY — must pass through unchanged. The substring
        // fast-filter matches, but the rewrite must be gated on an actual marker key.
        var evt = new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\","
            + "\"id\":\"c3\",\"name\":\"exec\",\"input\":{\"note\":\"the bridge_tool_namespace field\"}}}",
            "content_block_start");

        var outEvt = (await Run(evt))[0];
        // No marker property → returned unchanged (same instance), value preserved.
        Assert.Same(evt.Data, outEvt.Data);
        Assert.Contains("bridge_tool_namespace field", outEvt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheContentBlockMarkers_AreRemoved_NotSimilarlyNamedContent()
    {
        // Defensive: a text delta that happens to mention the marker name in its text
        // must NOT be corrupted — only the content_block keys are dropped, and this
        // event has no content_block, so it passes through untouched.
        var evt = new SseItem<string>(
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\","
            + "\"text\":\"the bridge_tool_namespace field is internal\"}}",
            "content_block_delta");

        var outEvt = (await Run(evt))[0];
        // Unchanged — the marker name inside prose is left alone (no content_block to scrub).
        Assert.Equal(evt.Data, outEvt.Data);
        Assert.Contains("bridge_tool_namespace field is internal", outEvt.Data, StringComparison.Ordinal);
    }
}
