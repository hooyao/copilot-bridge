using System.Security.Cryptography;
using System.Text;
using CopilotBridge.Cli.Hosting.Options;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal static class CodexCatalogSource
{
    private const string Origin = "https://raw.githubusercontent.com";
    private const string Prefix = "/openai/codex/rust-v";
    private const string Suffix = "/codex-rs/models-manager/models.json";

    public static Uri BuildUri(CodexClientVersion version) =>
        new(Origin + Prefix + version + Suffix, UriKind.Absolute);
}

internal static class CodexCatalogCachePaths
{
    public static string ResolveRoot(CodexModelCatalogOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CacheDirectory))
        {
            if (!Path.IsPathFullyQualified(options.CacheDirectory))
                throw new InvalidOperationException("Codex catalog cache directory must be absolute.");
            return Path.GetFullPath(options.CacheDirectory);
        }

        string baseDirectory;
        if (OperatingSystem.IsWindows())
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        }
        else
        {
            baseDirectory = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new InvalidOperationException("Cannot resolve a per-user cache directory for Codex catalogs.");
        return Path.GetFullPath(Path.Combine(baseDirectory, "copilot-bridge", "codex-catalogs"));
    }

    public static string GetRecordPath(string root, CodexClientVersion version)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version.ToString())));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, $"catalog-{hash}.cache"));
        if (!IsUnderRoot(normalizedRoot, path))
            throw new InvalidOperationException("Resolved Codex catalog cache path escaped its root.");
        return path;
    }

    public static bool IsUnderRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return string.Equals(normalizedRoot, normalizedCandidate, comparison) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
