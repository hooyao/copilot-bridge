using System.Diagnostics;
using CopilotBridge.Cli.Hosting.ClientConfig;

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
    // user's real ~/.codex). This exec surface does not start Codex's SQLite log layer.
    string? CodexHome = null,
    // When set, codex runs with this as its working directory. Tasks that write relative
    // filenames MUST pass a disposable dir so the real client cannot create/overwrite
    // files in the test runner's CWD (the checkout). null = inherit the runner's dir.
    string? WorkingDirectory = null,
    // Extra `-c key=value` config overrides appended AFTER the provider overrides, so a
    // test can toggle a codex config knob without touching the user's config.toml. E.g.
    // ["web_search=live"] forces codex's hosted web-search into live-fetch mode (default
    // is "cached") so a native-search test actually hits the network. Each entry becomes a
    // separate `-c <entry>` pair.
    IReadOnlyList<string>? ExtraConfig = null,
    // Enables the production configuration path: write the bridge provider through
    // CodexConfigurator, including nested command auth, so Codex fetches /models.
    bool UseCommandAuth = false,
    // Feed Prompt through stdin (`codex exec -`) to support long-context fixtures
    // beyond Windows' process command-line limit.
    bool PromptViaStdin = false,
    // Pin a reviewed client binary when endpoint schema/version compatibility is
    // part of the scenario. null preserves the existing latest-installed behavior.
    string? ExpectedCodexVersion = null);

internal sealed record CodexResult(
    int ExitCode, string Stdout, string Stderr, TimeSpan Duration, string CodexHome,
    // codex exec 0.147 hard-codes log_db=None and therefore has no SQLite dispatch log.
    // Scenarios that require the client-owned SQLite verdict use CodexAppServerProcess.
    string? DispatchLogPath,
    // Bounds remain useful for stderr/trace correlation. They are not SQLite proof for
    // codex exec because that surface does not start the log database.
    long StartedUnixSeconds,
    long EndedUnixSeconds);

internal static class CodexProcess
{
    private const string CodexExeEnv = "CODEX_EXE";
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
        var codexExe = ResolveCodexExe(inv.ExpectedCodexVersion);
        const string providerId = "bridge";
        var baseUrl = inv.BridgeBaseUrl.TrimEnd('/') + "/codex";

        var args = new List<string>
        {
            "exec", "--json",
            "--skip-git-repo-check",
            "-m", inv.Model,
            "--dangerously-bypass-approvals-and-sandbox",
            inv.PromptViaStdin ? "-" : inv.Prompt,
        };

        if (!inv.UseCommandAuth)
        {
            // Existing behavior tests inject a provider through -c and use a dummy
            // env key. This path intentionally does not opt into remote discovery.
            args.InsertRange(3,
            [
                "-c", $"model_provider={providerId}",
                "-c", $"model_providers.{providerId}.name=bridge",
                "-c", $"model_providers.{providerId}.base_url={baseUrl}",
                "-c", $"model_providers.{providerId}.wire_api=responses",
                "-c", $"model_providers.{providerId}.env_key=BRIDGE_DUMMY_KEY",
            ]);
        }

        // Append any extra -c overrides (e.g. web_search=live). Inserted before the
        // positional prompt would also work, but codex accepts flags after the prompt too;
        // keep them grouped with the other -c pairs by inserting ahead of -m. Simplest and
        // order-safe: rebuild with the extras placed just before the model flag.
        if (inv.ExtraConfig is { Count: > 0 } extra)
        {
            // Find the "-m" marker and splice the extra -c pairs in front of it so the
            // final arg order stays "<overrides> <extra overrides> -m <model> --bypass <prompt>".
            var insertAt = args.IndexOf("-m");
            var spliced = new List<string>(extra.Count * 2);
            foreach (var kv in extra) { spliced.Add("-c"); spliced.Add(kv); }
            args.InsertRange(insertAt, spliced);
        }

        var psi = new ProcessStartInfo
        {
            FileName = codexExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = inv.PromptViaStdin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (inv.WorkingDirectory is not null) psi.WorkingDirectory = inv.WorkingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["BRIDGE_DUMMY_KEY"] = "dummy-bridge-bypass";
        // Isolate config/auth/state from the user's real ~/.codex.
        var codexHome = inv.CodexHome
            ?? Path.Combine(Path.GetTempPath(), "codex-e1-home-" + Guid.NewGuid().ToString("N"));
        psi.Environment["CODEX_HOME"] = codexHome;
        Directory.CreateDirectory(codexHome);
        // Do not inherit Desktop identity into this standalone process. RUST_LOG keeps
        // stderr useful, but it cannot make codex exec create logs_2.sqlite: that binary
        // path deliberately constructs its core with log_db=None.
        psi.Environment["RUST_LOG"] =
            "warn,codex_core::tools=trace,codex_core::session::turn=trace,codex_core::stream_events_utils=debug";
        psi.Environment.Remove("CODEX_THREAD_ID");
        psi.Environment.Remove("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
        if (inv.UseCommandAuth)
        {
            var connection = new BridgeConnection(new Uri(inv.BridgeBaseUrl).Port);
            var (content, _) = CodexConfigurator.BuildContent(
                original: null,
                connection,
                CodexProviderAuthInvocation.ResolveCurrent());
            File.WriteAllText(Path.Combine(codexHome, "config.toml"), content);
        }

        // Stamp coarse bounds for stderr/trace correlation. Minus 2s for
        // clock/rounding slack so an event in the same second is never missed.
        var startedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        proc.Start();
        if (inv.PromptViaStdin)
        {
            await proc.StandardInput.WriteAsync(inv.Prompt.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
            proc.StandardInput.Close();
        }
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
        // Allow stderr and bridge-side asynchronous evidence to drain before recording
        // the upper bound.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var endedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CodexResult(proc.ExitCode, stdout, stderr, sw.Elapsed, codexHome,
            null, startedUnix, endedUnix);
    }

    internal static string ResolveCodexExe(string? expectedVersion = null)
    {
        var candidates = new List<string>();
        var fromEnv = Environment.GetEnvironmentVariable(CodexExeEnv);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) candidates.Add(fromEnv);
        // Prefer the NEWEST codex.exe under the versioned bin root — Codex
        // self-updates into a fresh hash dir, so "newest by write time" tracks the
        // active install without a hardcoded hash (or user name) going stale.
        if (Directory.Exists(CodexBinRoot))
        {
            var newest = Directory.EnumerateFiles(CodexBinRoot, "codex.exe", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .FirstOrDefault();
            candidates.AddRange(Directory.EnumerateFiles(CodexBinRoot, "codex.exe", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName));
        }
        // Walk PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "codex.exe");
            if (File.Exists(candidate)) candidates.Add(candidate);
        }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (expectedVersion is null || HasVersion(candidate, expectedVersion)) return candidate;
        }
        throw new FileNotFoundException(
            expectedVersion is null
                ? "Could not locate codex.exe. Set CODEX_EXE or ensure codex is on PATH."
                : $"Could not locate codex.exe version {expectedVersion}. Set CODEX_EXE to that reviewed binary.");
    }

    private static bool HasVersion(string executable, string expectedVersion)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(5_000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && string.Equals(
                process.StandardOutput.ReadToEnd().Trim(),
                $"codex-cli {expectedVersion}",
                StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
