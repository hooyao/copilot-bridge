using System.Diagnostics;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Best-effort probe of the installed Claude Code version. Startup uses this only
/// to decide whether version-specific timeout interpretation is safe; failure is
/// non-fatal and produces an explicit version-dependent/unknown report.
/// </summary>
internal static class ClaudeCodeVersionProbe
{
    private const int ProbeTimeoutMilliseconds = 2_000;

    public static string? TryRead()
    {
        try
        {
            var executable = ResolveClaudeExecutable();
            if (executable is null) return null;

            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(output))
                output = stderr.GetAwaiter().GetResult();
            var firstToken = output.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return Version.TryParse(firstToken, out _) ? firstToken : null;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveClaudeExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        var currentDirectory = Environment.CurrentDirectory;
        if (!OperatingSystem.IsWindows())
            return FindExecutableOnPath(
                "claude", pathValue, [string.Empty], currentDirectory);

        return FindWindowsClaudeExecutable(pathValue, currentDirectory);
    }

    /// <summary>
    /// Resolve a command only from explicit, fully-qualified PATH directories.
    /// Empty/relative entries and the process working directory are skipped so a
    /// bridge launched inside an untrusted checkout cannot turn a best-effort
    /// version probe into command execution.
    /// </summary>
    internal static string? FindExecutableOnPath(
        string command,
        string? pathValue,
        IReadOnlyList<string> extensions,
        string currentDirectory)
    {
        foreach (var directory in SafePathDirectories(pathValue, currentDirectory))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the Windows native executable in PATH directory order. npm places a
    /// shell shim in the selected directory and the real executable below it; a
    /// direct executable in a later PATH entry must not override that selected
    /// installation merely because it has a different file shape.
    /// </summary>
    internal static string? FindWindowsClaudeExecutable(
        string? pathValue,
        string currentDirectory)
    {
        string[] shimNames = ["claude.cmd", "claude.bat", "claude.ps1", "claude"];
        string[] packageExecutables =
        [
            Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            Path.Combine("node_modules", "@anthropic-ai", "claude-code", "node_modules",
                "@anthropic-ai", "claude-code-win32-x64", "claude.exe"),
        ];

        foreach (var directory in SafePathDirectories(pathValue, currentDirectory))
        {
            var direct = Path.Combine(directory, "claude.exe");
            if (File.Exists(direct)) return direct;

            if (!shimNames.Any(name => File.Exists(Path.Combine(directory, name))))
                continue;

            foreach (var relative in packageExecutables)
            {
                var candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            // This is the first PATH entry that would resolve `claude`, but its
            // native target is not in a source-confirmed layout. Reporting unknown
            // is safer than probing a different installation later in PATH.
            return null;
        }
        return null;
    }

    private static IReadOnlyList<string> SafePathDirectories(
        string? pathValue,
        string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathValue)) return [];

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
        var result = new List<string>();
        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator))
        {
            string directory;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                if (string.IsNullOrWhiteSpace(expanded) || !Path.IsPathFullyQualified(expanded))
                    continue;
                directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or IOException
                    or System.Security.SecurityException)
            {
                continue;
            }
            if (string.Equals(directory, current, comparison)) continue;
            result.Add(directory);
        }
        return result;
    }
}
