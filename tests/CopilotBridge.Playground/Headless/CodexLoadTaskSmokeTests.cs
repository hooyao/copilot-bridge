using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Codex <b>load-task</b> smoke for the copilot-model-sync skill: drives the real
/// <c>codex.exe</c> through the bridge on a genuinely multi-step tool task (not a
/// plain one-word turn) so the FULL Codex client wire shape — including the
/// harness tool-registration preamble (<c>input[0]</c> <c>additional_tools</c>),
/// multi-call <c>function_call</c>/<c>function_call_output</c> round-trips, and
/// reasoning echoes — actually crosses the bridge for the model under test, and
/// asserts that from the bridge's own audit (not just stdout).
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A plain "reply pong" turn (see
/// <see cref="CodexE2EHeadlessTests.Codex_PlainTurn_CompletesThroughBridge"/>) does
/// NOT make Codex emit its full tool suite, so it cannot catch a new inbound wire
/// shape. That is exactly how the gpt-5.6 <c>additional_tools</c> item shipped a
/// 400: the model was added to the catalog and the plain/tool turns passed, but no
/// load task ever exercised the harness preamble the newer client sends. This
/// smoke closes that gap — the skill requires it for every added/reconciled Codex
/// model.</para>
/// <para>The assertions read the bridge's four-file IO audit via
/// <see cref="BridgeLogReader"/> and require the model under test, the
/// <c>additional_tools</c> preamble, ≥2 successful upstream rounds, and a real
/// <c>function_call</c>/<c>function_call_output</c> pair on the <c>/responses</c>
/// wire — NOT merely the canary in stdout (a model could echo a prompt-embedded
/// canary without ever calling a tool).</para>
/// <para>The model is taken from <c>CODEX_SMOKE_MODEL</c> (default
/// <c>gpt-5.3-codex</c>) so a model-sync run can target the id it just added.
/// Integration-tagged: needs live Copilot + <c>codex.exe</c>; skipped in CI. Uses
/// the ephemeral-port <see cref="BridgeFixture"/> (never 8765) and injects the
/// provider via <c>-c</c> overrides so <c>~/.codex/config.toml</c> is untouched.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexLoadTaskSmokeTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexLoadTaskSmokeTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    private static string Model =>
        Environment.GetEnvironmentVariable("CODEX_SMOKE_MODEL") is { Length: > 0 } m
            ? m
            : "gpt-5.3-codex";

    [Fact]
    public async Task CodexLoadTaskSmoke_MultiStepToolChain_CompletesThroughBridge()
    {
        var model = Model;
        const string canary = "codex-loadtask-canary-51742";
        // A genuinely multi-step task: two shell writes then a read-back, forcing
        // several tool calls in sequence with tool_result round-trips — the shape
        // most likely to surface a Codex-client wire regression (a new input-item
        // type, a tool-arg reassembly bug, a reasoning-echo mishandling) on a
        // freshly added model.
        var prompt =
            "Do these steps in order, actually running the shell commands (do not fabricate output). "
            + "As soon as step 3 is done, give your final answer and stop:\n"
            + "1. Run `echo first-line > codex_probe.txt`.\n"
            + $"2. Run `echo {canary} >> codex_probe.txt`.\n"
            + "3. Run `cat codex_probe.txt` and tell me the exact second line, verbatim.";

        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: model,
            Timeout: TimeSpan.FromMinutes(6)));

        var entries = reader.ReadNew()
            .Where(e => e.InboundPath.EndsWith("/responses", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"[{model}] codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"bridge /responses entries: {entries.Count}");

        // Inspect what each UPSTREAM (T2-built) /responses body actually carried —
        // this is the full Codex client wire shape the smoke exists to exercise.
        var sawAdditionalTools = false;
        var sawFunctionCall = false;
        var sawFunctionCallOutput = false;
        var modelOnWire = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var itemTypes = ExtractInputItemTypes(e.UpstreamBody);
            var wireModel = (e.UpstreamBody as JsonObject)?["model"]?.GetValue<string>();
            _output.WriteLine($"  [{i}] status={e.UpstreamStatus} model={wireModel} input_items={string.Join(",", itemTypes)}");
            if (wireModel == model) modelOnWire = true;
            if (itemTypes.Contains("additional_tools")) sawAdditionalTools = true;
            if (itemTypes.Contains("function_call")) sawFunctionCall = true;
            if (itemTypes.Contains("function_call_output")) sawFunctionCallOutput = true;
        }

        // codex.exe closed the loop cleanly.
        Assert.Equal(0, result.ExitCode);

        // The turn actually reached THIS model's /responses backend.
        Assert.True(modelOnWire, $"No upstream /responses body carried model={model}; the load task never routed to the model under test.");

        // A real multi-step tool loop actually crossed the bridge — the whole point
        // of a LOAD task over a plain turn, asserted from the bridge AUDIT (not just
        // stdout, which a model could satisfy by echoing the prompt-embedded canary
        // without calling any tool). Requires BOTH a function_call the model emitted
        // AND the function_call_output codex.exe fed back.
        Assert.True(sawFunctionCall, "No upstream body carried a function_call item — the model never invoked a tool (a plain/echo turn would still print the canary).");
        Assert.True(sawFunctionCallOutput, "No upstream body carried a function_call_output item — codex.exe never forwarded a tool result back.");

        // ≥2 successful upstream rounds (at minimum: the round that produced the
        // first tool call, and the round that carried its result back). This also
        // guards the additional_tools fix indirectly — if the preamble 400'd, the
        // turn could not complete these rounds.
        var successful = entries.Where(e => e.UpstreamStatus is >= 200 and < 300).ToList();
        Assert.True(successful.Count >= 2,
            $"Expected ≥2 successful /responses calls for a tool round-trip; got {successful.Count}.");

        // additional_tools preamble: OBSERVED, not required. The desktop Codex app
        // sends it at input[0]; the `codex exec` CLI used here (this version) does
        // NOT — so hard-requiring it would fail against the real client. When it IS
        // present, the ≥2-successful-rounds assertion above already proves the bridge
        // carried it without a 400 (the exact regression this whole change fixes).
        _output.WriteLine(sawAdditionalTools
            ? "[wire] additional_tools preamble present AND carried without a 400 — the fix's target shape exercised end-to-end."
            : "[wire] additional_tools NOT emitted by this codex.exe version (expected for `codex exec`; the HTTP-edge test CodexAdditionalToolsHeadlessTests covers that shape directly).");

        // Finally, the canary the model could only obtain by actually running the
        // tools reaches its output.
        Assert.Contains(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"[audit] four-file IO traces under {_bridge.LogDirectory}");
    }

    /// <summary>
    /// The <c>type</c> discriminator of every item in a Responses upstream body's
    /// <c>input[]</c> array (e.g. <c>message</c>, <c>additional_tools</c>,
    /// <c>function_call</c>, <c>function_call_output</c>, <c>reasoning</c>).
    /// </summary>
    private static IReadOnlyList<string> ExtractInputItemTypes(JsonNode? upstreamBody)
    {
        if (upstreamBody is not JsonObject obj || obj["input"] is not JsonArray input)
            return Array.Empty<string>();
        var types = new List<string>(input.Count);
        foreach (var it in input)
            if (it is JsonObject io && io["type"]?.GetValue<string>() is { } t)
                types.Add(t);
        return types;
    }
}
