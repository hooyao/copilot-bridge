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
                if (File.Exists(candidate)
                    && IsCandidateOutsideWorkingDirectory(candidate, currentDirectory))
                    return candidate;
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
            if (File.Exists(direct)
                && IsCandidateOutsideWorkingDirectory(direct, currentDirectory))
                return direct;

            if (!shimNames.Any(name => File.Exists(Path.Combine(directory, name))))
                continue;

            foreach (var relative in packageExecutables)
            {
                var candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)
                    && IsCandidateOutsideWorkingDirectory(candidate, currentDirectory))
                    return Path.GetFullPath(candidate);
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

        if (!TryResolveDirectoryAliases(
                currentDirectory,
                out var current,
                out _,
                recursionDepth: 0))
            return [];

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
            if (!TryResolveDirectoryAliases(
                    directory,
                    out var resolvedDirectory,
                    out var traversedAlias,
                    recursionDepth: 0)
                || traversedAlias
                || IsSameOrChildDirectory(resolvedDirectory, current))
                continue;
            result.Add(resolvedDirectory);
        }
        return result;
    }

    private static bool IsCandidateOutsideWorkingDirectory(
        string candidate,
        string currentDirectory)
    {
        try
        {
            if (!TryResolveDirectoryAliases(
                    currentDirectory,
                    out var current,
                    out _,
                    recursionDepth: 0))
                return false;

            var file = new FileInfo(candidate);
            var target = file.LinkTarget is null
                ? file
                : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            if (target?.DirectoryName is not { } targetDirectory
                || !TryResolveDirectoryAliases(
                    targetDirectory,
                    out var resolvedTargetDirectory,
                    out _,
                    recursionDepth: 0))
                return false;

            return !IsSameOrChildDirectory(resolvedTargetDirectory, current);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or IOException
                or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryResolveDirectoryAliases(
        string path,
        out string resolved,
        out bool traversedAlias,
        int recursionDepth)
    {
        resolved = string.Empty;
        traversedAlias = false;
        if (recursionDepth > 40) return false;

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath)) return false;
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;

            var current = Path.TrimEndingDirectorySeparator(root);
            var relative = Path.GetRelativePath(root, fullPath);
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                if (segment == ".") continue;
                var next = Path.Combine(current, segment);
                var info = new DirectoryInfo(next);
                if (!info.Exists) return false;

                if (info.LinkTarget is null
                    && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    current = next;
                    continue;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not DirectoryInfo targetDirectory
                    || !TryResolveDirectoryAliases(
                        targetDirectory.FullName,
                        out current,
                        out _,
                        recursionDepth + 1))
                    return false;
                traversedAlias = true;
            }

            resolved = Path.TrimEndingDirectorySeparator(current);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or IOException
                or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsSameOrChildDirectory(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, parent, comparison)) return true;

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            || parent.EndsWith(Path.AltDirectorySeparatorChar)
                ? parent
                : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }
}
