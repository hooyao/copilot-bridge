using Xunit;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Shared plumbing for the <b>client-behavior flywheel</b> tests. These are
/// deliberately THIN xUnit "actuators": each starts a real bridge subprocess
/// (<see cref="ServeProcess"/>) with a scenario config, drives a real headless
/// client (<see cref="CodexProcess"/> / <see cref="ClaudeProcess"/>) on a task
/// chosen to exercise a specific code path, captures the evidence, and writes a
/// <see cref="BehaviorManifest"/>. The xUnit assertions cover only the HARNESS
/// CONTRACT — bridge came up, the client actually ran to completion (not a timeout /
/// missing-exe), and evidence was captured. They intentionally do NOT assert the
/// client executed the tool correctly: that verdict is the job of the
/// <c>real-client-verify</c> skill's agent, which reads the client's OWN dispatch
/// log (codex <c>logs_2.sqlite</c> / claude transcript). Encoding the semantic
/// verdict in xUnit is exactly what let three green-but-broken gpt-5.6 releases ship.
/// </summary>
/// <remarks>
/// <para><b>Latest-models policy.</b> This suite targets only the newest Claude and
/// gpt ids (agent-client behavior, not an LLM-API matrix). The constants here are the
/// single place to bump when Copilot ships a newer model; catalog work still goes
/// through the <c>copilot-model-sync</c> skill.</para>
/// <para>Integration-tagged (needs live Copilot + the client exe) and further tagged
/// <c>Kind=ClientBehavior</c> so <c>dotnet test --filter "Kind=ClientBehavior"</c>
/// runs exactly the flywheel's real-client leg. CI still skips the whole file via the
/// <c>Category=Integration</c> trait.</para>
/// </remarks>
internal static class ClientBehaviorSupport
{
    /// <summary>Newest Claude id under behavior test (native <c>/cc</c>).</summary>
    public const string LatestClaude = "claude-opus-4.8";

    /// <summary>Newest gpt (Codex) id under behavior test (<c>/codex</c> and the
    /// CC→gpt route target).</summary>
    public const string LatestGpt = "gpt-5.6-sol";

    /// <summary>Filename-safe UTC stamp for manifest/IO filenames. Test code, so a
    /// direct clock read is fine (unlike workflow scripts).</summary>
    public static string Stamp() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

    /// <summary>
    /// The harness-contract assertions every behavior test shares: the bridge bound a
    /// port (implied by a non-null handle), the client actually ran (its trace dir
    /// exists and holds at least one audit file — i.e. it reached the bridge), and the
    /// manifest was written. A timeout or missing-exe throws before this and reddens
    /// the test; a client that ran but produced a broken tool call still passes here
    /// (the agent judges that from the client log the manifest points at).
    /// </summary>
    public static void AssertHarnessProducedEvidence(string traceDir, string manifestPath)
    {
        Assert.True(File.Exists(manifestPath), $"run manifest not written: {manifestPath}");
        Assert.True(Directory.Exists(traceDir), $"bridge trace dir missing: {traceDir}");
        var traceFiles = Directory.GetFiles(traceDir, "*.json");
        Assert.True(traceFiles.Length > 0,
            $"bridge wrote no per-request audit under {traceDir} — the client never reached the bridge "
            + "(a harness failure, not a client-behavior finding).");
    }
}
