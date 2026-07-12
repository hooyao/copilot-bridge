using System.Diagnostics;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Spawns the real <c>codex.exe exec --json</c> pointed at the bridge via a
/// custom model-provider (base_url=.../codex, wire_api=responses), injected
/// entirely through <c>-c</c> overrides so the user's <c>~/.codex/config.toml</c>
/// is never touched. Captures stdout (the JSONL turn events) / stderr / exit
/// code. The bridge presents as Copilot's <c>/responses</c> backend; auth to the
/// bridge is a dummy bearer (the bridge uses its own Copilot token upstream).
/// </summary>
internal sealed record CodexInvocation(
    string BridgeBaseUrl,   // e.g. http://127.0.0.1:5xxxx  (the /codex prefix is appended)
    string Prompt,
    string Model = "gpt-5.3-codex",
    TimeSpan? Timeout = null,
    // When set, codex uses THIS as CODEX_HOME (isolating its config/auth/state from the
    // user's real ~/.codex). NOTE: this does NOT relocate the dispatch log — codex
    // writes logs_2.sqlite to the REAL ~/.codex regardless of CODEX_HOME (verified: an
    // isolated home's logs_2.sqlite stays empty while ~/.codex gains the run's rows).
    // So the verdict reads ~/.codex/logs_2.sqlite filtered by the run's start time; see
    // CodexResult.DispatchLogPath / StartedUnixSeconds.
    string? CodexHome = null,
    // When set, codex runs with this as its working directory. Tasks that write relative
    // filenames MUST pass a disposable dir so the real client cannot create/overwrite
    // files in the test runner's CWD (the checkout). null = inherit the runner's dir.
    string? WorkingDirectory = null);

internal sealed record CodexResult(
    int ExitCode, string Stdout, string Stderr, TimeSpan Duration, string CodexHome,
    // The codex dispatch log (logs_2.sqlite) the verdict agent reads for the "did the
    // tool actually execute / any incompatible-payload fatal" signal — the REAL
    // ~/.codex copy, since codex logs there regardless of CODEX_HOME.
    string DispatchLogPath,
    // Unix-second window bounding THIS run's rows in the long-lived, shared ~/.codex
    // logs_2.sqlite. Both bounds are needed: a start-only window would also sweep in rows
    // from LATER runs (or a concurrent desktop codex), misattributing a later fatal to
    // this case. Started is stamped just before launch (minus slack); Ended just after
    // exit (plus slack).
    long StartedUnixSeconds,
    long EndedUnixSeconds);

internal static class CodexProcess
{
    private const string CodexExeEnv = "CODEX_EXE";
    // codex writes its dispatch log (logs_2.sqlite) to the real user Codex home, NOT to
    // CODEX_HOME. This is that path — the verdict source.
    private static readonly string RealDispatchLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "logs_2.sqlite");
    // Codex installs under %LOCALAPPDATA%\OpenAI\Codex\bin\<version-hash>\codex.exe
    // and self-updates into a fresh hash dir, so the ONLY stable parts are the
    // LocalApplicationData root and the "OpenAI\Codex\bin" suffix — never the user
    // name or the hash. ResolveCodexExe prefers CODEX_EXE, then the newest
    // codex.exe under this derived bin root.
    private static readonly string CodexBinRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI", "Codex", "bin");

    public static async Task<CodexResult> RunAsync(CodexInvocation inv, CancellationToken ct = default)
    {
        var codexExe = ResolveCodexExe();
        const string providerId = "bridge";
        var baseUrl = inv.BridgeBaseUrl.TrimEnd('/') + "/codex";

        // Inject the provider purely via -c overrides (config.toml untouched).
        var args = new List<string>
        {
            "exec", "--json",
            "--skip-git-repo-check",
            "-c", $"model_provider={providerId}",
            "-c", $"model_providers.{providerId}.name=bridge",
            "-c", $"model_providers.{providerId}.base_url={baseUrl}",
            "-c", $"model_providers.{providerId}.wire_api=responses",
            "-c", $"model_providers.{providerId}.env_key=BRIDGE_DUMMY_KEY",
            "-m", inv.Model,
            "--dangerously-bypass-approvals-and-sandbox",
            inv.Prompt,
        };

        var psi = new ProcessStartInfo
        {
            FileName = codexExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (inv.WorkingDirectory is not null) psi.WorkingDirectory = inv.WorkingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["BRIDGE_DUMMY_KEY"] = "dummy-bridge-bypass";
        // Isolate config/auth/state from the user's real ~/.codex — but note codex still
        // writes its dispatch log (logs_2.sqlite) to the REAL ~/.codex, not here (see the
        // record docs). CodexHome is returned for completeness; the verdict reads
        // DispatchLogPath windowed by StartedUnixSeconds.
        var codexHome = inv.CodexHome
            ?? Path.Combine(Path.GetTempPath(), "codex-e1-home-" + Guid.NewGuid().ToString("N"));
        psi.Environment["CODEX_HOME"] = codexHome;
        Directory.CreateDirectory(codexHome);

        // Stamp the start time (whole seconds, floored) so the verdict windows this run's
        // rows out of the long-lived ~/.codex/logs_2.sqlite. Minus 2s for clock/rounding
        // slack so a row written in the same second is never missed.
        var startedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        var timeout = inv.Timeout ?? TimeSpan.FromMinutes(2);
        var exited = await Task.Run(() => proc.WaitForExit((int)timeout.TotalMilliseconds), ct);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"codex.exe did not exit within {timeout}.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();
        // Upper window bound. codex flushes its last dispatch rows a beat AFTER the
        // process returns, so wait for that drain, THEN stamp the actual current time —
        // do NOT pad the bound into the future (a `now + N` bound would overlap the NEXT
        // sequential run in the same class and let its rows, including a fast fatal, be
        // attributed to this case). The bounded wait captures the flush without reaching
        // past it.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var endedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CodexResult(proc.ExitCode, stdout, stderr, sw.Elapsed, codexHome,
            RealDispatchLog, startedUnix, endedUnix);
    }

    private static string ResolveCodexExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable(CodexExeEnv);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        // Prefer the NEWEST codex.exe under the versioned bin root — Codex
        // self-updates into a fresh hash dir, so "newest by write time" tracks the
        // active install without a hardcoded hash (or user name) going stale.
        if (Directory.Exists(CodexBinRoot))
        {
            var newest = Directory.EnumerateFiles(CodexBinRoot, "codex.exe", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null) return newest.FullName;
        }
        // Walk PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate codex.exe. Set CODEX_EXE or ensure codex is on PATH.");
    }
}
