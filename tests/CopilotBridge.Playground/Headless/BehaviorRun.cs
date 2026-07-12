using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Writes a <b>run manifest</b> — the seam between the thin xUnit actuator and the
/// skill's verdict agent. The behavior tests deliberately do NOT assert on wire shape
/// or tool execution (encoding that judgment in xUnit is exactly what let three
/// green-but-broken gpt-5.6 releases ship). Instead each test drives a real client on
/// a path-exercising task and records, in one predictable JSON file, WHERE the
/// evidence is: the bridge's four-file trace dir, the client's own dispatch log
/// (codex <c>logs_2.sqlite</c> in the real <c>~/.codex</c> — NOT under
/// <c>CODEX_HOME</c>; claude stdout transcript), the client exit code, and which
/// case/route this was. The <c>real-client-verify</c> skill reads these manifests,
/// opens each client's OWN log, and renders PASS/FAIL — because a bridge 200 says
/// nothing about whether the client could parse and execute what the bridge sent back.
/// </summary>
/// <remarks>
/// One manifest file per run at
/// <c>&lt;EvidenceRoot&gt;/manifests/&lt;caseId&gt;-&lt;utcStamp&gt;.json</c>. The
/// timestamp is supplied by the caller (the harness has the wall clock; this helper
/// stays deterministic) so filenames sort chronologically and never collide.
/// </remarks>
internal sealed record BehaviorManifest(
    string CaseId,
    string Client,        // "codex" | "claude"
    string Route,         // "/codex" | "/cc" | "/cc->gpt"
    string Model,
    ServeScenario Scenario, // the appsettings scenario the bridge ran under
    int ClientExitCode,
    double DurationSeconds,
    string TraceDir,      // bridge four-file audit (BridgeLogReader reads this)
    string? DispatchLogPath, // codex: real ~/.codex/logs_2.sqlite (codex logs there, NOT CODEX_HOME); null for claude
    long DispatchSinceUnix,  // codex: window logs_2.sqlite to this run (0 for claude)
    string Prompt);
    // NOTE: the saved-stdout/stderr file paths are NOT fields here — they are DERIVED by
    // BehaviorRun.Write from CaseId + utcStamp (the code that owns the write), and
    // returned to the caller via its out params. Putting them on the record invited a
    // footgun: a caller would pass "" and never see the real path on its own instance.

internal static class BehaviorRun
{
    /// <summary>
    /// Persist a client run's stdout/stderr next to the evidence and write the
    /// manifest. Returns the manifest file path (also emitted to the test output so a
    /// human can open it). <paramref name="utcStamp"/> must be a filename-safe UTC
    /// stamp like <c>20260712-153000</c> — passed in because the harness owns the
    /// clock. The saved stdout/stderr paths are returned via the out params (they are
    /// derived here, not supplied on <paramref name="manifest"/>).
    /// </summary>
    public static string Write(
        BehaviorManifest manifest, string stdout, string stderr, string utcStamp,
        out string stdoutPath, out string stderrPath)
    {
        var root = ServeProcess.EvidenceRoot();
        var runsDir = Path.Combine(root, "manifests");
        var ioDir = Path.Combine(root, "client-io");
        Directory.CreateDirectory(runsDir);
        Directory.CreateDirectory(ioDir);

        stdoutPath = Path.Combine(ioDir, $"{manifest.CaseId}-{utcStamp}.stdout.txt");
        stderrPath = Path.Combine(ioDir, $"{manifest.CaseId}-{utcStamp}.stderr.txt");
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, stderr);

        // Hand-built JsonObject (not source-gen) — this is test-only tooling, AOT rules
        // don't apply, and a plain object keeps the manifest human-readable/diffable.
        var obj = new JsonObject
        {
            ["caseId"] = manifest.CaseId,
            ["client"] = manifest.Client,
            ["route"] = manifest.Route,
            ["model"] = manifest.Model,
            ["scenario"] = manifest.Scenario.ToString(),
            ["clientExitCode"] = manifest.ClientExitCode,
            ["durationSeconds"] = manifest.DurationSeconds,
            ["traceDir"] = manifest.TraceDir,
            ["dispatchLogPath"] = manifest.DispatchLogPath,
            ["dispatchSinceUnix"] = manifest.DispatchSinceUnix == 0 ? null : manifest.DispatchSinceUnix,
            ["stdoutPath"] = stdoutPath,
            ["stderrPath"] = stderrPath,
            ["prompt"] = manifest.Prompt,
            ["utcStamp"] = utcStamp,
        };

        var manifestPath = Path.Combine(runsDir, $"{manifest.CaseId}-{utcStamp}.json");
        File.WriteAllText(manifestPath,
            obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return manifestPath;
    }
}
