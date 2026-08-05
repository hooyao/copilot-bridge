using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Codex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal sealed record CodexCatalogCacheEntry
{
    public required CodexClientVersion Version { get; init; }
    public required Uri SourceUri { get; init; }
    public required byte[] SourceBytes { get; init; }
    public required CodexCatalogCacheMetadata Metadata { get; init; }
    public required CodexCatalogBaseline Baseline { get; init; }
}

internal interface ICodexCatalogDiskStore
{
    ValueTask<CodexCatalogCacheEntry?> TryLoadAsync(CodexClientVersion version, CancellationToken cancellationToken = default);
    ValueTask<CodexCatalogCacheEntry> PromoteAsync(CodexCatalogCacheEntry candidate, CancellationToken cancellationToken = default);
    ValueTask CleanupAsync(Func<string, bool> isProtectedVersion, CancellationToken cancellationToken = default);
}

internal sealed class CodexCatalogDiskStore(
    IOptions<CodexModelCatalogOptions> options,
    ILogger<CodexCatalogDiskStore> log,
    CodexCatalogDiskStoreHooks? hooks = null,
    TimeProvider? timeProvider = null) : ICodexCatalogDiskStore, IDisposable
{
    private static ReadOnlySpan<byte> Magic => "CBCAT001"u8;
    private const int HeaderLength = 16;
    private const int MetadataLimit = 64 * 1024;
    private static readonly SemaphoreSlim ProcessWriterLock = new(1, 1);
    private readonly CodexModelCatalogOptions _options = options.Value;
    private readonly string _root = CodexCatalogCachePaths.ResolveRoot(options.Value);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async ValueTask<CodexCatalogCacheEntry?> TryLoadAsync(
        CodexClientVersion version,
        CancellationToken cancellationToken = default)
    {
        var path = CodexCatalogCachePaths.GetRecordPath(_root, version);
        if (!File.Exists(path)) return null;
        try
        {
            return await ReadRecordAsync(path, version, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(exception, "Ignored invalid Codex catalog disk cache for {ClientVersion}", version);
            return null;
        }
    }

    public async ValueTask<CodexCatalogCacheEntry> PromoteAsync(
        CodexCatalogCacheEntry candidate,
        CancellationToken cancellationToken = default)
    {
        candidate = ValidateCandidate(candidate);
        await ProcessWriterLock.WaitAsync(cancellationToken);
        try
        {
            if (hooks?.MutationEntered is { } entered) await entered(cancellationToken);
            Directory.CreateDirectory(_root);
            // Another waiter may have promoted fresher state while this request waited.
            var current = await TryReadUnderWriterLockAsync(candidate.Version, cancellationToken);
            if (current is not null && current.Metadata.ValidatedAtUtc >= candidate.Metadata.ValidatedAtUtc)
                return current;

            var destination = CodexCatalogCachePaths.GetRecordPath(_root, candidate.Version);
            var temporary = Path.Combine(_root, $".catalog-{Guid.NewGuid():N}.tmp");
            try
            {
                await WriteRecordAsync(temporary, candidate, cancellationToken);
                if (hooks?.BeforePromotion is { } beforePromotion)
                    await beforePromotion(temporary, destination, cancellationToken);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(exception, "Failed to remove Codex catalog temporary cache record");
                }
            }
            return candidate;
        }
        finally
        {
            hooks?.MutationExited?.Invoke();
            ProcessWriterLock.Release();
        }
    }

    private CodexCatalogCacheEntry ValidateCandidate(CodexCatalogCacheEntry candidate)
    {
        if (candidate.SourceBytes.Length is <= 0 || candidate.SourceBytes.Length > _options.MaxSourceBytes)
            throw new InvalidDataException("Codex catalog cache candidate has invalid source length.");
        var expectedUri = CodexCatalogSource.BuildUri(candidate.Version);
        var digest = Convert.ToHexStringLower(SHA256.HashData(candidate.SourceBytes));
        if (candidate.Metadata.ClientVersion != candidate.Version.ToString() ||
            candidate.Metadata.SourceUrl != expectedUri.AbsoluteUri ||
            candidate.SourceUri != expectedUri ||
            !string.Equals(candidate.Metadata.Sha256, digest, StringComparison.Ordinal))
            throw new InvalidDataException("Codex catalog cache candidate identity does not match its source bytes.");
        var baseline = CodexCatalogBaseline.Parse(candidate.SourceBytes, candidate.Metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        ValidateTimestamps(candidate.Metadata);
        return candidate with { SourceUri = expectedUri, Baseline = baseline };
    }

    public async ValueTask CleanupAsync(
        Func<string, bool> isProtectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return;
        await ProcessWriterLock.WaitAsync(cancellationToken);
        try
        {
            if (hooks?.MutationEntered is { } entered) await entered(cancellationToken);
            var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-_options.RetentionDays);
            var records = Directory.EnumerateFiles(_root, "catalog-*.cache", SearchOption.TopDirectoryOnly)
                .Where(IsRecognizedRecordPath)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            var inactive = new List<(FileInfo Record, string? Version)>();
            foreach (var record in records)
            {
                var version = await TryReadVersionUnderWriterLockAsync(record.FullName, cancellationToken);
                if (version is not null && isProtectedVersion(version)) continue;
                inactive.Add((record, version));
            }

            var expired = inactive.Where(item => item.Record.LastWriteTimeUtc < cutoff);
            var overCount = inactive.Where(item => item.Record.LastWriteTimeUtc >= cutoff)
                .Skip(_options.MaxRetainedVersions);
            foreach (var (record, version) in expired.Concat(overCount))
            {
                // Memory/in-flight state can change while cleanup is scanning.
                // Re-evaluate immediately before deletion while the disk writer
                // lock is still held.
                if (version is not null && isProtectedVersion(version)) continue;
                try { record.Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(exception, "Failed to prune Codex catalog cache record {CacheFile}", record.Name);
                }
            }

            foreach (var temporary in Directory.EnumerateFiles(_root, ".catalog-*.tmp", SearchOption.TopDirectoryOnly)
                         .Where(IsRecognizedTemporaryPath))
            {
                try
                {
                    if (hooks?.BeforeDelete is { } beforeDelete)
                        await beforeDelete(temporary, cancellationToken);
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(exception, "Failed to prune Codex catalog temporary record");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(exception, "Failed to enumerate Codex catalog cache for retention cleanup");
        }
        finally
        {
            hooks?.MutationExited?.Invoke();
            ProcessWriterLock.Release();
        }
    }

    private static bool IsRecognizedRecordPath(string path) =>
        HasHexName(Path.GetFileName(path), "catalog-", ".cache", 64);

    private static bool IsRecognizedTemporaryPath(string path) =>
        HasHexName(Path.GetFileName(path), ".catalog-", ".tmp", 32);

    private static bool HasHexName(string name, string prefix, string suffix, int digits)
    {
        if (name.Length != prefix.Length + digits + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        return name.AsSpan(prefix.Length, digits).ToString()
            .All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private async ValueTask<CodexCatalogCacheEntry?> TryReadUnderWriterLockAsync(
        CodexClientVersion version,
        CancellationToken cancellationToken)
    {
        var path = CodexCatalogCachePaths.GetRecordPath(_root, version);
        if (!File.Exists(path)) return null;
        try { return await ReadRecordAsync(path, version, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(exception, "Ignored invalid Codex catalog disk cache during promotion for {ClientVersion}", version);
            return null;
        }
    }

    private async ValueTask<string?> TryReadVersionUnderWriterLockAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[HeaderLength];
            await stream.ReadExactlyAsync(header, cancellationToken);
            ValidateHeader(header, out var metadataLength, out _);
            var metadataBytes = new byte[metadataLength];
            await stream.ReadExactlyAsync(metadataBytes, cancellationToken);
            return JsonSerializer.Deserialize(metadataBytes, JsonContext.Default.CodexCatalogCacheMetadata)?.ClientVersion;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(exception, "Could not identify Codex catalog cache record during cleanup");
            return null;
        }
    }

    private async ValueTask<CodexCatalogCacheEntry> ReadRecordAsync(
        string path,
        CodexClientVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        ValidateHeader(header, out var metadataLength, out var sourceLength);
        if (sourceLength > _options.MaxSourceBytes || stream.Length != HeaderLength + metadataLength + sourceLength)
            throw new InvalidDataException("Codex catalog cache record has invalid lengths.");
        var metadataBytes = new byte[metadataLength];
        await stream.ReadExactlyAsync(metadataBytes, cancellationToken);
        var sourceBytes = new byte[sourceLength];
        await stream.ReadExactlyAsync(sourceBytes, cancellationToken);
        var metadata = JsonSerializer.Deserialize(metadataBytes, JsonContext.Default.CodexCatalogCacheMetadata)
            ?? throw new InvalidDataException("Codex catalog cache metadata is empty.");
        var expectedUri = CodexCatalogSource.BuildUri(expectedVersion);
        var digest = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        if (metadata.SchemaVersion != 1 || metadata.ClientVersion != expectedVersion.ToString() ||
            metadata.SourceUrl != expectedUri.AbsoluteUri || !string.Equals(metadata.Sha256, digest, StringComparison.Ordinal))
            throw new InvalidDataException("Codex catalog cache metadata does not match its source bytes.");
        var baseline = CodexCatalogBaseline.Parse(sourceBytes, metadata);
        CodexCatalogBaselineValidator.Validate(baseline);
        ValidateTimestamps(metadata);
        return new()
        {
            Version = expectedVersion,
            SourceUri = expectedUri,
            SourceBytes = sourceBytes,
            Metadata = metadata,
            Baseline = baseline,
        };
    }

    private void ValidateTimestamps(CodexCatalogCacheMetadata metadata)
    {
        var futureLimit = _time.GetUtcNow().AddMinutes(5);
        if (metadata.FetchedAtUtc > futureLimit || metadata.ValidatedAtUtc > futureLimit)
            throw new InvalidDataException("Codex catalog cache metadata has timestamps in the future.");
    }

    private async ValueTask WriteRecordAsync(
        string path,
        CodexCatalogCacheEntry entry,
        CancellationToken cancellationToken)
    {
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(entry.Metadata, JsonContext.Default.CodexCatalogCacheMetadata);
        if (metadataBytes.Length > MetadataLimit) throw new InvalidDataException("Codex catalog cache metadata is oversized.");
        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), metadataBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), entry.SourceBytes.Length);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(header, cancellationToken);
        if (hooks?.AfterHeaderWritten is { } afterHeaderWritten)
            await afterHeaderWritten(path, cancellationToken);
        await stream.WriteAsync(metadataBytes, cancellationToken);
        await stream.WriteAsync(entry.SourceBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header, out int metadataLength, out int sourceLength)
    {
        if (header.Length != HeaderLength || !header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Codex catalog cache record has an invalid header.");
        metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        sourceLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (metadataLength is <= 0 or > MetadataLimit || sourceLength <= 0)
            throw new InvalidDataException("Codex catalog cache record has invalid lengths.");
    }

    public void Dispose() { }
}

internal sealed record CodexCatalogDiskStoreHooks
{
    public Func<CancellationToken, ValueTask>? MutationEntered { get; init; }
    public Action? MutationExited { get; init; }
    public Func<string, string, CancellationToken, ValueTask>? BeforePromotion { get; init; }
    public Func<string, CancellationToken, ValueTask>? BeforeDelete { get; init; }
    public Func<string, CancellationToken, ValueTask>? AfterHeaderWritten { get; init; }
}
