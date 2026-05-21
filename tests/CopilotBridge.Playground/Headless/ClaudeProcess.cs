using System.Diagnostics;
using System.Text;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Spawns <c>claude.exe -p</c> in non-interactive (--bare) mode pointing at a
/// bridge URL. Captures stdout / stderr / exit code; the test asserts on those
/// alongside the bridge's per-request audit logs.
/// </summary>
internal sealed record ClaudeInvocation(
    string BridgeBaseUrl,
    string Prompt,
    string? Model = null,
    string? Effort = null,
    string OutputFormat = "json",
    bool Verbose = false,
    string? AllowedTools = "",  // "" = no tools; null = default
    TimeSpan? Timeout = null);

internal sealed record ClaudeResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration);

internal static class ClaudeProcess
{
    private const string ClaudeExeEnv = "CLAUDE_EXE";

    public static async Task<ClaudeResult> RunAsync(ClaudeInvocation inv, CancellationToken ct = default)
    {
        var claudeExe = ResolveClaudeExe();

        var args = new List<string>
        {
            "--bare",
            "-p", inv.Prompt,
            "--output-format", inv.OutputFormat,
        };
        if (inv.Verbose) args.Add("--verbose");
        if (inv.Model is not null) { args.Add("--model"); args.Add(inv.Model); }
        if (inv.AllowedTools is not null)
        {
            args.Add("--allowedTools");
            args.Add(inv.AllowedTools);
        }
        args.Add("--setting-sources");
        args.Add("");
        args.Add("--no-session-persistence");

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // --bare requires API key auth; bridge ignores the value, but Claude Code
        // refuses to start without one set.
        psi.Environment["ANTHROPIC_BASE_URL"] = inv.BridgeBaseUrl + "/cc";
        psi.Environment["ANTHROPIC_API_KEY"] = "dummy-bridge-bypass";
        // CLAUDE_CODE_EFFORT_LEVEL takes precedence over persisted settings.json's
        // effortLevel — see restored-src/src/utils/effort.ts resolveAppliedEffort.
        // The --effort CLI flag alone gets shadowed by a user's persisted setting,
        // so the env var is the unambiguous way to drive this in tests.
        if (inv.Effort is not null)
        {
            psi.Environment["CLAUDE_CODE_EFFORT_LEVEL"] = inv.Effort;
        }

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
            throw new TimeoutException($"claude.exe did not exit within {timeout}.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        return new ClaudeResult(proc.ExitCode, stdout, stderr, sw.Elapsed);
    }

    private static string ResolveClaudeExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClaudeExeEnv);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        // Fallback: walk PATH. `where claude` showed it under .local\bin\ for the
        // user; this should resolve via the OS lookup.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException(
            "Could not locate claude.exe. Set the CLAUDE_EXE environment variable or ensure claude is on PATH.");
    }
}
