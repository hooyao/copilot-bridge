using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Boots the bridge as a REAL subprocess — <c>copilot-bridge.exe serve --port N</c>
/// — the way a user runs it, so a behavior test exercises the actual CLI-arg /
/// <c>appsettings.json</c>-binding / routing-validation path (not an in-process
/// <see cref="BridgeFixture"/> that bypasses those layers). This is the deliberate
/// difference from the in-process fixture: the flywheel's whole point is that a
/// green bridge is not enough, so the bridge under test must be the one a user
/// actually starts.
/// </summary>
/// <remarks>
/// <para><b>Why a scratch base dir + in-place patch.</b> The bridge loads
/// <c>appsettings.json</c> from <see cref="AppContext.BaseDirectory"/> (the exe's own
/// directory), and there is no environment-variable knob to point it at a different
/// routing config: <c>WebApplication.CreateSlimBuilder</c> does register an env-var
/// source, but <c>BridgeConfigurationExtensions.AddBridgeAppSettings</c> re-layers the
/// rebased <c>appsettings.json</c> ON TOP of it, so the JSON always wins. To run a
/// scenario with a DIFFERENT routing config without mutating the checked-in
/// <c>src/CopilotBridge.Cli/appsettings.json</c>, we copy the build output into a
/// per-run scratch dir and <b>patch the copied appsettings in place</b> — flipping
/// <c>Tracing.Enabled</c> on and, for the cc-to-gpt scenario, promoting the bundled
/// <c>_Locations_disabled</c> route to active <c>Locations</c>. Patching the real file
/// (rather than shipping a hand-written duplicate) keeps the whole <c>Pipeline</c>
/// block — runaway/leak guards, detectors — byte-identical to production, so the
/// flywheel can never drift from what ships. The GitHub token is found via the store's
/// <c>~/github_token.dat</c> fallback (same Windows user → DPAPI decrypts), so it need
/// not be copied.</para>
/// <para><b>Readiness.</b> Startup logs go to the console; Serilog routes everything
/// at/above Verbose to <b>stderr</b> (<c>SerilogBootstrapper</c> —
/// <c>standardErrorFromLevel: Verbose</c>), so we watch stderr for the "listening on
/// http://localhost:&lt;port&gt;" line. A startup failure (bad config, auth, port
/// clash) exits the process non-zero WITHOUT that line —
/// <c>FatalErrorHandler</c> skips its keypress pause when stdin is redirected, so the
/// subprocess never hangs.</para>
/// <para><b>Port.</b> Always an OS-assigned free loopback port, never 8765 (the
/// user's real bridge). Reserved via a transient socket bind, same technique as
/// <see cref="BridgeFixture"/>.</para>
/// </remarks>
internal sealed record ServeInvocation(
    ServeScenario Scenario,
    TimeSpan? ReadyTimeout = null);

/// <summary>
/// The appsettings shape a behavior run needs. Each value is applied by patching the
/// copied production <c>appsettings.json</c> — so only the delta from production is
/// expressed here, and everything else (detectors, timeouts) stays as shipped.
/// </summary>
internal enum ServeScenario
{
    /// <summary>No routing rewrites (empty <c>Locations</c>), tracing on. Native /cc
    /// (Claude Code) and native /codex (Codex CLI) scenarios.</summary>
    Passthrough,

    /// <summary>The <c>claude-opus-4.8 → gpt-5.6-sol</c> location active (promoted from
    /// the shipped <c>_Locations_disabled</c> example), tracing on. The CC→gpt leg.</summary>
    CcToGpt,
}

/// <summary>
/// A running bridge subprocess. <see cref="BaseUrl"/> is the root (callers append
/// <c>/cc</c> or <c>/codex</c>); <see cref="TraceDir"/> is where the per-request
/// four-file audit is written (tracing is forced on in every scenario). Dispose to
/// stop the process and (best-effort) delete the scratch dir.
/// </summary>
internal sealed class ServeHandle : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _scratchDir;
    private readonly StringBuilder _stderrTail;

    public string BaseUrl { get; }
    public string TraceDir { get; }

    /// <summary>The last slice of the subprocess's stderr — the startup banner and
    /// any fatal message. Surfaced so a thin actuator test can attach it on failure
    /// instead of the operator hunting the rolling log.</summary>
    public string StderrTail
    {
        get { lock (_stderrTail) return _stderrTail.ToString(); }
    }

    internal ServeHandle(Process process, string scratchDir, string baseUrl, string traceDir,
        StringBuilder stderrTail)
    {
        _process = process;
        _scratchDir = scratchDir;
        BaseUrl = baseUrl;
        TraceDir = traceDir;
        _stderrTail = stderrTail;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await Task.Run(() => _process.WaitForExit(5000));
            }
        }
        catch { /* best effort */ }
        finally
        {
            _process.Dispose();
        }

        // Best-effort scratch cleanup. A locked log/trace file (still-flushing sink)
        // must not fail the test — the OS temp dir is reclaimed anyway.
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best effort */ }
    }
}

internal static class ServeProcess
{
    /// <summary>
    /// Copy the bridge build output to a scratch dir, patch the copied appsettings for
    /// the scenario, and launch <c>copilot-bridge.exe serve --port &lt;free&gt;</c>.
    /// Returns once the process logs it is listening. Throws
    /// <see cref="ServeStartupException"/> (with the captured stderr) if it exits or
    /// times out before then — so a caller sees the bridge's own fatal message, not a
    /// bare timeout.
    /// </summary>
    public static async Task<ServeHandle> StartAsync(ServeInvocation inv, CancellationToken ct = default)
    {
        var buildOutputDir = LocateBuildOutputDir();
        var exe = Path.Combine(buildOutputDir, "copilot-bridge.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"Bridge exe not found at {exe}. Build src/CopilotBridge.Cli first (dotnet build).");

        // Per-run scratch base dir = a copy of the build output. AppContext.BaseDirectory
        // then points here, so the real src/CopilotBridge.Cli/appsettings.json is never
        // touched — we patch the COPY.
        var scratchDir = Path.Combine(
            Path.GetTempPath(), "cbridge-serve-" + Guid.NewGuid().ToString("N"));

        // Trace to a STABLE dir OUTSIDE the scratch tree so the four-file audit — the
        // agent's evidence — survives this handle disposing the scratch dir when the
        // bridge stops. Lives under the solution's test output, one dir per run.
        var traceDir = Path.Combine(EvidenceRoot(), "serve-" + Guid.NewGuid().ToString("N"));

        // Everything from the copy onward can throw — CopyDirectory itself (an AV lock,
        // disk-full, a stale locked file), a drifted config (PatchAppSettings), a
        // locked/quarantined exe (proc.Start), a lost port race, the readiness wait
        // timing out, OR the CALLER's ct cancelling. Any of those must clean up the
        // scratch dir, the (empty) trace dir, and a half-started process — otherwise a
        // cancelled or failed start leaks a partial scratch dir, an orphaned bridge
        // (holding its port), and temp dirs. So run the whole copy+setup+wait under one
        // guard.
        Process? proc = null;
        try
        {
            CopyDirectory(buildOutputDir, scratchDir);
            Directory.CreateDirectory(traceDir);
            PatchAppSettings(Path.Combine(scratchDir, "appsettings.json"), inv.Scenario, traceDir);

            var scratchExe = Path.Combine(scratchDir, "copilot-bridge.exe");
            var port = GetFreeLoopbackPort();
            var baseUrl = $"http://localhost:{port}";

            var psi = new ProcessStartInfo
            {
                FileName = scratchExe,
                WorkingDirectory = scratchDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // redirected stdin → FatalErrorHandler skips its keypress pause
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());

            proc = new Process { StartInfo = psi };
            var stderrTail = new StringBuilder();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyMarker = $"listening on http://localhost:{port}";

            // Serilog writes the readiness banner to stderr. Watch it line-by-line;
            // signal ready on the marker, and keep a bounded tail for diagnostics.
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderrTail)
                {
                    stderrTail.AppendLine(e.Data);
                    if (stderrTail.Length > 8192)
                        stderrTail.Remove(0, stderrTail.Length - 8192);
                }
                if (e.Data.Contains(readyMarker, StringComparison.Ordinal))
                    ready.TrySetResult(true);
            };
            // Drain stdout so the pipe buffer can't fill and stall the process.
            proc.OutputDataReceived += (_, _) => { };

            proc.EnableRaisingEvents = true;
            // The process exiting before "listening" means startup failed — unblock the
            // wait so we don't sit until the timeout on a dead process.
            proc.Exited += (_, _) => ready.TrySetResult(false);

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var timeout = inv.ReadyTimeout ?? TimeSpan.FromSeconds(90);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            bool becameReady;
            try
            {
                becameReady = await ready.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our own readiness timeout (not the caller's ct). Kill + clean here so we
                // can attach the bridge's captured stderr; the outer catch handles ct.
                await KillQuietly(proc);
                TryDeleteDir(scratchDir);
                TryDeleteDir(traceDir);
                throw new ServeStartupException(
                    $"Bridge did not log '{readyMarker}' within {timeout}. Stderr:\n{Snapshot(stderrTail)}");
            }

            if (!becameReady)
            {
                // Process exited before readiness — surface its own fatal message. Drain
                // the async stderr first: after an exit, the buffered ErrorDataReceived
                // lines (which include the fatal banner) are only guaranteed flushed once
                // the parameterless WaitForExit() returns. Without this the exception can
                // carry an empty/truncated Stderr on the very path meant to diagnose it.
                try { proc.WaitForExit(); } catch { /* already gone */ }
                await KillQuietly(proc);
                TryDeleteDir(scratchDir);
                TryDeleteDir(traceDir);
                throw new ServeStartupException(
                    $"Bridge process exited (code {SafeExitCode(proc)}) before listening. Stderr:\n{Snapshot(stderrTail)}");
            }

            return new ServeHandle(proc, scratchDir, baseUrl, traceDir, stderrTail);
        }
        catch (Exception ex) when (ex is not ServeStartupException)
        {
            // Any other abnormal exit before we handed ownership to a ServeHandle — the
            // caller's ct cancelling the readiness wait, PatchAppSettings on a drifted
            // config, a locked/quarantined exe failing proc.Start, a lost port race.
            // Clean up so we never leak an orphaned bridge (holding its port) or temp dirs.
            if (proc is not null) await KillQuietly(proc);
            TryDeleteDir(scratchDir);
            TryDeleteDir(traceDir);
            throw;
        }
    }

    /// <summary>
    /// Patch the copied production <c>appsettings.json</c> to the scenario shape: force
    /// <c>Tracing.Enabled=true</c> always; for <see cref="ServeScenario.CcToGpt"/>,
    /// promote the shipped <c>_Locations_disabled</c> example to the active
    /// <c>Locations</c> array. Everything else (the whole <c>Pipeline</c> block) is
    /// left exactly as production ships it, so a behavior run never drifts from the
    /// real detector/timeout config. Throws if the source shape the patch depends on
    /// is missing (a drifted appsettings should fail loudly, not silently run the
    /// wrong scenario).
    /// </summary>
    private static void PatchAppSettings(string path, ServeScenario scenario, string traceDir)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new ServeStartupException($"appsettings.json at {path} is not a JSON object.");

        // Tracing on, pointed at a stable absolute dir OUTSIDE the scratch tree — the
        // agent's verdict reads the four-file audit these produce, and it must outlive
        // the scratch dir this run deletes on stop.
        var tracing = root["Tracing"]?.AsObject()
            ?? throw new ServeStartupException("appsettings.json has no Tracing section to enable.");
        tracing["Enabled"] = true;
        tracing["Directory"] = traceDir;

        if (scenario == ServeScenario.CcToGpt)
        {
            var routing = root["Routing"]?.AsObject()
                ?? throw new ServeStartupException("appsettings.json has no Routing section.");
            var disabled = routing["_Locations_disabled"]?.AsArray()
                ?? throw new ServeStartupException(
                    "CcToGpt scenario needs Routing._Locations_disabled (the claude-opus-4.8 → "
                    + "gpt-5.6-sol example) in appsettings.json, but it is absent — the shipped "
                    + "config drifted. Restore the disabled example or update the scenario.");
            // Move the disabled example into the active Locations slot (deep-clone so
            // we don't share nodes across the tree).
            routing["Locations"] = JsonNode.Parse(disabled.ToJsonString());
        }

        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            // Skip the bridge's own runtime output subtrees if a prior JIT run left them
            // in the build dir: they're irrelevant to a fresh scenario run, they bloat the
            // copy, and — worse — a file under log/ or request-traces/ can be locked by a
            // bridge instance started from this same bin dir, which would make File.Copy
            // throw mid-copy. (The scenario's own trace dir is elsewhere, so nothing here
            // is needed.)
            var firstSeg = rel.Replace('\\', '/').Split('/', 2)[0];
            if (firstSeg is "log" or "logs" or "request-traces") continue;

            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Walk up from the test bin dir to the bridge's build output
    /// (<c>src/CopilotBridge.Cli/bin/&lt;Config&gt;/net10.0/</c>). Prefers the config
    /// this test assembly was built in (Debug/Release) so we run a matching bridge
    /// build; falls back to whichever exists.
    /// </summary>
    private static string LocateBuildOutputDir()
    {
        var config =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cliBin = Path.Combine(dir.FullName, "src", "CopilotBridge.Cli", "bin");
            if (Directory.Exists(cliBin))
            {
                foreach (var cfg in new[] { config, "Release", "Debug" })
                {
                    var candidate = Path.Combine(cliBin, cfg, "net10.0");
                    if (File.Exists(Path.Combine(candidate, "copilot-bridge.exe")))
                        return candidate;
                }
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CopilotBridge.Cli/bin/<Config>/net10.0/copilot-bridge.exe. "
            + "Build the CLI project first.");
    }

    /// <summary>
    /// Stable root for behavior-run evidence (bridge traces + run manifests), OUTSIDE
    /// any per-run scratch dir so it survives the bridge stopping. Under the repo at
    /// <c>&lt;repo&gt;/tests/behavior-runs/</c> (gitignored) when the repo is found;
    /// else the OS temp dir. The skill's verdict agent reads manifests from here.
    /// </summary>
    internal static string EvidenceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Repo root marker: a solution file (this repo ships CopilotBridge.slnx, the
            // XML solution format — NOT .sln) or a .git entry next to tests/. .git is a
            // DIRECTORY in a normal clone but a FILE in a git worktree, so accept either.
            var hasTests = Directory.Exists(Path.Combine(dir.FullName, "tests"));
            var hasSln = File.Exists(Path.Combine(dir.FullName, "CopilotBridge.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CopilotBridge.sln"));
            var gitPath = Path.Combine(dir.FullName, ".git");
            var hasGit = Directory.Exists(gitPath) || File.Exists(gitPath);
            if (hasTests && (hasSln || hasGit))
            {
                var root = Path.Combine(dir.FullName, "tests", "behavior-runs");
                Directory.CreateDirectory(root);
                return root;
            }
            dir = dir.Parent;
        }
        var fallback = Path.Combine(Path.GetTempPath(), "cbridge-behavior-runs");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task KillQuietly(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await Task.Run(() => proc.WaitForExit(5000));
            }
        }
        catch { /* best effort */ }
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch { return -1; }
    }

    private static string Snapshot(StringBuilder sb)
    {
        lock (sb) return sb.ToString();
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Thrown when the bridge subprocess fails to reach its listening state, or the
/// scenario patch cannot be applied. Carries the captured stderr / reason so the
/// failure is diagnosable from the bridge's own message rather than a bare timeout.
/// </summary>
internal sealed class ServeStartupException : Exception
{
    public ServeStartupException(string message) : base(message) { }
}
