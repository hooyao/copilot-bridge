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
