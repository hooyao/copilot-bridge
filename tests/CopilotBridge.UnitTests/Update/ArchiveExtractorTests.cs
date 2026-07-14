using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Malicious-archive corpus tests for <see cref="ArchiveExtractor"/> ("Secure
/// download and archive staging"). Builds real ZIP/TAR.GZ bytes in memory,
/// including traversal, symlink, and duplicate entries, and asserts safe
/// rejection. Also proves size/digest verification.
/// </summary>
public class ArchiveExtractorTests : IDisposable
{
    private readonly string _root;

    public ArchiveExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cb-arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Staging => Path.Combine(_root, "staging");

    private string WriteZip(Action<ZipArchive> build)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        build(zip);
        return path;
    }

    private static void AddZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Clean_zip_extracts_all_files()
    {
        var zip = WriteZip(z =>
        {
            AddZipEntry(z, "copilot-bridge.exe", "bridge");
            AddZipEntry(z, "appsettings.json", "{}");
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);
        Assert.True(result.Ok);
        Assert.Equal("bridge", File.ReadAllText(Path.Combine(Staging, "copilot-bridge.exe")));
    }

    [Fact]
    public void Zip_expanding_beyond_the_budget_is_rejected_and_staging_removed()
    {
        // Highly compressible content that expands well past a tiny test budget.
        var big = new string('A', 200_000);
        var zip = WriteZip(z => AddZipEntry(z, "copilot-bridge.exe", big));

        // 1 KiB budget — the 200 KB entry must trip the expanded-size guard.
        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging, maxExtractedBytes: 1024);

        Assert.False(result.Ok);
        Assert.Contains("budget", result.Reason, System.StringComparison.OrdinalIgnoreCase);
        // The partially-extracted staging tree is cleaned up.
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void Zip_traversal_entry_is_rejected_and_nothing_escapes()
    {
        var zip = WriteZip(z => AddZipEntry(z, "../escaped.txt", "evil"));

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);
        Assert.False(result.Ok);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void Zip_duplicate_normalized_names_are_rejected_on_windows_macos()
    {
        // Case-insensitive duplicate — only meaningful where the FS is case-insensitive.
        if (OperatingSystem.IsLinux())
        {
            return;
        }
        var zip = WriteZip(z =>
        {
            AddZipEntry(z, "File.txt", "a");
            AddZipEntry(z, "file.txt", "b");
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);
        Assert.False(result.Ok);
    }

    [Fact]
    public void Zip_unix_symlink_entry_is_rejected_before_materializing()
    {
        // A ZIP entry authored on Unix carries its st_mode in the high 16 bits of
        // ExternalAttributes. A symlink (S_IFLNK) must be rejected — the update
        // contract forbids links/reparse/unexpected types before cutover — even
        // though .NET's ZIP reader would otherwise write it as an ordinary file.
        const int sIflnk = 0xA000;
        var zip = WriteZip(z =>
        {
            var e = z.CreateEntry("copilot-bridge.exe");
            using (var s = e.Open())
            {
                var bytes = Encoding.UTF8.GetBytes("/etc/passwd");
                s.Write(bytes, 0, bytes.Length);
            }
            e.ExternalAttributes = sIflnk << 16;
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);

        Assert.False(result.Ok);
        Assert.Contains("symlink", result.Reason, System.StringComparison.OrdinalIgnoreCase);
        // Nothing was materialized.
        Assert.False(File.Exists(Path.Combine(Staging, "copilot-bridge.exe")));
    }

    [Fact]
    public void Zip_unix_device_node_entry_is_rejected()
    {
        // S_IFCHR (character device) — any non-regular Unix type must be refused.
        const int sIfchr = 0x2000;
        var zip = WriteZip(z =>
        {
            var e = z.CreateEntry("dev-entry");
            using (var s = e.Open()) { s.WriteByte(0); }
            e.ExternalAttributes = sIfchr << 16;
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);

        Assert.False(result.Ok);
        Assert.Contains("non-regular", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zip_windows_reparse_point_entry_is_rejected()
    {
        // FILE_ATTRIBUTE_REPARSE_POINT (0x400) in the DOS/Windows attribute bits.
        const int reparse = 0x400;
        var zip = WriteZip(z =>
        {
            var e = z.CreateEntry("copilot-bridge.exe");
            using (var s = e.Open()) { s.WriteByte(0); }
            e.ExternalAttributes = reparse;
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);

        Assert.False(result.Ok);
        Assert.Contains("reparse", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zip_regular_file_with_unix_mode_still_extracts()
    {
        // A Unix-authored ZIP entry with a normal S_IFREG mode must NOT be rejected
        // by the new type check (guards against over-rejecting real archives).
        const int sIfreg = 0x8000;
        var zip = WriteZip(z =>
        {
            var e = z.CreateEntry("copilot-bridge");
            using (var s = e.Open())
            {
                var bytes = Encoding.UTF8.GetBytes("bridge");
                s.Write(bytes, 0, bytes.Length);
            }
            e.ExternalAttributes = (sIfreg | 0x1ED) << 16; // 0755
        });

        var result = ArchiveExtractor.Extract(zip, UpdateWire.ArchiveZip, Staging);

        Assert.True(result.Ok);
        Assert.Equal("bridge", File.ReadAllText(Path.Combine(Staging, "copilot-bridge")));
    }

    [Fact]
    public void TarGz_symlink_entry_is_rejected()
    {
        var path = Path.Combine(_root, "sym.tar.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "copilot-bridge.exe")
            {
                LinkName = "/etc/passwd",
            };
            tar.WriteEntry(link);
        }

        var result = ArchiveExtractor.Extract(path, UpdateWire.ArchiveTarGz, Staging);
        Assert.False(result.Ok);
        Assert.Contains("non-regular", result.Reason);
    }

    [Fact]
    public void TarGz_regular_files_extract()
    {
        var path = Path.Combine(_root, "ok.tar.gz");
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "copilot-bridge")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("bridge")),
            };
            tar.WriteEntry(entry);
        }

        var result = ArchiveExtractor.Extract(path, UpdateWire.ArchiveTarGz, Staging);
        Assert.True(result.Ok);
        Assert.Equal("bridge", File.ReadAllText(Path.Combine(Staging, "copilot-bridge")));
    }

    [Fact]
    public async Task Verify_rejects_size_mismatch_and_digest_mismatch()
    {
        var file = Path.Combine(_root, "blob.bin");
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("hello world"));
        var actual = await ArchiveExtractor.ComputeSha256Async(file, CancellationToken.None);
        var size = new FileInfo(file).Length;

        // Correct size + digest passes.
        Assert.True((await ArchiveExtractor.VerifyAsync(file, size, actual, CancellationToken.None)).Ok);
        // Wrong size fails.
        Assert.False((await ArchiveExtractor.VerifyAsync(file, size + 1, actual, CancellationToken.None)).Ok);
        // Wrong digest fails.
        Assert.False((await ArchiveExtractor.VerifyAsync(file, size, new string('0', 64), CancellationToken.None)).Ok);
    }
}
