using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the gpt-5.6 <b>namespaced-tool</b> round-trip. A tool in a
/// NON-default namespace (collaboration / MCP — e.g. <c>collaboration.list_agents</c>)
/// carries a <c>"namespace"</c> field on its Responses <c>function_call</c> item, and
/// the client MUST round-trip it back on echo or the next turn 400s:
/// <c>Missing namespace for function_call '&lt;name&gt;'. It does not exist in the default
/// namespace. Round-trip the model's function_call item with its namespace field
/// included.</c>
/// </summary>
/// <remarks>
/// <para>Contract source (NOT guessed from the error):</para>
/// <list type="bullet">
///   <item>openai/codex <c>codex-rs/core/tests/common/responses.rs</c> —
///   <c>ev_function_call_with_namespace</c> puts <c>"namespace"</c> on the
///   <c>output_item.done</c>/<c>.added</c> function_call item, sibling to
///   <c>call_id</c>/<c>name</c>.</item>
///   <item>vercel/ai SDK: read side stores it under
///   <c>providerMetadata.openai.namespace</c>; write side must re-emit it.</item>
///   <item>Live: <c>NamespaceRealReplayProbe</c> replays the real 400-ing request
///   verbatim (400) and with namespace injected (200).</item>
/// </list>
/// <para>Two directions, four tiers:</para>
/// <list type="number">
///   <item><b>Response side (T3→T4)</b>: Copilot's streamed function_call namespace
///   must reach the Codex-facing function_call item, so Codex learns it.</item>
///   <item><b>Request side (T1→T2)</b>: a Codex echo carrying <c>namespace</c> must
///   re-emit <c>"namespace"</c> on the upstream function_call.</item>
/// </list>
/// Each test is written to fail if the corresponding tier drops the field (the
/// mutation-check: delete the emit and the assertion reddens).
/// </remarks>
public class CodexNamespaceRoundTripTests
{
    // ── Response side: T3 captures namespace → T4 re-emits it to Codex ──────────

    [Fact]
    public void T3ThenT4_FunctionCallWithNamespace_ReachesCodexFacingItem()
    {
        // Copilot streams a collaboration.list_agents call WITH namespace. After the
        // T3→T4 hub round-trip, the Codex-facing function_call item MUST carry the
        // same namespace — else Codex never learns it and can't echo it next turn.
        var roundTripped = RunT3ThenT4(NamespacedFunctionToolStream(
            callId: "call_ns", ns: "collaboration", name: "list_agents", arguments: "{}"));

        var item = FindFunctionCallOutputItem(roundTripped);
        Assert.NotNull(item);
        Assert.Equal("collaboration", item!["namespace"]?.GetValue<string>());
        Assert.Equal("list_agents", item["name"]!.GetValue<string>());
    }

    [Fact]
    public void T3ThenT4_DefaultNamespaceTool_EmitsNoNamespaceField()
    {
        // A plain default-namespace function tool has NO namespace on the wire; the
        // round trip must NOT invent one (byte-identical to the pre-fix behavior).
        var roundTripped = RunT3ThenT4(NamespacedFunctionToolStream(
            callId: "call_plain", ns: null, name: "read_file", arguments: "{\"p\":\"x\"}"));

        var item = FindFunctionCallOutputItem(roundTripped);
        Assert.NotNull(item);
        Assert.False(item!.ContainsKey("namespace"),
            "a default-namespace tool must not carry a namespace field");
    }

    [Fact]
    public void T3_Namespace_NeverLeaksTheInternalMarkerToTheWire()
    {
        // The bridge-internal IR marker (bridge_tool_namespace) T3 stamps on the
        // tool_use content_block must NEVER reach the Codex-facing stream — T4 lifts
        // it onto the function_call item as the real `namespace` field and drops the
        // marker. Assert the marker string appears nowhere in the emitted output.
        var roundTripped = RunT3ThenT4(NamespacedFunctionToolStream(
            callId: "call_ns", ns: "collaboration", name: "list_agents", arguments: "{}"));

        foreach (var e in roundTripped)
            Assert.DoesNotContain("bridge_tool_namespace", e.Data, StringComparison.Ordinal);
    }

    // ── Request side: T1 preserves namespace → T2 re-emits it upstream ──────────

    [Fact]
    public void T1ThenT2_EchoedFunctionCallWithNamespace_ReEmitsNamespace()
    {
        // Codex echoes a prior collaboration.list_agents call WITH its namespace on a
        // follow-up turn. T1→T2 must re-emit "namespace" on the upstream function_call
        // — the fix for the production 400.
        var body = BuildEchoBody(callId: "call_e", ns: "collaboration", name: "list_agents", args: "{}");
        var emitted = CodexRoundTrip.RoundTrip(body).AsObject();

        var fc = FindUpstreamFunctionCall(emitted, "call_e");
        Assert.NotNull(fc);
        Assert.Equal("collaboration", fc!["namespace"]?.GetValue<string>());
        Assert.Equal("list_agents", fc["name"]!.GetValue<string>());
    }

    [Fact]
    public void T1ThenT2_EchoedFunctionCallWithoutNamespace_EmitsNoNamespace()
    {
        // A default-namespace echo carries no namespace; T2 must not add one.
        var body = BuildEchoBody(callId: "call_p", ns: null, name: "read_file", args: "{\"p\":\"x\"}");
        var emitted = CodexRoundTrip.RoundTrip(body).AsObject();

        var fc = FindUpstreamFunctionCall(emitted, "call_p");
        Assert.NotNull(fc);
        Assert.False(fc!.ContainsKey("namespace"),
            "a default-namespace echo must not gain a namespace field");
    }

    [Fact]
    public void FullBidirectional_NamespaceSurvivesResponseThenRequestEcho()
    {
        // The whole loop: Copilot emits a namespaced call (T3→T4 to Codex); Codex
        // echoes it back (T1→T2 to Copilot). The namespace must survive BOTH hops —
        // this is the end-to-end invariant the production bug violated.
        // Hop 1: response side — extract the namespace Codex would receive.
        var codexFacing = RunT3ThenT4(NamespacedFunctionToolStream(
            callId: "call_x", ns: "collaboration", name: "spawn_agent", arguments: "{\"task\":\"go\"}"));
        var received = FindFunctionCallOutputItem(codexFacing);
        Assert.Equal("collaboration", received!["namespace"]?.GetValue<string>());

        // Hop 2: request side — Codex echoes exactly what it received.
        var echoBody = BuildEchoBody(callId: "call_x", ns: "collaboration",
            name: "spawn_agent", args: "{\"task\":\"go\"}");
        var upstream = CodexRoundTrip.RoundTrip(echoBody).AsObject();
        var reEmitted = FindUpstreamFunctionCall(upstream, "call_x");
        Assert.Equal("collaboration", reEmitted!["namespace"]?.GetValue<string>());
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal Responses SSE for a FUNCTION tool call carrying an optional
    /// <c>namespace</c> on both the output_item.added and .done items (mirrors real
    /// Copilot: namespace present on both, per the 0009 capture).
    /// </summary>
    private static List<SseItem<string>> NamespacedFunctionToolStream(
        string callId, string? ns, string name, string arguments)
    {
        var nsField = ns is { Length: > 0 } ? $",\"namespace\":{Enc(ns)}" : "";
        string Item(string status, string args) =>
            $"{{\"type\":\"function_call\",\"id\":\"item_1\",\"call_id\":{Enc(callId)}{nsField},\"name\":{Enc(name)},\"arguments\":{Enc(args)},\"status\":\"{status}\"}}";
        return
        [
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new($"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{Item("in_progress", "")}}}",
                "response.output_item.added"),
            new($"{{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"delta\":{Enc(arguments)}}}",
                "response.function_call_arguments.delta"),
            new($"{{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"output_index\":0,\"arguments\":{Enc(arguments)}}}",
                "response.function_call_arguments.done"),
            new($"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{Item("completed", arguments)}}}",
                "response.output_item.done"),
            new("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
                "response.completed"),
        ];
    }

    private static List<SseItem<string>> RunT3ThenT4(List<SseItem<string>> responsesStream, string model = "gpt-5.6-sol")
    {
        var t3 = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in responsesStream) ir.AddRange(t3.Translate(e));
        ir.AddRange(t3.Flush());

        var t4 = new AnthropicToResponsesStream(model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());
        return outp;
    }

    private static JsonObject? FindFunctionCallOutputItem(List<SseItem<string>> stream)
    {
        foreach (var e in stream)
        {
            var node = JsonNode.Parse(e.Data)!.AsObject();
            if (node["type"]?.GetValue<string>() != "response.output_item.done") continue;
            if (node["item"] is JsonObject item
                && item["type"]?.GetValue<string>() == "function_call")
                return item;
        }
        return null;
    }

    /// <summary>A follow-up-turn body echoing a prior function_call, with optional namespace.</summary>
    private static string BuildEchoBody(string callId, string? ns, string name, string args)
    {
        var nsField = ns is { Length: > 0 } ? $"\"namespace\":{Enc(ns)}," : "";
        return $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You can use collaboration tools.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"go"}]},
              {"type":"function_call","call_id":{{Enc(callId)}},{{nsField}}"name":{{Enc(name)}},"arguments":{{Enc(args)}}},
              {"type":"function_call_output","call_id":{{Enc(callId)}},"output":"ok"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"done"}]}
            ],
            "stream":true,"store":false
          }
          """;
    }

    private static JsonObject? FindUpstreamFunctionCall(JsonObject emitted, string callId)
    {
        if (emitted["input"] is not JsonArray input) return null;
        foreach (var n in input)
            if (n is JsonObject o
                && o["type"]?.GetValue<string>() == "function_call"
                && o["call_id"]?.GetValue<string>() == callId)
                return o;
        return null;
    }

    private static string Enc(string s) => JsonSerializer.Serialize(s);
}
