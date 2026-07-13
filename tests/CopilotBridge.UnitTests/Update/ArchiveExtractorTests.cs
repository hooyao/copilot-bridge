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
