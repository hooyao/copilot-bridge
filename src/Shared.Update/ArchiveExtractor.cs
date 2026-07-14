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
    public static UpdateStepResult Extract(
        string archivePath, string archiveKind, string stagingDir, long maxExtractedBytes = MaxExtractedBytes)
    {
        Directory.CreateDirectory(stagingDir);
        var seen = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var budget = new ExtractBudget(maxExtractedBytes);

        try
        {
            var result = archiveKind switch
            {
                UpdateWire.ArchiveZip => ExtractZip(archivePath, stagingDir, seen, budget),
                UpdateWire.ArchiveTarGz => ExtractTarGz(archivePath, stagingDir, seen, budget),
                _ => UpdateStepResult.Fail("unsupported archive kind"),
            };
            if (!result.Ok)
            {
                // A rejected archive leaves no partially-extracted tree behind.
                TryDeleteTree(stagingDir);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            TryDeleteTree(stagingDir);
            return UpdateStepResult.Fail($"archive extraction failed: {ex.GetType().Name}");
        }
    }

    // Defensive cap on TOTAL expanded bytes across all entries, so a small
    // highly-compressed archive (a "zip bomb") cannot exhaust the disk and stall
    // startup before preflight fails. Far above a legitimate release archive.
    private const long MaxExtractedBytes = 512L * 1024 * 1024;

    private sealed class ExtractBudget(long limit)
    {
        private long _used;
        public bool TryAdd(long bytes)
        {
            _used += bytes;
            return _used <= limit;
        }
    }

    // Stream one entry to disk while counting expanded bytes against the budget.
    private static UpdateStepResult WriteEntry(Stream source, string target, ExtractBudget budget)
    {
        using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (!budget.TryAdd(read))
            {
                return UpdateStepResult.Fail("archive expands beyond the size budget");
            }
            dst.Write(buffer, 0, read);
        }
        return UpdateStepResult.Success();
    }

    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static UpdateStepResult ExtractZip(
        string archivePath, string stagingDir, HashSet<string> seen, ExtractBudget budget)
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

            // Reject anything that is not a plain regular file BEFORE materializing
            // it. ZIP has no EntryType like TAR, but the high 16 bits of
            // ExternalAttributes carry the Unix st_mode for Unix-authored archives,
            // and the low bits carry MS-DOS/Windows FileAttributes — so a symlink,
            // device node, or reparse-point entry is detectable here. This mirrors
            // the TAR path's non-regular rejection and satisfies the update
            // contract (links/reparse/unexpected types rejected before cutover).
            var reject = NonRegularZipEntryReason(entry);
            if (reject is not null)
            {
                return UpdateStepResult.Fail(reject);
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
            using var src = entry.Open();
            var wrote = WriteEntry(src, target, budget);
            if (!wrote.Ok)
            {
                return wrote;
            }
        }
        return UpdateStepResult.Success();
    }

    private static UpdateStepResult ExtractTarGz(
        string archivePath, string stagingDir, HashSet<string> seen, ExtractBudget budget)
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
            var data = entry.DataStream;
            if (data is null)
            {
                // A regular file with no data stream is an empty file.
                using var _ = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                continue;
            }
            var wrote = WriteEntry(data, target, budget);
            if (!wrote.Ok)
            {
                return wrote;
            }
        }
        return UpdateStepResult.Success();
    }

    // Inspect a ZIP entry's ExternalAttributes and return a rejection reason if it
    // is anything other than a plain regular file, else null. Two encodings live
    // in ExternalAttributes: the high 16 bits hold the Unix st_mode when the
    // archive was authored on a Unix host (host byte 3), and the low 8 bits hold
    // MS-DOS/Windows FileAttributes. We reject Unix symlinks and any non-regular
    // Unix file type, and Windows reparse points — the entry types the update
    // contract forbids. A zero/absent Unix mode (common in Windows-authored ZIPs)
    // is treated as a regular file and allowed.
    private static string? NonRegularZipEntryReason(ZipArchiveEntry entry)
    {
        var attrs = entry.ExternalAttributes;

        // Windows reparse point (junction/symlink surrogate) in the DOS attr bits.
        const int fileAttributeReparsePoint = 0x400;
        if ((attrs & fileAttributeReparsePoint) != 0)
        {
            return "archive contains a reparse-point entry";
        }

        // Unix st_mode in the high 16 bits (only meaningful when non-zero).
        var unixMode = (attrs >> 16) & 0xFFFF;
        if (unixMode != 0)
        {
            const int sIfmt = 0xF000;   // file-type mask
            const int sIflnk = 0xA000;  // symbolic link
            const int sIfreg = 0x8000;  // regular file
            var fileType = unixMode & sIfmt;
            if (fileType == sIflnk)
            {
                return "archive contains a symlink entry";
            }
            // Any encoded type that is neither regular nor "unset" (0) is a device
            // node, fifo, socket, etc. — reject it.
            if (fileType != sIfreg && fileType != 0)
            {
                return $"archive contains a non-regular entry (mode 0x{unixMode:X})";
            }
        }

        return null;
    }

    private static string NormalizeKey(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return OperatingSystem.IsLinux() ? rel : rel.ToLowerInvariant();
    }
}
