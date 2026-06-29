using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// A6 — stream round-trip fidelity (change 3, task 6.5). Feeds a REAL captured
/// <c>/responses</c> SSE fixture through T3 (Responses→IR Anthropic) then T4
/// (IR→Responses), and asserts the re-emitted Responses stream preserves what
/// Codex's parser needs: the event types in order, text/argument deltas that
/// concatenate to identical content, and the terminal <c>response.completed</c>
/// with NO spurious <c>[DONE]</c>. The fixtures are real wire captures
/// (<c>tests/.../Fixtures/responses-sse-*.txt</c>), used as input samples.
/// </summary>
public class CodexStreamRoundTripTests
{
    private readonly ITestOutputHelper _output;
    public CodexStreamRoundTripTests(ITestOutputHelper output) => _output = output;

    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static List<SseItem<string>> ParseSse(string path)
    {
        var raw = File.ReadAllText(path);
        var items = new List<SseItem<string>>();
        string? evt = null;
        var data = new System.Text.StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("event:", StringComparison.Ordinal)) evt = l[6..].Trim();
            else if (l.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(l[5..].TrimStart());
            }
            else if (l.Length == 0 && (evt is not null || data.Length > 0))
            {
                items.Add(new SseItem<string>(data.ToString(), evt));
                evt = null; data.Clear();
            }
        }
        return items;
    }

    /// <summary>Run T3 (Responses→IR) over a captured stream, returning the IR events.</summary>
    private static List<SseItem<string>> RunT3(List<SseItem<string>> responsesStream, string model = "gpt-5.3-codex")
    {
        var sm = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in responsesStream) ir.AddRange(sm.Translate(e));
        ir.AddRange(sm.Flush());
        return ir;
    }

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6_T3_ProducesValidAnthropicGrammar(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var ir = RunT3(responses);

        var types = ir.Select(EventType).ToList();
        _output.WriteLine($"{fixture}: T3 IR events = {string.Join(", ", types)}");

        // Anthropic grammar: message_start first, message_stop last, balanced blocks.
        Assert.Equal("message_start", types[0]);
        Assert.Equal("message_stop", types[^1]);
        Assert.Contains("message_delta", types);
        Assert.Equal(types.Count(t => t == "content_block_start"),
                     types.Count(t => t == "content_block_stop"));
        // No [DONE] leaked in.
        Assert.DoesNotContain(ir, e => e.Data.Contains("[DONE]", StringComparison.Ordinal));
    }

    [Fact]
    public void A6_ToolStream_T3_PreservesToolCallAndArgs()
    {
        var responses = ParseSse(Path.Combine(FixturesDir, "responses-sse-toolcall.txt"));
        var ir = RunT3(responses);

        // A tool_use content_block_start must appear, and input_json_delta(s).
        Assert.Contains(ir, e => e.Data.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal));
        Assert.Contains(ir, e => e.Data.Contains("input_json_delta", StringComparison.Ordinal));
        // The terminal stop_reason should be tool_use (a tool was called).
        var delta = ir.First(e => EventType(e) == "message_delta");
        Assert.Contains("tool_use", delta.Data);
    }

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6_FullRoundTrip_T3ThenT4_BackToResponses(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var ir = RunT3(responses);

        // T4: IR → Responses.
        var t4 = new AnthropicToResponsesStream("gpt-5.3-codex");
        var roundTripped = new List<SseItem<string>>();
        foreach (var e in ir) roundTripped.AddRange(t4.Translate(e));
        roundTripped.AddRange(t4.Flush());

        var types = roundTripped.Select(EventType).ToList();
        _output.WriteLine($"{fixture}: T3→T4 Responses events = {string.Join(", ", types)}");

        // Must open with response.created and end with response.completed (Codex
        // parser requirement), and never emit [DONE].
        Assert.Equal("response.created", types[0]);
        Assert.Equal("response.completed", types[^1]);
        Assert.DoesNotContain(roundTripped, e => e.Data.Contains("[DONE]", StringComparison.Ordinal));
    }

    // ── A6c — usage cache/reasoning sub-counts survive T3→T4 ──────────────────
    // CONTRACT: the Codex /responses path is a near-passthrough, so the usage
    // Codex sees on response.completed must equal Copilot's upstream usage —
    // including input_tokens_details.cached_tokens (drives Codex's prompt-cache
    // telemetry) and output_tokens_details.reasoning_tokens. The Responses→IR→
    // Responses hub round-trip previously dropped both (T3 never captured them,
    // T4 hard-coded 0), so Codex showed 0% cache even when Copilot served the
    // whole prefix from cache. The real fixtures carry reasoning_tokens=32/8.

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6c_Usage_CacheAndReasoning_RoundTripEqualsUpstream(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var upstream = ExtractCompletedUsage(responses);
        var emitted = ExtractCompletedUsage(RunT3ThenT4(responses));

        Assert.Equal(upstream.Input, emitted.Input);
        Assert.Equal(upstream.Output, emitted.Output);
        Assert.Equal(upstream.Cached, emitted.Cached);        // was forced to 0 (the bug)
        Assert.Equal(upstream.Reasoning, emitted.Reasoning);  // was forced to 0 (the bug)
        Assert.Equal(upstream.Total, emitted.Total);
        Assert.True(upstream.Reasoning > 0, "fixture should carry reasoning_tokens (guards the assertion)");
    }

    [Fact]
    public void A6c_HighCacheHit_CachedTokensSurvive()
    {
        // Mirror a real multi-turn Codex trace: Copilot served ~99.9% of the
        // prompt from cache (input_tokens=109589, cached_tokens=109440). Codex's
        // terminal must carry that, not cached_tokens=0.
        var emitted = ExtractCompletedUsage(
            RunT3ThenT4(SyntheticCompletedStream(input: 109589, cached: 109440, output: 315, reasoning: 0)));

        Assert.Equal(109589, emitted.Input);
        Assert.Equal(109440, emitted.Cached);   // the cache hit Codex was blind to
        Assert.Equal(315, emitted.Output);
        Assert.Equal(0, emitted.Reasoning);
        Assert.Equal(109904, emitted.Total);    // input + output; cached is a subset, not added
    }

    [Fact]
    public void A6c_MissingUsageDetails_DefaultToZero_NoInvention()
    {
        // Copilot omits input_tokens_details/output_tokens_details on a turn with
        // no cache hit and no reasoning. The round trip must not crash on the
        // absent sub-objects and must NOT invent counts — cached/reasoning = 0,
        // totals preserved.
        var emitted = ExtractCompletedUsage(
            RunT3ThenT4(SyntheticTerminalStream("response.completed", "completed",
                input: 2000, cached: 0, output: 10, reasoning: 0, withDetails: false)));

        Assert.Equal(2000, emitted.Input);
        Assert.Equal(0, emitted.Cached);
        Assert.Equal(10, emitted.Output);
        Assert.Equal(0, emitted.Reasoning);
        Assert.Equal(2010, emitted.Total);
    }

    [Fact]
    public void A6c_IncompleteTerminal_CacheSurvivesAndStatusIncomplete()
    {
        // A max_tokens turn arrives as response.incomplete (a different T3 branch
        // than response.completed) — the cache sub-count must still survive, and
        // T4 must mark the terminal status incomplete (not completed).
        var stream = RunT3ThenT4(SyntheticTerminalStream("response.incomplete", "incomplete",
            input: 50000, cached: 49000, output: 256, reasoning: 64));

        var usage = ExtractCompletedUsage(stream);
        Assert.Equal(50000, usage.Input);
        Assert.Equal(49000, usage.Cached);    // cache hit survives the incomplete path too
        Assert.Equal(64, usage.Reasoning);
        Assert.Equal(50256, usage.Total);
        Assert.Equal("incomplete", ExtractTerminalStatus(stream));
    }

    /// <summary>Run T3 (Responses→IR) then T4 (IR→Responses), returning the re-emitted stream.</summary>
    private static List<SseItem<string>> RunT3ThenT4(List<SseItem<string>> responsesStream, string model = "gpt-5.3-codex")
    {
        var ir = RunT3(responsesStream, model);
        var t4 = new AnthropicToResponsesStream(model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());
        return outp;
    }

    /// <summary>Minimal Responses SSE carrying only the terminal usage (no output items).</summary>
    private static List<SseItem<string>> SyntheticCompletedStream(long input, long cached, long output, long reasoning) =>
        SyntheticTerminalStream("response.completed", "completed", input, cached, output, reasoning);

    /// <summary>
    /// Minimal Responses SSE (response.created + a terminal) carrying only usage.
    /// <paramref name="withDetails"/>=false omits the *_details sub-objects, the
    /// shape Copilot sends for a turn with no cache hit / no reasoning.
    /// </summary>
    private static List<SseItem<string>> SyntheticTerminalStream(
        string terminalType, string status, long input, long cached, long output, long reasoning, bool withDetails = true)
    {
        var usage = withDetails
            ? $"{{\"input_tokens\":{input},\"input_tokens_details\":{{\"cached_tokens\":{cached}}},"
              + $"\"output_tokens\":{output},\"output_tokens_details\":{{\"reasoning_tokens\":{reasoning}}},"
              + $"\"total_tokens\":{input + output}}}"
            : $"{{\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{input + output}}}";
        return
        [
            new SseItem<string>("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new SseItem<string>(
                $"{{\"type\":\"{terminalType}\",\"response\":{{\"id\":\"r\",\"status\":\"{status}\",\"usage\":{usage}}}}}",
                terminalType),
        ];
    }

    /// <summary>Read the status string off the terminal event (completed/incomplete/failed).</summary>
    private static string ExtractTerminalStatus(List<SseItem<string>> stream)
    {
        foreach (var e in stream)
        {
            if (EventType(e) is not ("response.completed" or "response.incomplete" or "response.failed")) continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("status", out var s))
                return s.GetString() ?? "";
        }
        return "";
    }

    /// <summary>Pull the usage off the terminal response.completed/incomplete/failed event.</summary>
    private static (long Input, long Cached, long Output, long Reasoning, long Total) ExtractCompletedUsage(
        List<SseItem<string>> stream)
    {
        static long Scalar(JsonElement o, string p) =>
            o.TryGetProperty(p, out var v) && v.TryGetInt64(out var n) ? n : 0;
        static long Nested(JsonElement o, string p, string c) =>
            o.TryGetProperty(p, out var d) && d.ValueKind == JsonValueKind.Object ? Scalar(d, c) : 0;

        foreach (var e in stream)
        {
            if (EventType(e) is not ("response.completed" or "response.incomplete" or "response.failed")) continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (!doc.RootElement.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object) continue;
            return (
                Scalar(usage, "input_tokens"),
                Nested(usage, "input_tokens_details", "cached_tokens"),
                Scalar(usage, "output_tokens"),
                Nested(usage, "output_tokens_details", "reasoning_tokens"),
                Scalar(usage, "total_tokens"));
        }
        throw new Xunit.Sdk.XunitException("no terminal event with response.usage found");
    }

    [Fact]
    public void A6_TextDeltas_ConcatenateToIdenticalText()
    {
        // The text the IR carries (T3) must equal the text the original Responses
        // stream carried — delta concatenation is lossless.
        var responses = ParseSse(Path.Combine(FixturesDir, "responses-sse-text.txt"));
        var originalText = ConcatResponsesText(responses);

        var ir = RunT3(responses);
        var irText = ConcatIrText(ir);

        Assert.Equal(originalText, irText);
        Assert.False(string.IsNullOrEmpty(originalText), "fixture carried no text deltas");
    }

    private static string ConcatResponsesText(List<SseItem<string>> stream)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in stream)
        {
            if (EventType(e) != "response.output_text.delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                sb.Append(d.GetString());
        }
        return sb.ToString();
    }

    private static string ConcatIrText(List<SseItem<string>> ir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in ir)
        {
            if (EventType(e) != "content_block_delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("delta", out var d)
                && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                && d.TryGetProperty("text", out var tx))
                sb.Append(tx.GetString());
        }
        return sb.ToString();
    }

    private static string EventType(SseItem<string> e)
    {
        // Prefer the data's "type" (authoritative), fall back to the SSE event field.
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("type", out var t)) return t.GetString() ?? e.EventType ?? "";
        }
        catch (JsonException) { }
        return e.EventType ?? "";
    }
}
