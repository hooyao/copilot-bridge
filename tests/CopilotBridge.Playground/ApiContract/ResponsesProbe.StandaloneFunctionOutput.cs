using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Backend contract for Codex 0.153.3 standalone named
/// <c>function_call_output</c> items (openai/codex #39782). These are external
/// tool events injected into thread history without a preceding model tool call,
/// so absence of <c>call_id</c> is intentional and <c>name</c> is the identity.
/// </summary>
public partial class ResponsesProbe
{
    [Theory]
    [MemberData(nameof(StandaloneNamedFunctionOutputModels))]
    public async Task StandaloneNamedFunctionOutput_AcceptanceMatrix(
        string model, bool expectedAccepted)
    {
        var payload = $$"""
          {
            "model":"{{model}}",
            "instructions":"Acknowledge the external tool event with exactly: ok",
            "input":[
              {
                "type":"function_call_output",
                "name":"notifications",
                "namespace":"codex_app",
                "output":"A scheduled notification fired."
              },
              {
                "type":"message",
                "role":"user",
                "content":[{"type":"input_text","text":"Acknowledge now."}]
              }
            ],
            "stream":false,
            "store":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            $"{model} standalone named function output");
        var accepted = WireAcceptance.IsAccepted(
            status, body, $"{model} standalone named function output");

        _output.WriteLine($"[{model}] standalone named output → {(int)status} accepted={accepted}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
        Assert.Equal(expectedAccepted, accepted);
    }

    public static IEnumerable<object[]> StandaloneNamedFunctionOutputModels() =>
    [
        ["gpt-5.3-codex", true],
        ["gpt-5.4-mini", true],
        ["gpt-5.4", true],
        ["gpt-5.5", true],
        ["gpt-5.6-luna", true],
        ["gpt-5.6-sol", false],
        ["gpt-5.6-sol-fast", true],
        ["gpt-5.6-terra", true],
        ["gpt-5-mini", false],
        ["mai-code-1-flash-picker", false],
    ];

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5-mini")]
    [InlineData("mai-code-1-flash-picker")]
    public async Task StandaloneNamedFunctionOutput_SyntheticCallId_AcceptanceMatrix(string model)
    {
        var payload = $$"""
          {
            "model":"{{model}}",
            "instructions":"Reply with exactly the external event text and nothing else.",
            "input":[
              {
                "type":"function_call_output",
                "call_id":"call_bridge_standalone_probe",
                "name":"notifications",
                "namespace":"codex_app",
                "output":"STANDALONE_EVENT_CANARY_9264"
              },
              {
                "type":"message",
                "role":"user",
                "content":[{"type":"input_text","text":"Report the external event."}]
              }
            ],
            "stream":false,
            "store":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            $"{model} standalone named output with synthetic compatibility call id");
        var accepted = WireAcceptance.IsAccepted(
            status, body, $"{model} standalone named output with synthetic compatibility call id");

        _output.WriteLine($"[{model}] standalone + synthetic call_id → {(int)status} accepted={accepted}");
        _output.WriteLine($"  body: {Truncate(body, 900)}");
        Assert.False(accepted,
            $"{model} now accepts the synthetic call-id workaround; re-evaluate the no-rewrite policy.");
    }

    [Fact]
    public async Task StandaloneNamedFunctionOutput_DesktopHeartbeatShape_IsAccepted()
    {
        const string payload = """
          {
            "model":"gpt-5.6-sol-fast",
            "instructions":"Treat heartbeat XML as an external tool event and reply exactly: ok",
            "input":[
              {
                "type":"message",
                "id":"msg_standalone_probe_developer",
                "role":"developer",
                "content":[{"type":"input_text","text":"External tool events may be followed by a user turn."}]
              },
              {
                "type":"function_call_output",
                "id":"fco_standalone_probe_1",
                "name":"automation_update",
                "namespace":"codex_app",
                "output":"<heartbeat><automation_id>probe</automation_id><current_time_iso>2026-09-05T12:29:27.790Z</current_time_iso><instructions>Reply exactly: ok</instructions></heartbeat>"
              },
              {
                "type":"function_call_output",
                "id":"fco_standalone_probe_2",
                "name":"automation_update",
                "namespace":"codex_app",
                "output":"<heartbeat><automation_id>probe</automation_id><current_time_iso>2026-09-05T12:32:27.790Z</current_time_iso><instructions>Reply exactly: ok</instructions></heartbeat>"
              },
              {
                "type":"message",
                "id":"msg_standalone_probe_user",
                "role":"user",
                "content":[{"type":"input_text","text":"Continue after the injected events."}]
              }
            ],
            "reasoning":{"effort":"low","context":"all_turns"},
            "include":["reasoning.encrypted_content"],
            "stream":false,
            "store":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            "gpt-5.6-sol-fast captured Desktop standalone heartbeat shape");
        var accepted = WireAcceptance.IsAccepted(
            status, body, "gpt-5.6-sol-fast captured Desktop standalone heartbeat shape");

        _output.WriteLine($"[gpt-5.6-sol-fast] captured heartbeat shape → {(int)status} accepted={accepted}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
        Assert.True(accepted,
            $"Copilot rejected the captured Desktop standalone heartbeat shape: {WireAcceptance.ErrorMessage(body)}");
    }
}
