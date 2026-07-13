using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// The outcome of a preflight step: a success flag plus a short, secret-free
/// phase/reason phrase for the journal and operator diagnostics.
/// </summary>
internal readonly struct UpdateStepResult
{
    private UpdateStepResult(bool ok, string? reason)
    {
        Ok = ok;
        Reason = reason;
    }

    public bool Ok { get; }
    public string? Reason { get; }

    public static UpdateStepResult Success() => new(true, null);
    public static UpdateStepResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Framework-only archive verification and extraction. No <c>tar</c>/<c>unzip</c>
/// process is used. Extraction is entry-by-entry so every path and entry type is
/// checked before a byte is written, and nothing lands outside the staging root.
/// </summary>
internal static class ArchiveExtractor
{
    /// <summary>
    /// Compute the lowercase hex SHA-256 of a file, streaming (no full-file
    /// buffering).
    /// </summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Verify a downloaded archive's byte count and SHA-256 against the plan's
    /// values before it is trusted for extraction.
    /// </summary>
    public static async Task<UpdateStepResult> VerifyAsync(
        string archivePath, long expectedSize, string expectedSha256Hex, CancellationToken ct)
    {
        var info = new FileInfo(archivePath);
        if (!info.Exists)
        {
            return UpdateStepResult.Fail("archive missing after download");
        }
        if (info.Length != expectedSize)
        {
            return UpdateStepResult.Fail("archive size mismatch");
        }
        var actual = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateStepResult.Fail("archive digest mismatch");
        }
        return UpdateStepResult.Success();
    }

    /// <summary>
    /// Extract a ZIP or TAR.GZ into a fresh <paramref name="stagingDir"/>,
    /// accepting only regular files that resolve inside it. Rejects absolute
    /// paths, <c>..</c> traversal, mixed-separator escapes, links/hard
    /// links/reparse entries, devices/special entries, and case-insensitively
    /// duplicate normalized names. On any violation nothing outside staging
    /// remains usable and a fail reason is returned.
    /// </summary>
    public static UpdateStepResult Extract(string archivePath, string archiveKind, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var seen = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        try
        {
            return archiveKind switch
            {
                UpdateWire.ArchiveZip => ExtractZip(archivePath, stagingDir, seen),
                UpdateWire.ArchiveTarGz => ExtractTarGz(archivePath, stagingDir, seen),
                _ => UpdateStepResult.Fail("unsupported archive kind"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return UpdateStepResult.Fail($"archive extraction failed: {ex.GetType().Name}");
        }
    }

    private static UpdateStepResult ExtractZip(string archivePath, string stagingDir, HashSet<string> seen)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            // A directory entry in ZIP has an empty name after the trailing '/'.
            var name = entry.FullName;
            if (name.EndsWith('/') || name.EndsWith('\\'))
            {
                // Skip explicit directory entries; files create their own dirs.
                continue;
            }

            var target = UpdatePaths.ResolveContained(stagingDir, name);
            if (target is null)
            {
                return UpdateStepResult.Fail("archive entry escapes staging");
            }
            if (!seen.Add(NormalizeKey(stagingDir, target)))
            {
                return UpdateStepResult.Fail("archive has duplicate entry");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        return UpdateStepResult.Success();
    }

    private static UpdateStepResult ExtractTarGz(string archivePath, string stagingDir, HashSet<string> seen)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    continue; // files create their own directories
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    break;
                default:
                    // Symlinks, hard links, char/block devices, fifos, etc.
                    return UpdateStepResult.Fail($"archive contains non-regular entry ({entry.EntryType})");
            }

            var target = UpdatePaths.ResolveContained(stagingDir, entry.Name);
            if (target is null)
            {
                return UpdateStepResult.Fail("archive entry escapes staging");
            }
            if (!seen.Add(NormalizeKey(stagingDir, target)))
            {
                return UpdateStepResult.Fail("archive has duplicate entry");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        return UpdateStepResult.Success();
    }

    private static string NormalizeKey(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return OperatingSystem.IsLinux() ? rel : rel.ToLowerInvariant();
    }
}
