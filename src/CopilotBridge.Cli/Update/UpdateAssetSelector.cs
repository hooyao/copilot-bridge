using System.Runtime.InteropServices;
using CopilotBridge.Cli.Models.GitHub;
using CopilotBridge.Update.Wire;

namespace CopilotBridge.Cli.Update;

/// <summary>The download archive format for a runtime identifier.</summary>
internal enum ArchiveKind
{
    Zip,
    TarGz,
}

/// <summary>Maps <see cref="ArchiveKind"/> to its frozen wire string.</summary>
internal static class ArchiveKindExtensions
{
    public static string ToWire(this ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => UpdateWire.ArchiveZip,
        ArchiveKind.TarGz => UpdateWire.ArchiveTarGz,
        // A new kind must be mapped here explicitly — never silently misrouted.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped archive kind"),
    };
}

/// <summary>
/// A fully-validated download target for one release + RID: exactly one asset,
/// with an HTTPS URL, positive size, and a supported <c>sha256:</c> digest.
/// </summary>
internal sealed record ResolvedAsset(
    string AssetName,
    string DownloadUrl,
    long Size,
    string Sha256Hex,
    ArchiveKind Kind);

/// <summary>
/// Maps the current OS/architecture to one supported runtime identifier and
/// selects the single matching release archive. macOS auto-update uses the
/// <c>.tar.gz</c>, never the <c>.pkg</c>. Any ambiguity (missing/duplicate
/// asset, non-uploaded state, non-HTTPS URL, non-positive size, missing or
/// non-SHA-256 digest, unsupported runtime) yields <c>null</c> so the caller
/// warns and starts the current version rather than performing a partial update.
/// </summary>
internal static class UpdateAssetSelector
{
    /// <summary>The RIDs this project publishes update archives for.</summary>
    public static readonly IReadOnlyList<string> SupportedRids =
        ["win-x64", "win-arm64", "linux-x64", "osx-arm64"];

    /// <summary>Resolve the RID for the current process, or null if unsupported.</summary>
    public static string? CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows())
        {
            return arch switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null,
            };
        }
        if (OperatingSystem.IsLinux())
        {
            return arch == Architecture.X64 ? "linux-x64" : null;
        }
        if (OperatingSystem.IsMacOS())
        {
            return arch == Architecture.Arm64 ? "osx-arm64" : null;
        }
        return null;
    }

    private static bool IsWindowsRid(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>The archive kind and expected asset file name for a version+RID.</summary>
    public static (ArchiveKind Kind, string AssetName) ExpectedAsset(string version, string rid)
    {
        return IsWindowsRid(rid)
            ? (ArchiveKind.Zip, $"copilot-bridge-{version}-{rid}.zip")
            : (ArchiveKind.TarGz, $"copilot-bridge-{version}-{rid}.tar.gz");
    }

    /// <summary>
    /// Select the exact update asset for <paramref name="release"/> on
    /// <paramref name="rid"/>. <paramref name="version"/> is the release version
    /// without the tag's <c>v</c> prefix (matches the asset file name).
    /// </summary>
    public static ResolvedAsset? Resolve(GitHubRelease release, string version, string rid)
    {
        if (!SupportedRids.Contains(rid))
        {
            return null;
        }

        var (kind, assetName) = ExpectedAsset(version, rid);

        GitHubReleaseAsset? match = null;
        foreach (var asset in release.Assets)
        {
            if (!string.Equals(asset.Name, assetName, StringComparison.Ordinal))
            {
                continue;
            }
            if (match is not null)
            {
                // Duplicate asset with the exact expected name — ambiguous.
                return null;
            }
            match = asset;
        }

        if (match is null)
        {
            return null;
        }
        if (!string.Equals(match.State, "uploaded", StringComparison.Ordinal))
        {
            return null;
        }
        if (match.Size <= 0)
        {
            return null;
        }
        if (match.BrowserDownloadUrl is not { } url
            || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!TryParseSha256(match.Digest, out var hex))
        {
            return null;
        }

        return new ResolvedAsset(assetName, url, match.Size, hex, kind);
    }

    // Accept only "sha256:<64 lowercase-or-uppercase hex>"; reject other
    // algorithms or malformed digests. Returns the lowercased hex.
    private static bool TryParseSha256(string? digest, out string hex)
    {
        hex = string.Empty;
        if (digest is null)
        {
            return false;
        }
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var value = digest[prefix.Length..];
        if (value.Length != 64)
        {
            return false;
        }
        foreach (var c in value)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!ok)
            {
                return false;
            }
        }
        hex = value.ToLowerInvariant();
        return true;
    }
}
