using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the gpt-5.6 <b>custom_tool_call id</b> round-trip. Copilot's
/// <c>/responses</c> endpoint REQUIRES that a <c>custom_tool_call</c> item's
/// <c>id</c> — the one the client echoes back on a follow-up turn — begins with
/// <c>ctc</c>; otherwise the turn 400s with
/// <c>Invalid 'input[N].id': 'item_1'. Expected an ID that begins with 'ctc'.</c>
/// </summary>
/// <remarks>
/// <para>Contract source (NOT guessed from the code — from the wire + the error):</para>
/// <list type="bullet">
///   <item>Captured upstream first-turn response (request-traces 2026-07-14
///   <c>022622-0499</c>): the real custom_tool_call id is <c>ctc_0679bd5b…</c>, and it
///   appears ONLY on the <c>custom_tool_call_input.delta</c>/<c>.done</c> events'
///   <c>item_id</c> — the <c>output_item.added</c>/<c>.done</c> ids are rolling
///   encrypted blobs.</item>
///   <item>Production 400 (<c>034441</c>): the bridge echoed a synthesized
///   <c>item_1</c> id and Copilot rejected it — the exact string in the error.</item>
///   <item>Upstream checks the PREFIX only (its own output id is a fresh
///   <c>ctco_&lt;uuid&gt;</c> it accepts), so a conforming id need not equal the
///   original.</item>
/// </list>
/// The invariant under test: whatever custom_tool_call id the bridge hands Codex, it
/// MUST begin with <c>ctc</c> — because Codex echoes exactly that id next turn and
/// Copilot rejects any non-<c>ctc</c> id. Each test reddens if T4 reverts to the old
/// <c>item_N</c> id or drops the real captured id (the mutation-check).
/// </remarks>
public class CodexCustomToolCallIdRoundTripTests
{
    private const string Marker = "bridge_custom_tool_call_id";

    [Fact]
    public void T3ThenT4_RealCtcId_ReachesCodexFacingCompletedItem()
    {
        // Upstream streams a custom_tool_call whose real id (ctc_…) rides the
        // input.delta/.done item_id. After the T3→T4 round-trip the Codex-facing
        // COMPLETED custom_tool_call item MUST carry that exact id — it is what Codex
        // stores and echoes next turn, and Copilot requires it to begin with `ctc`.
        var roundTripped = RunT3ThenT4(CustomToolStream(
            callId: "call_abc", realCtcId: "ctc_0679bd5b187491ee", input: "const r = 1;"));

        var item = FindCustomToolCallItem(roundTripped, status: "completed");
        Assert.NotNull(item);
        Assert.Equal("ctc_0679bd5b187491ee", item!["id"]!.GetValue<string>());
        Assert.Equal("call_abc", item["call_id"]!.GetValue<string>());
    }

    [Fact]
    public void T3ThenT4_CompletedCustomToolCallId_AlwaysBeginsWithCtc()
    {
        // The core invariant: whatever the completed custom_tool_call id is, it must
        // begin with `ctc` (never the old `item_N`, which 400s the echo turn).
        var roundTripped = RunT3ThenT4(CustomToolStream(
            callId: "call_abc", realCtcId: "ctc_0679bd5b187491ee", input: "x"));

        var item = FindCustomToolCallItem(roundTripped, status: "completed");
        Assert.NotNull(item);
        Assert.StartsWith("ctc", item!["id"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void T3ThenT4_NoCtcIdObserved_StillEmitsCtcPrefixedId()
    {
        // Fallback path: upstream never surfaced a plaintext ctc_ id (the input events
        // carried no item_id). T4 must STILL emit a ctc-prefixed id (synthesized from
        // the call_id) — never `item_N`. This is the belt-and-suspenders guarantee
        // that a first-turn-only trace or a shape change can't reintroduce the 400.
        var roundTripped = RunT3ThenT4(CustomToolStream(
            callId: "call_xyz", realCtcId: null, input: "y"));

        var item = FindCustomToolCallItem(roundTripped, status: "completed");
        Assert.NotNull(item);
        Assert.StartsWith("ctc", item!["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("item_", item["id"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void T3ThenT4_Marker_NeverLeaksToTheCodexFacingWire()
    {
        // The bridge-internal marker (bridge_custom_tool_call_id) T3 stamps on the
        // content_block_stop must NEVER reach the Codex-facing stream — T4 lifts it
        // onto the item id and drops the marker. Assert it appears nowhere.
        var roundTripped = RunT3ThenT4(CustomToolStream(
            callId: "call_abc", realCtcId: "ctc_0679bd5b187491ee", input: "z"));

        foreach (var e in roundTripped)
            Assert.DoesNotContain(Marker, e.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void CcToGpt_Marker_ScrubbedBeforeReachingClaude()
    {
        // On the CC→gpt route there is NO T4 — ClaudeCodeOutboundAdapter is the client
        // edge. It must strip bridge_custom_tool_call_id from the content_block_stop
        // event so it never reaches claude.exe as bogus metadata.
        var ir = RunT3(CustomToolStream(
            callId: "call_abc", realCtcId: "ctc_0679bd5b187491ee", input: "w"));

        var scrubbed = ScrubForClaude(ir);

        // The marker is gone…
        foreach (var e in scrubbed)
            Assert.DoesNotContain(Marker, e.Data, StringComparison.Ordinal);
        // …but the content_block_stop event itself survives (only the key is dropped).
        Assert.Contains(scrubbed, e =>
            JsonNode.Parse(e.Data)?["type"]?.GetValue<string>() == "content_block_stop");
    }

    [Fact]
    public void CcToGpt_MarkerFreeStop_IsByteIdenticalPassthrough()
    {
        // A content_block_stop with no marker (a text block, or a real Anthropic
        // backend) must pass through untouched — the same instance, no re-serialization.
        var adapter = NewClaudeAdapter();
        var evt = new SseItem<string>("{\"type\":\"content_block_stop\",\"index\":0}", "content_block_stop");

        var outp = DrainStream(adapter, [evt]);

        Assert.Single(outp);
        Assert.Same(evt.Data, outp[0].Data);   // byte-identical, same reference
    }

    [Fact]
    public void T3ThenT4_NonCtcItemId_IsNotEchoed_CompletedIdStillCtc()
    {
        // The exact production failure mode: an input event's item_id that does NOT
        // begin with `ctc` (a synthetic or opaque token) must NEVER become the echoed
        // completed id — Copilot would 400 it ("Expected an ID that begins with 'ctc'").
        // T4 must fall back to a ctc-synthesized id instead of carrying the bad value.
        var roundTripped = RunT3ThenT4(CustomToolStream(
            callId: "call_xyz", realCtcId: "item_1", input: "q"));

        var item = FindCustomToolCallItem(roundTripped, status: "completed");
        Assert.NotNull(item);
        Assert.StartsWith("ctc", item!["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEqual("item_1", item["id"]!.GetValue<string>());
    }

    [Fact]
    public void T4_NonCtcMarkerOnBlockStop_IsRejected_CompletedIdStaysCtc()
    {
        // Defense in depth at the CONSUMPTION boundary: T4 must enforce the `ctc`
        // prefix on the marker itself, not trust T3 to have gated it. A malformed or
        // replayed IR event whose content_block_stop carries
        // bridge_custom_tool_call_id="item_1" must NOT override the safe synthesized id
        // — otherwise it reintroduces the exact follow-up 400 this change prevents.
        var ir = new List<SseItem<string>>
        {
            new("{\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"role\":\"assistant\",\"content\":[]}}", "message_start"),
            new("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_poison\",\"name\":\"exec\",\"input\":{},\"bridge_input_is_grammar_text\":true}}", "content_block_start"),
            new("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"x\"}}", "content_block_delta"),
            // Poisoned marker: a non-ctc id that must be rejected at T4.
            new("{\"type\":\"content_block_stop\",\"index\":0,\"bridge_custom_tool_call_id\":\"item_1\"}", "content_block_stop"),
            new("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}", "message_delta"),
            new("{\"type\":\"message_stop\"}", "message_stop"),
        };
        var t4 = new AnthropicToResponsesStream("gpt-5.6-sol");
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());

        var item = FindCustomToolCallItem(outp, status: "completed");
        Assert.NotNull(item);
        var id = item!["id"]!.GetValue<string>();
        Assert.StartsWith("ctc", id, StringComparison.Ordinal);
        Assert.NotEqual("item_1", id);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal Responses SSE for a CUSTOM (grammar) tool — exec — mirroring real
    /// Copilot: the output_item.added/.done ids are opaque (here a blob-ish string),
    /// and the plaintext ctc_ id rides the custom_tool_call_input.delta/.done item_id.
    /// When <paramref name="realCtcId"/> is null the input events carry NO item_id
    /// (the no-ctc fallback path).
    /// </summary>
    private static List<SseItem<string>> CustomToolStream(
        string callId, string? realCtcId, string input)
    {
        // The added/done item id is an opaque server token (NOT ctc-prefixed) — proves
        // the fix does not lean on the added-event id.
        const string addedId = "OPAQUEBLOB_added";
        const string doneId = "OPAQUEBLOB_done";
        var idField = realCtcId is { Length: > 0 } ? $",\"item_id\":{Enc(realCtcId)}" : "";
        string Item(string id, string status, string inp) =>
            $"{{\"type\":\"custom_tool_call\",\"id\":{Enc(id)},\"call_id\":{Enc(callId)},\"name\":\"exec\",\"input\":{Enc(inp)},\"status\":\"{status}\"}}";
        return
        [
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new($"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{Item(addedId, "in_progress", "")}}}",
                "response.output_item.added"),
            new($"{{\"type\":\"response.custom_tool_call_input.delta\",\"output_index\":0{idField},\"delta\":{Enc(input)}}}",
                "response.custom_tool_call_input.delta"),
            new($"{{\"type\":\"response.custom_tool_call_input.done\",\"output_index\":0{idField},\"input\":{Enc(input)}}}",
                "response.custom_tool_call_input.done"),
            new($"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{Item(doneId, "completed", input)}}}",
                "response.output_item.done"),
            new("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
                "response.completed"),
        ];
    }

    private static List<SseItem<string>> RunT3(List<SseItem<string>> responsesStream, string model = "gpt-5.6-sol")
    {
        var t3 = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in responsesStream) ir.AddRange(t3.Translate(e));
        ir.AddRange(t3.Flush());
        return ir;
    }

    private static List<SseItem<string>> RunT3ThenT4(List<SseItem<string>> responsesStream, string model = "gpt-5.6-sol")
    {
        var ir = RunT3(responsesStream, model);
        var t4 = new AnthropicToResponsesStream(model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());
        return outp;
    }

    private static JsonObject? FindCustomToolCallItem(List<SseItem<string>> stream, string status)
    {
        foreach (var e in stream)
        {
            var node = JsonNode.Parse(e.Data)!.AsObject();
            if (node["type"]?.GetValue<string>() != "response.output_item.done") continue;
            if (node["item"] is JsonObject item
                && item["type"]?.GetValue<string>() == "custom_tool_call"
                && item["status"]?.GetValue<string>() == status)
                return item;
        }
        return null;
    }

    private static ClaudeCodeOutboundAdapter NewClaudeAdapter() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    private static List<SseItem<string>> ScrubForClaude(List<SseItem<string>> ir) =>
        DrainStream(NewClaudeAdapter(), ir);

    private static List<SseItem<string>> DrainStream(
        ClaudeCodeOutboundAdapter adapter, List<SseItem<string>> input)
    {
        async IAsyncEnumerable<SseItem<string>> Source()
        {
            foreach (var e in input) { yield return e; await Task.Yield(); }
        }
        var outp = new List<SseItem<string>>();
        var e = adapter.AdaptStreamAsync(Source(), default).GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                outp.Add(e.Current);
        }
        finally { e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return outp;
    }

    private static string Enc(string s) => JsonSerializer.Serialize(s);
}
