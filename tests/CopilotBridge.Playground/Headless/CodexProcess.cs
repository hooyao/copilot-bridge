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
    TimeSpan? Timeout = null);

internal sealed record CodexResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration);

internal static class CodexProcess
{
    private const string CodexExeEnv = "CODEX_EXE";
    private const string DefaultCodexExe =
        @"C:\Users\yahu2\AppData\Local\OpenAI\Codex\bin\f1c7ee7a13db5fed\codex.exe";

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
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["BRIDGE_DUMMY_KEY"] = "dummy-bridge-bypass";
        // Keep Codex from finding the user's real auth / config home.
        psi.Environment["CODEX_HOME"] = Path.Combine(Path.GetTempPath(), "codex-e1-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(psi.Environment["CODEX_HOME"]!);

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
        return new CodexResult(proc.ExitCode, stdout, stderr, sw.Elapsed);
    }

    private static string ResolveCodexExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable(CodexExeEnv);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        if (File.Exists(DefaultCodexExe)) return DefaultCodexExe;
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
