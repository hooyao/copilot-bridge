using System.Runtime.Versioning;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Live probe for the REQUEST-side <b>namespaced-tool</b> round-trip — the gpt-5.6
/// collaboration/MCP feature that produced the production error
/// <c>Missing namespace for function_call 'list_agents'. It does not exist in the
/// default namespace. Round-trip the model's function_call item with its namespace
/// field included.</c>
/// </summary>
/// <remarks>
/// <para>Grounded in the AUTHORITATIVE spec, not guessed from the error:</para>
/// <list type="bullet">
///   <item>openai/codex <c>codex-rs/core/tests/common/responses.rs</c> —
///   <c>ev_function_call_with_namespace</c> / <c>ev_custom_tool_call_with_namespace</c>
///   put a top-level <c>"namespace"</c> string on the <c>output_item.done</c> item,
///   sibling to <c>call_id</c>/<c>name</c>.</item>
///   <item>vercel/ai SDK CHANGELOG: a <c>tool_search</c>-dispatched tool's
///   <c>function_call</c> carries <c>namespace</c>; the read side stores it under
///   <c>providerMetadata.openai.namespace</c> and the write side MUST re-emit it or
///   multi-turn 400s with the exact error above.</item>
/// </list>
/// <para>This probe confirms what the bridge needs from Copilot directly:</para>
/// <list type="number">
///   <item><b>N1</b> — a follow-up turn that echoes a prior namespaced
///   <c>function_call</c> WITH <c>"namespace":"collaboration"</c> is accepted (200).</item>
///   <item><b>N0</b> — the SAME turn with the <c>namespace</c> field REMOVED
///   reproduces the 400 (proves the field is load-bearing, not incidental).</item>
/// </list>
/// The tools[] registry mirrors the real capture: a <c>{"type":"namespace",
/// "name":"collaboration","tools":[...]}</c> wrapper around the function defs
/// (the shape codex's <c>namespace_child_tool</c> helper walks).
/// Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~NamespacedToolEcho" --logger "console;verbosity=detailed"</code>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class NamespacedToolEchoProbe
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NamespacedToolEchoProbe(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // A namespace-wrapped tool registry: the collaboration namespace holding a
    // list_agents function (mirrors the real gpt-5.6 capture shape that
    // codex's namespace_child_tool() walks: type:"namespace" → tools:[function...]).
    private const string NamespacedToolsBlock = """
      "tools":[{"type":"namespace","name":"collaboration","description":"Multi-agent collaboration tools.","tools":[{"type":"function","name":"list_agents","description":"List live agents.","strict":false,"parameters":{"type":"object","properties":{},"additionalProperties":false}}]}]
      """;

    [Fact]
    public async Task N1_FunctionCallEcho_WithNamespace_IsAccepted()
    {
        // Echo a prior collaboration.list_agents call WITH its namespace field, then
        // a result, then a trivial follow-up — the exact round-trip codex performs.
        var body = $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You can list agents via the collaboration namespace.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"list the agents"}]},
              {"type":"function_call","name":"list_agents","namespace":"collaboration","call_id":"call_ns_1","arguments":"{}"},
              {"type":"function_call_output","call_id":"call_ns_1","output":"no agents"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"ok, now say done"}]}
            ],
            "stream":false,"store":false,
            {{NamespacedToolsBlock}}
          }
          """;
        await Probe("N1 function_call WITH namespace", body, expected: System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task N0_FunctionCallEcho_WithoutNamespace_ViaTopLevelToolsAlsoAccepted()
    {
        // The SAME turn with the namespace field REMOVED. IMPORTANT FINDING: when the
        // namespaced tool is registered via the TOP-LEVEL tools[] (as here), Copilot does
        // NOT enforce the namespace round-trip — this echo is accepted (200) too. The
        // production 400 ("Missing namespace") only occurs when the tool is registered via
        // the `additional_tools` developer preamble; that path is covered by
        // NamespaceRealReplayProbe (real bytes: verbatim → 400, +namespace → 200). This
        // probe documents the top-level-tools path so the difference is explicit, not
        // assumed.
        var body = $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You can list agents via the collaboration namespace.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"list the agents"}]},
              {"type":"function_call","name":"list_agents","call_id":"call_ns_1","arguments":"{}"},
              {"type":"function_call_output","call_id":"call_ns_1","output":"no agents"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"ok, now say done"}]}
            ],
            "stream":false,"store":false,
            {{NamespacedToolsBlock}}
          }
          """;
        await Probe("N0 function_call WITHOUT namespace (top-level tools → accepted)", body,
            expected: System.Net.HttpStatusCode.OK);
    }

    private async Task Probe(string label, string body, System.Net.HttpStatusCode expected)
    {
        using var client = new PlaygroundClient();
        var (status, resp) = await client.TryPostResponsesAsync(body);
        _output.WriteLine($"[{label}] → {(int)status} {status}");
        _output.WriteLine($"  body: {(resp.Length <= 500 ? resp : resp[..500])}");
        Assert.Equal(expected, status);
    }
}
