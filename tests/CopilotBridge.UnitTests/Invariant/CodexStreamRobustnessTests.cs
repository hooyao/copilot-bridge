using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Streaming-robustness coverage for the PR-review fixes (C1/C2 + usage +
/// empty-stream). Complements <see cref="CodexStreamRoundTripTests"/> (which
/// covers the happy-path round trip) with the error/edge cases the reviewers
/// flagged as untested:
/// <list type="bullet">
///   <item>A6 CONTENT through T4 — text + tool args + call_id actually survive to
///         the re-emitted Responses stream, not just event types.</item>
///   <item><c>response.failed</c> from upstream becomes an HONEST
///         <c>response.failed</c> terminal (NOT a fake <c>response.completed</c>).</item>
///   <item><c>response.incomplete</c> → terminal <c>status:"incomplete"</c>.</item>
///   <item>An empty / unterminated upstream stream still yields a terminal
///         (Codex's parser requires one).</item>
///   <item>Usage from the upstream terminal reaches the re-emitted completed.</item>
/// </list>
/// All synthetic SSE here is hand-built but real-shaped (mirrors the live grammar
/// in the committed responses-sse-*.txt fixtures).
/// </summary>
public class CodexStreamRobustnessTests
{
    private const string Model = "gpt-5.3-codex";

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SseItem<string> Sse(string type, string data) => new(data, type);

    private static List<SseItem<string>> T3(IEnumerable<SseItem<string>> upstream)
    {
        var sm = new ResponsesToAnthropicStream(Model);
        var ir = new List<SseItem<string>>();
        foreach (var e in upstream) ir.AddRange(sm.Translate(e));
        ir.AddRange(sm.Flush());
        return ir;
    }

    private static List<SseItem<string>> T4(IEnumerable<SseItem<string>> ir)
    {
        var sm = new AnthropicToResponsesStream(Model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(sm.Translate(e));
        outp.AddRange(sm.Flush());
        return outp;
    }

    private static string TypeOf(SseItem<string> e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("type", out var t)) return t.GetString() ?? e.EventType ?? "";
        }
        catch (JsonException) { }
        return e.EventType ?? "";
    }

    // A minimal real-shaped text stream that says "Hello world".
    private static List<SseItem<string>> TextStream(long inTok = 7, long outTok = 3) =>
    [
        Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}"""),
        Sse("response.in_progress", """{"type":"response.in_progress","response":{"id":"r","status":"in_progress"}}"""),
        Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m0","role":"assistant","content":[]}}"""),
        Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"m0","output_index":0,"content_index":0,"delta":"Hello "}"""),
        Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"m0","output_index":0,"content_index":0,"delta":"world"}"""),
        Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m0"}}"""),
        Sse("response.completed", "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":" + inTok + ",\"output_tokens\":" + outTok + "}}}"),
    ];

    // ── A6 content through T4 ─────────────────────────────────────────────────

    [Fact]
    public void A6_TextContent_SurvivesT3ThenT4()
    {
        var outp = T4(T3(TextStream()));

        // Re-parse the emitted Responses stream: output_text.delta must concatenate
        // to the original text, and output_text.done must carry the full text.
        var deltaText = new StringBuilder();
        string? doneText = null;
        foreach (var e in outp)
        {
            if (TypeOf(e) == "response.output_text.delta")
            {
                using var d = JsonDocument.Parse(e.Data);
                deltaText.Append(d.RootElement.GetProperty("delta").GetString());
            }
            else if (TypeOf(e) == "response.output_text.done")
            {
                using var d = JsonDocument.Parse(e.Data);
                doneText = d.RootElement.GetProperty("text").GetString();
            }
        }
        Assert.Equal("Hello world", deltaText.ToString());
        Assert.Equal("Hello world", doneText);

        // Terminal is a clean completed (not failed), with the upstream usage.
        var last = outp[^1];
        Assert.Equal("response.completed", TypeOf(last));
        using var doc = JsonDocument.Parse(last.Data);
        var usage = doc.RootElement.GetProperty("response").GetProperty("usage");
        Assert.Equal(7, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(3, usage.GetProperty("output_tokens").GetInt64());
    }

    [Fact]
    public void A6_ToolCall_ArgsAndCallId_SurviveT3ThenT4()
    {
        const string callId = "call_xyz789";
        var upstream = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}"""),
            Sse("response.output_item.added", "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"" + callId + "\",\"name\":\"shell\",\"id\":\"fc0\"}}"),
            Sse("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","item_id":"fc0","output_index":0,"delta":"{\"cmd\":"}"""),
            Sse("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","item_id":"fc0","output_index":0,"delta":"\"ls\"}"}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc0"}}"""),
            Sse("response.completed", """{"type":"response.completed","response":{"id":"r","status":"completed"}}"""),
        };

        var outp = T4(T3(upstream));

        // function_call_arguments.delta fragments must concatenate to the original
        // JSON, and the .done event must carry the full arguments + name.
        var args = new StringBuilder();
        string? doneArgs = null, doneCallId = null;
        foreach (var e in outp)
        {
            var ty = TypeOf(e);
            if (ty == "response.function_call_arguments.delta")
            {
                using var d = JsonDocument.Parse(e.Data);
                args.Append(d.RootElement.GetProperty("delta").GetString());
            }
            else if (ty == "response.function_call_arguments.done")
            {
                using var d = JsonDocument.Parse(e.Data);
                doneArgs = d.RootElement.GetProperty("arguments").GetString();
            }
            else if (ty == "response.output_item.done")
            {
                using var d = JsonDocument.Parse(e.Data);
                if (d.RootElement.TryGetProperty("item", out var item)
                    && item.TryGetProperty("call_id", out var cid))
                    doneCallId = cid.GetString();
            }
        }
        Assert.Equal("""{"cmd":"ls"}""", args.ToString());
        Assert.Equal("""{"cmd":"ls"}""", doneArgs);
        Assert.Equal(callId, doneCallId);
    }

    // ── C1: response.failed → honest response.failed (not fake completed) ─────

    [Fact]
    public void ResponseFailed_BecomesHonestFailedTerminal_NotCompleted()
    {
        var upstream = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m0","role":"assistant","content":[]}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"m0","output_index":0,"content_index":0,"delta":"partial"}"""),
            Sse("response.failed", """{"type":"response.failed","response":{"id":"r","status":"failed","error":{"code":"server_error","message":"boom"}}}"""),
        };

        var outp = T4(T3(upstream));

        var terminal = outp[^1];
        Assert.Equal("response.failed", TypeOf(terminal));
        Assert.DoesNotContain(outp, e => TypeOf(e) == "response.completed");
        using var doc = JsonDocument.Parse(terminal.Data);
        Assert.Equal("failed", doc.RootElement.GetProperty("response").GetProperty("status").GetString());
    }

    // ── response.incomplete → terminal status incomplete ─────────────────────

    [Fact]
    public void ResponseIncomplete_MapsToIncompleteStatus()
    {
        var upstream = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m0","role":"assistant","content":[]}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"m0","output_index":0,"content_index":0,"delta":"cut"}"""),
            Sse("response.incomplete", """{"type":"response.incomplete","response":{"id":"r","status":"incomplete"}}"""),
        };

        var outp = T4(T3(upstream));

        var terminal = outp[^1];
        Assert.Equal("response.completed", TypeOf(terminal));
        using var doc = JsonDocument.Parse(terminal.Data);
        Assert.Equal("incomplete", doc.RootElement.GetProperty("response").GetProperty("status").GetString());
    }

    // ── empty / unterminated stream still yields a terminal (C2) ──────────────

    [Fact]
    public void EmptyUpstreamStream_T3_StillYieldsTerminal_ViaFlushTerminal()
    {
        // T3 saw nothing. The strategy's fault/empty path calls FlushTerminal,
        // which must synthesize a message_start + terminal so T4 can produce a
        // response.completed (Codex requires a terminal).
        var sm = new ResponsesToAnthropicStream(Model);
        var ir = new List<SseItem<string>>();
        ir.AddRange(sm.FlushTerminal(failed: false));

        Assert.NotEmpty(ir);
        Assert.Equal("message_start", TypeOf(ir[0]));
        Assert.Contains(ir, e => TypeOf(e) == "message_stop");

        var outp = T4(ir);
        Assert.Contains(outp, e => TypeOf(e) == "response.completed");
    }

    [Fact]
    public void FaultedUpstreamStream_FlushTerminalFailed_YieldsFailedTerminal()
    {
        // The strategy caught a mid-stream throw and called FlushTerminal(failed:true).
        var sm = new ResponsesToAnthropicStream(Model);
        var ir = new List<SseItem<string>>();
        // simulate a partial stream then a fault terminal
        ir.AddRange(sm.Translate(Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}""")));
        ir.AddRange(sm.FlushTerminal(failed: true));

        var outp = T4(ir);
        Assert.Equal("response.failed", TypeOf(outp[^1]));
        Assert.DoesNotContain(outp, e => TypeOf(e) == "response.completed");
    }

    // ── unparseable event is skipped without corrupting the rest ─────────────

    [Fact]
    public void UnparseableEvent_IsSkipped_RestOfStreamSurvives()
    {
        var upstream = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"r","status":"in_progress"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m0","role":"assistant","content":[]}}"""),
            new("{not valid json", "response.output_text.delta"),   // garbage data
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"m0","output_index":0,"content_index":0,"delta":"ok"}"""),
            Sse("response.completed", """{"type":"response.completed","response":{"id":"r","status":"completed"}}"""),
        };

        var outp = T4(T3(upstream));
        // The good delta survives; the stream still terminates cleanly.
        var text = new StringBuilder();
        foreach (var e in outp)
            if (TypeOf(e) == "response.output_text.delta")
            {
                using var d = JsonDocument.Parse(e.Data);
                text.Append(d.RootElement.GetProperty("delta").GetString());
            }
        Assert.Equal("ok", text.ToString());
        Assert.Equal("response.completed", TypeOf(outp[^1]));
    }
}
