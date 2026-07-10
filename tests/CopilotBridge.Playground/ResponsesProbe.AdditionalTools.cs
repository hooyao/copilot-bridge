using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Live probes for the <c>input[]</c> item shape <c>{"type":"additional_tools",
/// "role":"developer","tools":[…]}</c> that the 2026-07 Codex CLI (gpt-5.6-sol
/// under <c>model_reasoning_effort=max</c>) started emitting — the one the bridge
/// currently 400s on at INBOUND deserialization ("Polymorphism_Unrecognized
/// TypeDiscriminator, additional_tools Path: $.input[0]"). See the desktop
/// capture <c>request-traces/20260710-145459-0001</c>.
/// </summary>
/// <remarks>
/// <para>The load-bearing question these answer: the bridge's own
/// <c>ResponsesInputItem</c> union doesn't model <c>additional_tools</c>, so the
/// request dies at T1 before Copilot is ever contacted. The FIX shape depends
/// entirely on what Copilot's native <c>/responses</c> does with this item:</para>
/// <list type="bullet">
///   <item><b>Upstream 200</b> → Copilot understands <c>additional_tools</c>
///         natively; the bridge should model it and carry it through (faithful
///         passthrough via the openai bag), NOT invent a translation.</item>
///   <item><b>Upstream 400</b> → Copilot rejects the item; the bridge must
///         translate it (hoist the nested <c>tools</c> to the top-level
///         <c>tools[]</c> array the Responses API defines) or drop it. The
///         top-level-tools probes below tell us whether hoisting is even legal for
///         these nested shapes (custom/grammar, namespace).</item>
/// </list>
/// <para>Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~AdditionalTools_" --logger "console;verbosity=detailed"</code>
/// Read the "→ HTTP N" lines: 200 = accepted, 400 = rejected. Probes only LOG.</para>
/// <para>Raw string literals here are plain (no <c>$$</c> interpolation) because
/// the nested tool JSON carries literal <c>}}</c> runs that collide with brace
/// escaping; the model id is a literal in each payload.</para>
/// </remarks>
public partial class ResponsesProbe
{
    /// <summary>
    /// The decisive probe: send the exact <c>additional_tools</c> input item the
    /// real capture carries (one nested function tool, minimal), and see whether
    /// Copilot's native <c>/responses</c> accepts it as an <c>input[]</c> element.
    /// </summary>
    [Fact]
    public async Task AdditionalTools_AsInputItem_MinimalFunction()
    {
        const string payload = """
          {
            "model": "gpt-5.6-sol",
            "instructions": "Reply with exactly: ok",
            "input": [
              {
                "type": "additional_tools",
                "role": "developer",
                "tools": [
                  {"type":"function","name":"wait","description":"Wait.","strict":false,
                   "parameters":{"type":"object","properties":{"cell_id":{"type":"string"}},"required":["cell_id"],"additionalProperties":false}}
                ]
              },
              {"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}
            ],
            "stream": false,
            "store": false
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[gpt-5.6-sol] additional_tools(input, function) → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 500)}");
    }

    /// <summary>
    /// Same input-item probe but with the full nested tool zoo from the capture:
    /// a <c>custom</c> tool with a Lark <c>grammar</c> format (exec), a
    /// <c>function</c> tool, and a <c>namespace</c> tool wrapping more functions
    /// (collaboration). If the minimal probe 200s but this 400s, a specific nested
    /// shape is the culprit; if both behave the same, the item type itself decides.
    /// </summary>
    [Fact]
    public async Task AdditionalTools_AsInputItem_FullZoo()
    {
        const string payload = """
          {
            "model": "gpt-5.6-sol",
            "instructions": "Reply with exactly: ok",
            "input": [
              {
                "type": "additional_tools",
                "role": "developer",
                "tools": [
                  {"type":"custom","name":"exec","description":"Run JS.",
                   "format":{"type":"grammar","syntax":"lark","definition":"start: /.+/"}},
                  {"type":"function","name":"wait","description":"Wait.","strict":false,
                   "parameters":{"type":"object","properties":{"cell_id":{"type":"string"}},"required":["cell_id"],"additionalProperties":false}},
                  {"type":"namespace","name":"collaboration","description":"Sub-agents.",
                   "tools":[
                     {"type":"function","name":"spawn_agent","description":"Spawn.","strict":false,
                      "parameters":{"type":"object","properties":{"task_name":{"type":"string"},"message":{"type":"string"}},"required":["task_name","message"],"additionalProperties":false}}
                   ]}
                ]
              },
              {"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}
            ],
            "stream": false,
            "store": false
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[gpt-5.6-sol] additional_tools(input, full zoo) → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 800)}");
    }

    /// <summary>
    /// Fallback-strategy probe: put the SAME nested tools at the top-level
    /// <c>tools[]</c> array (the Responses-native place for tools) instead of
    /// inside an <c>additional_tools</c> input item. This tells us whether the
    /// bridge could translate the item by hoisting — and whether the exotic
    /// nested shapes (custom/grammar, namespace) are themselves accepted at the
    /// top level. Runs each tool shape in isolation so a single rejecting shape is
    /// pinpointed, not masked by a sibling.
    /// </summary>
    [Theory]
    [InlineData("function", """{"type":"function","name":"wait","description":"Wait.","strict":false,"parameters":{"type":"object","properties":{"cell_id":{"type":"string"}},"required":["cell_id"],"additionalProperties":false}}""")]
    [InlineData("custom_grammar", """{"type":"custom","name":"exec","description":"Run JS.","format":{"type":"grammar","syntax":"lark","definition":"start: /.+/"}}""")]
    [InlineData("namespace", """{"type":"namespace","name":"collaboration","description":"Sub-agents.","tools":[{"type":"function","name":"spawn_agent","description":"Spawn.","strict":false,"parameters":{"type":"object","properties":{"task_name":{"type":"string"}},"required":["task_name"],"additionalProperties":false}}]}""")]
    public async Task AdditionalTools_HoistedToTopLevelTools(string label, string toolJson)
    {
        const string template = """
          {
            "model": "gpt-5.6-sol",
            "instructions": "You may use tools.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}],
            "stream": false,
            "tool_choice": "auto",
            "tools": [__TOOL__]
          }
          """;
        var payload = template.Replace("__TOOL__", toolJson);
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[gpt-5.6-sol] top-level tools({label}) → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 500)}");
    }
}
