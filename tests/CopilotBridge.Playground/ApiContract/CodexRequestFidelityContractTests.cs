using System.Runtime.Versioning;
using CopilotBridge.Playground.Contract;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Live destination contract for the native request-fidelity allowlist. Payloads
/// are de-identified/minimized from the 2026-08-07 Codex Desktop 0.147 captures;
/// each test changes one acceptance axis and must go red when Copilot drifts.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexRequestFidelityContractTests
{
    private readonly ITestOutputHelper _output;

    public CodexRequestFidelityContractTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Gpt56_AllTurnsDeveloperPositionAndValidMetadata_AreStillAccepted()
    {
        const string payload = """
          {
            "model":"gpt-5.6-sol",
            "input":[
              {"type":"message","role":"developer","id":"msg_dev_probe",
               "content":[{"type":"input_text","text":"Reply briefly."}]},
              {"type":"message","role":"assistant","phase":"commentary","status":"completed",
               "content":[{"type":"output_text","text":"Working."}]},
              {"type":"message","role":"user","id":"msg_user_probe",
               "content":[{"type":"input_text","text":"Reply with exactly: ok"}]}
            ],
            "reasoning":{"effort":"xhigh","summary":"detailed","context":"all_turns"},
            "store":false,
            "stream":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            "gpt-5.6 valid request metadata");
        var accepted = WireAcceptance.IsAccepted(status, body, "gpt-5.6 valid request metadata");
        _output.WriteLine($"all_turns + metadata → {(int)status} accepted={accepted}");
        Assert.True(accepted, "Copilot stopped accepting all_turns/developer/valid metadata; reconcile request fidelity.");
    }

    [Fact]
    public async Task Gpt56_NativeFunctionOutputArray_IsStillAccepted()
    {
        // Minimized from 20260807-053800-0207. The only load-bearing axis is
        // function_call_output.output retaining its native input_text[] array.
        const string payload = """
          {
            "model":"gpt-5.6-sol",
            "instructions":"Use the prior tool result and reply exactly: ok",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Run probe"}]},
              {"type":"function_call","call_id":"call_probe_array","name":"probe_tool","arguments":"{}"},
              {"type":"function_call_output","id":"fco_probe_array","call_id":"call_probe_array","output":[
                {"type":"input_text","text":"first"},
                {"type":"input_text","text":"second"}
              ]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Reply now"}]}
            ],
            "tools":[{"type":"function","name":"probe_tool","description":"Probe.","strict":false,
              "parameters":{"type":"object","properties":{},"required":[],"additionalProperties":false}}],
            "tool_choice":"auto",
            "reasoning":{"effort":"xhigh","context":"all_turns"},
            "store":false,
            "stream":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            "gpt-5.6 native function output array");
        var accepted = WireAcceptance.IsAccepted(status, body, "gpt-5.6 native function output array");
        _output.WriteLine($"native output array → {(int)status} accepted={accepted}");
        Assert.True(accepted, "Copilot stopped accepting native output arrays; reconsider the preservation contract.");
    }

    [Fact]
    public async Task Gpt56_NonMessageIdentity_IsStillRejected()
    {
        const string payload = """
          {
            "model":"gpt-5.6-sol",
            "input":[
              {"type":"message","role":"assistant","id":"item_0","phase":"commentary",
               "content":[{"type":"output_text","text":"Working."}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Reply: ok"}]}
            ],
            "reasoning":{"effort":"xhigh","context":"all_turns"},
            "store":false,
            "stream":false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload),
            "gpt-5.6 invalid message id");
        var accepted = WireAcceptance.IsAccepted(status, body, "gpt-5.6 invalid message id");
        _output.WriteLine($"item_0 id → {(int)status} accepted={accepted}");
        Assert.False(accepted,
            "Copilot now accepts item_0 as a message id; protocol.message_id coercion is stale and must be removed.");
        Assert.Contains("begins with 'msg'", body, StringComparison.OrdinalIgnoreCase);
    }
}
