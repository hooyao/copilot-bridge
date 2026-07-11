using System.Runtime.Versioning;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Live probe for the REQUEST-side custom-tool echo: how does Copilot's native
/// <c>/responses</c> want a prior <b>custom (grammar) tool</b> call replayed in a
/// follow-up turn? Codex echoes an `exec` call to the bridge as a
/// <c>function_call</c> whose <c>arguments</c> is RAW JavaScript (not JSON), which
/// currently 400s the bridge at T1 (`ExpectedStartOfValueNotFound`). The fix must
/// re-emit it upstream in whatever shape Copilot accepts — this probe finds that
/// shape instead of guessing.
/// </summary>
/// <remarks>
/// Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~CustomToolEcho" --logger "console;verbosity=detailed"</code>
/// Read the "→ HTTP N" lines. We test three candidate echo shapes for a prior exec
/// call whose input was raw JS:
///   (A) function_call + arguments = raw JS string
///   (B) function_call + arguments = the JS wrapped as a JSON string value
///   (C) custom_tool_call + input = raw JS  (the native custom shape, symmetric to output)
/// Whichever 200s is how the bridge must re-emit an echoed custom-tool call.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CustomToolEchoProbe
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public CustomToolEchoProbe(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // A realistic exec tool call input (raw JavaScript, NOT JSON).
    private const string ExecJs = "const r = await tools.shell_command({ command: \"echo hi\" }); text(r);";

    private const string ToolsBlock = """
      "tools":[{"type":"custom","name":"exec","description":"Run JS.","format":{"type":"grammar","syntax":"lark","definition":"start: /[\\s\\S]+/"}}]
      """;

    [Fact]
    public async Task CustomToolEcho_A_FunctionCall_RawJsArguments()
    {
        // exec produced a call last turn; echo it as function_call with raw-JS args,
        // then a function_call_output, then ask a trivial follow-up.
        var body = $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You have an exec tool.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"run echo hi"}]},
              {"type":"function_call","name":"exec","call_id":"call_echo_1","arguments":{{Json(ExecJs)}}},
              {"type":"function_call_output","call_id":"call_echo_1","output":"hi"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"good, now say done"}]}
            ],
            "stream":false,"store":false,
            {{ToolsBlock}}
          }
          """;
        await Probe("A function_call+raw-JS-arguments", body);
    }

    [Fact]
    public async Task CustomToolEcho_C_CustomToolCall_RawInput()
    {
        // Echo as the native custom_tool_call shape (symmetric to how Copilot emits it):
        // item type custom_tool_call, input = raw JS.
        var body = $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You have an exec tool.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"run echo hi"}]},
              {"type":"custom_tool_call","name":"exec","call_id":"call_echo_1","input":{{Json(ExecJs)}}},
              {"type":"function_call_output","call_id":"call_echo_1","output":"hi"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"good, now say done"}]}
            ],
            "stream":false,"store":false,
            {{ToolsBlock}}
          }
          """;
        await Probe("C custom_tool_call+raw-input", body);
    }

    [Fact]
    public async Task CustomToolEcho_C2_CustomToolCall_WithCustomOutput()
    {
        // Same as C but the result comes back as custom_tool_call_output (if that's the
        // paired result shape) rather than function_call_output.
        var body = $$"""
          {
            "model":"gpt-5.6-sol",
            "instructions":"You have an exec tool.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"run echo hi"}]},
              {"type":"custom_tool_call","name":"exec","call_id":"call_echo_1","input":{{Json(ExecJs)}}},
              {"type":"custom_tool_call_output","call_id":"call_echo_1","output":"hi"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"good, now say done"}]}
            ],
            "stream":false,"store":false,
            {{ToolsBlock}}
          }
          """;
        await Probe("C2 custom_tool_call+custom_tool_call_output", body);
    }

    private async Task Probe(string label, string body)
    {
        using var client = new PlaygroundClient();
        var (status, resp) = await client.TryPostResponsesAsync(body);
        _output.WriteLine($"[{label}] → {(int)status} {status}");
        _output.WriteLine($"  body: {(resp.Length <= 400 ? resp : resp[..400])}");
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
