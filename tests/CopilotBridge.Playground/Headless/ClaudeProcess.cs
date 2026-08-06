using System.Diagnostics;
using System.Text;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Spawns <c>claude.exe -p</c> in non-interactive mode pointing at a bridge URL.
/// Invocations use <c>--bare</c> by default but may opt into Agent orchestration.
/// Captures stdout / stderr / exit code; the test asserts on those alongside the
/// bridge's per-request audit logs.
/// </summary>
internal sealed record ClaudeInvocation(
    string BridgeBaseUrl,
    string Prompt,
    string? Model = null,
    string? Effort = null,
    string OutputFormat = "json",
    bool Verbose = false,
    string? AllowedTools = "",        // "" = no tools; null = default
    TimeSpan? Timeout = null,
    string? AnthropicApiKey = null,   // when set, AnthropicBaseUrl points at native
    string? AnthropicBaseUrl = null,  // override target (e.g. https://api.anthropic.com); null = bridge
    IReadOnlyList<string>? Betas = null,
    string? McpConfigPath = null,     // when set, passes --mcp-config <path>
    // Bare mode sets CLAUDE_CODE_SIMPLE=1 and removes Agent orchestration. Keep it
    // on for ordinary behavior cases; multi-agent cases set false explicitly while
    // still disabling settings sources and session persistence below.
    bool Bare = true,
    // When set, the client process runs with this as its working directory. Behavior
    // tasks that write relative filenames MUST pass a disposable dir so the real client
    // cannot create/overwrite files in the test runner's CWD (the checkout). null =
    // inherit the runner's directory (fine for no-tool / read-only tasks).
    string? WorkingDirectory = null,
    // Strip the timeout-governing env vars from the child so it runs on Claude Code's
    // FACTORY watchdog defaults. Required by any case whose whole point is that the
    // bridge — not a raised client bound — is what keeps a silent turn alive.
    bool ClearTimeoutEnv = false,
    // Compact-recovery cases need Claude Code's own persisted JSONL as the
    // semantic evidence. Ordinary behavior cases stay ephemeral by default.
    bool PersistSession = false,
    string? ClaudeConfigDir = null,
    string? SessionId = null,
    string? ResumeSessionId = null);

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

        var args = new List<string>();
        if (inv.Bare) args.Add("--bare");
        args.Add("-p");
        args.Add(inv.Prompt);
        args.Add("--output-format");
        args.Add(inv.OutputFormat);
        if (inv.Verbose) args.Add("--verbose");
        if (inv.Model is not null) { args.Add("--model"); args.Add(inv.Model); }
        if (inv.AllowedTools is not null)
        {
            args.Add("--allowedTools");
            args.Add(inv.AllowedTools);
        }
        if (inv.McpConfigPath is not null)
        {
            args.Add("--mcp-config");
            args.Add(inv.McpConfigPath);
            args.Add("--strict-mcp-config");
        }
        args.Add("--setting-sources");
        args.Add("");
        if (!inv.PersistSession) args.Add("--no-session-persistence");
        if (inv.SessionId is not null)
        {
            args.Add("--session-id");
            args.Add(inv.SessionId);
        }
        if (inv.ResumeSessionId is not null)
        {
            args.Add("--resume");
            args.Add(inv.ResumeSessionId);
        }

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (inv.WorkingDirectory is not null) psi.WorkingDirectory = inv.WorkingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        // --bare requires API key auth; bridge ignores the value, but Claude Code
        // refuses to start without one set.
        psi.Environment["ANTHROPIC_BASE_URL"] = inv.BridgeBaseUrl + "/cc";
        psi.Environment["ANTHROPIC_API_KEY"] = "dummy-bridge-bypass";
        if (inv.ClaudeConfigDir is not null)
            psi.Environment["CLAUDE_CONFIG_DIR"] = inv.ClaudeConfigDir;
        // CLAUDE_CODE_EFFORT_LEVEL takes precedence over persisted settings.json's
        // effortLevel — see restored-src/src/utils/effort.ts resolveAppliedEffort.
        // The --effort CLI flag alone gets shadowed by a user's persisted setting,
        // so the env var is the unambiguous way to drive this in tests.
        if (inv.Effort is not null)
        {
            psi.Environment["CLAUDE_CODE_EFFORT_LEVEL"] = inv.Effort;
        }
        if (inv.ClearTimeoutEnv)
        {
            // Run the client on its FACTORY timeout defaults. The keepalive behavior
            // case exists to prove the bridge's injected pings alone keep a healthy
            // silent turn alive; if an inherited env var had raised the client's idle
            // watchdog, the case would pass whether or not a single ping was sent, and
            // would therefore prove nothing. `--setting-sources ""` already blocks the
            // settings FILES; this covers the process environment the runner inherited.
            psi.Environment.Remove("CLAUDE_STREAM_IDLE_TIMEOUT_MS");
            psi.Environment.Remove("CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS");
            psi.Environment.Remove("API_TIMEOUT_MS");
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

    /// <summary>
    /// Locate a claude launcher that receives this harness's arguments VERBATIM.
    /// <para>Only a native <c>.exe</c> qualifies. An npm install also drops shims next
    /// to it — <c>claude.cmd</c> (parsed by cmd.exe) and extensionless <c>claude</c> (a
    /// <c>#!/bin/sh</c> script) — and those re-parse the argument list: a prompt
    /// containing a shell metacharacter such as <c>&gt;</c> is TRUNCATED at that point,
    /// silently dropping both the rest of the prompt and every later flag (including
    /// <c>--output-format stream-json</c>). That failure looks like a client/bridge bug
    /// — the client answers "your steps never came through", emits plain text instead of
    /// stream-json, and never reaches the test bridge — so this resolver refuses to use
    /// a shim rather than let an environment problem masquerade as client behavior.</para>
    /// <para>PATH often exposes only the shims (npm's global bin), while the real
    /// executable lives under the package's own <c>bin/</c>. So after PATH, search the
    /// npm package layout relative to each PATH entry.</para>
    /// </summary>
    private static string ResolveClaudeExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ClaudeExeEnv);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

        // 1. A real executable directly on PATH.
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, ClaudeExeName);
            if (File.Exists(candidate)) return candidate;
        }

        // 2. The npm package layout hanging off a PATH entry that holds only shims.
        foreach (var dir in pathDirs)
        {
            foreach (var relative in NpmExeLocations)
            {
                var candidate = Path.Combine(dir, relative);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        // 3. A shim is present but no executable: name it, because running the shim is
        //    exactly the silent-corruption case above.
        var shim = pathDirs
            .SelectMany(dir => ShimNames.Select(name => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
        throw new FileNotFoundException(
            shim is null
                ? $"Could not locate {ClaudeExeName}. Set the {ClaudeExeEnv} environment variable "
                  + "or ensure claude is on PATH."
                : $"Found only a launcher shim ({shim}) and no {ClaudeExeName}. A shim re-parses "
                  + "arguments through a shell, which truncates any prompt containing a "
                  + "metacharacter such as '>' and drops later flags — the run would produce "
                  + $"misleading client behavior. Set {ClaudeExeEnv} to the real executable "
                  + "(npm installs it under node_modules/@anthropic-ai/claude-code/bin/).");
    }

    private const string ClaudeExeName = "claude.exe";

    /// <summary>Shims an npm install puts on PATH next to no executable.</summary>
    private static readonly string[] ShimNames = ["claude.cmd", "claude.ps1", "claude"];

    /// <summary>Where npm keeps the real executable, relative to its global bin dir.</summary>
    private static readonly string[] NpmExeLocations =
    [
        Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", ClaudeExeName),
        Path.Combine("node_modules", "@anthropic-ai", "claude-code", "node_modules",
            "@anthropic-ai", "claude-code-win32-x64", ClaudeExeName),
    ];
}
