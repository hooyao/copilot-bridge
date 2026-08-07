using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Codex;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Catalogs.Codex;

[ImmutableObject(true)]
internal sealed record CodexCatalogResolution
{
    public required bool Success { get; init; }
    public CodexCatalogBaseline? Baseline { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    internal CodexCatalogCacheEntry? Entry { get; init; }
}

/// <summary>
/// HybridCache serializes a factory result before publishing it to L1 even when
/// L2 is disabled. Catalog entries are immutable and L1 retains the object itself,
/// so the payload is only a size marker; deserialization would mean the catalog's
/// explicit no-distributed-cache contract was violated and therefore fails closed.
/// </summary>
internal sealed class CodexCatalogResolutionHybridCacheSerializer
    : IHybridCacheSerializer<CodexCatalogResolution>
{
    private const byte LocalOnlyMarker = 1;

    public CodexCatalogResolution Deserialize(ReadOnlySequence<byte> source) =>
        throw new InvalidOperationException(
            "Codex catalog HybridCache entries are process-local and cannot be deserialized.");

    public void Serialize(CodexCatalogResolution value, IBufferWriter<byte> target)
    {
        ArgumentNullException.ThrowIfNull(value);
        var destination = target.GetSpan(1);
        destination[0] = LocalOnlyMarker;
        target.Advance(1);
    }
}

/// <summary>
/// HybridCache otherwise registers its reflection-based JSON serializer factory
/// as a fallback. Every cache value in this AOT application must instead have an
/// explicit serializer, so unsupported types deliberately have no fallback.
/// </summary>
internal sealed class ExplicitOnlyHybridCacheSerializerFactory
    : IHybridCacheSerializerFactory
{
    public bool TryCreateSerializer<T>(
        [NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        serializer = null;
        return false;
    }
}

internal interface ICodexCatalogSourceCache
{
    ValueTask<CodexCatalogResolution> ResolveAsync(string? clientVersion, CancellationToken cancellationToken = default);
}

internal sealed class CodexCatalogSourceCache(
    HybridCache memory,    ICodexCatalogSourceClient source,
    ICodexCatalogDiskStore disk,
    CodexBundledCatalog bundled,
    IOptions<CodexModelCatalogOptions> options,
    ILogger<CodexCatalogSourceCache> log,
    TimeProvider? timeProvider = null) : ICodexCatalogSourceCache
{
    /// <summary>Distinct resolve outcome identifying a compile-time bundled response.</summary>
    internal const string BuiltinFallbackOutcome = "builtin-fallback";

    private readonly CodexModelCatalogOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _cleanupRunning;

    // Confirmed upstream absence, keyed by exact canonical version. Deliberately
    // process-local and never persisted: it suppresses re-fetching a tag that
    // does not exist, but must not outlive the process or shadow a tag once it
    // is published. Only this observation is cached — never the bundled bytes.
    private readonly Dictionary<string, DateTimeOffset> _confirmedAbsent = new(StringComparer.Ordinal);

    public async ValueTask<CodexCatalogResolution> ResolveAsync(
        string? clientVersion,
        CancellationToken cancellationToken = default)
    {
        if (!CodexClientVersion.TryParse(clientVersion, out var version))
            return Failure("invalid", "client_version must be one canonical semantic version, for example 0.147.0-alpha.1.2.");

        // Checked before HybridCache because a bundled resolution is never a
        // cache entry; going through the factory would either publish it or
        // throw it away on every request.
        if (TryUseConfirmedAbsence(version)) return BundledFallback(version, "absent-cached", 0);

        var key = CacheKey(version);
        var factoryRan = false;
        CodexCatalogResolution result;
        try
        {
            result = await memory.GetOrCreateAsync(
                key,
                async sharedCancellationToken =>
                {
                    factoryRan = true;
                    return RequireCacheable(await ResolveDiskThenSourceAsync(
                        version, fallback: null, sharedCancellationToken));
                },
                EntryOptions(),
                cancellationToken: cancellationToken);
        }
        catch (CatalogResolutionException exception) { return exception.Resolution; }

        if (factoryRan) return result;
        if (result.Entry is { } entry && IsFresh(entry))
        {
            LogResult(version, "memory", fresh: true, "not-checked", "validated",
                entry.Metadata.Sha256, entry.Metadata.SourceETag, 0);
            return result with { Outcome = "memory" };
        }

        return await RefreshStaleMemoryAsync(version, result, cancellationToken);
    }

    private async Task<CodexCatalogResolution> RefreshStaleMemoryAsync(
        CodexClientVersion version,
        CodexCatalogResolution stale,
        CancellationToken cancellationToken)
    {
        try
        {
            return await memory.GetOrCreateAsync(
                CacheKey(version),
                async sharedCancellationToken => RequireCacheable(
                    await ResolveDiskThenSourceAsync(version, stale.Entry, sharedCancellationToken)),
                RefreshOptions(),
                cancellationToken: cancellationToken);
        }
        catch (CatalogResolutionException exception) { return exception.Resolution; }
    }

    private async Task<CodexCatalogResolution> ResolveDiskThenSourceAsync(
        CodexClientVersion version,
        CodexCatalogCacheEntry? fallback,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var key = version.ToString();
        var lastKnownGood = await disk.TryLoadAsync(version, cancellationToken) ?? fallback;
        if (lastKnownGood is not null && IsFresh(lastKnownGood))
        {
            LogResult(version, "disk", fresh: true, "not-checked", "validated",
                lastKnownGood.Metadata.Sha256, lastKnownGood.Metadata.SourceETag, stopwatch.ElapsedMilliseconds);
            _ = CleanupBestEffortAsync(key);
            return Success(lastKnownGood, "disk");
        }

        var fetched = await source.FetchAsync(version, lastKnownGood?.Metadata.SourceETag, cancellationToken);
        if (fetched.Status == CodexCatalogSourceStatus.NotModified && lastKnownGood is not null)
        {
            try
            {
                var refreshed = RefreshMetadata(lastKnownGood, fetched.ETag);
                var promoted = await disk.PromoteAsync(refreshed, cancellationToken);
                LogResult(version, "source-304", fresh: true, "not-modified", "validated",
                    promoted.Metadata.Sha256, promoted.Metadata.SourceETag, stopwatch.ElapsedMilliseconds);
                _ = CleanupBestEffortAsync(key);
                return Success(promoted, "source-304");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
            {
                log.LogWarning(exception, "Could not persist Codex catalog freshness for {ClientVersion}", version);
                return Stale(version, lastKnownGood, "refresh-persistence-failure", stopwatch.ElapsedMilliseconds);
            }
        }

        if (fetched.Status == CodexCatalogSourceStatus.Modified && fetched.Bytes is { Length: > 0 } bytes && fetched.Sha256 is { } digest)
        {
            try
            {
                if (lastKnownGood is not null &&
                    string.Equals(lastKnownGood.Metadata.Sha256, digest, StringComparison.Ordinal))
                {
                    var refreshed = RefreshMetadata(lastKnownGood, fetched.ETag);
                    var promotedSame = await disk.PromoteAsync(refreshed, cancellationToken);
                    LogResult(version, "source-200-unchanged", fresh: true, "modified-same-digest", "validated",
                        promotedSame.Metadata.Sha256, promotedSame.Metadata.SourceETag, stopwatch.ElapsedMilliseconds);
                    _ = CleanupBestEffortAsync(key);
                    return Success(promotedSame, "source-200-unchanged");
                }
                var now = _time.GetUtcNow();
                var metadata = new CodexCatalogCacheMetadata
                {
                    SchemaVersion = 1,
                    ClientVersion = key,
                    SourceUrl = CodexCatalogSource.BuildUri(version).AbsoluteUri,
                    SourceETag = fetched.ETag,
                    Sha256 = digest,
                    FetchedAtUtc = now,
                    ValidatedAtUtc = now,
                };
                var baseline = CodexCatalogBaseline.Parse(bytes, metadata);
                CodexCatalogBaselineValidator.Validate(baseline);
                var candidate = new CodexCatalogCacheEntry
                {
                    Version = version,
                    SourceUri = CodexCatalogSource.BuildUri(version),
                    SourceBytes = bytes,
                    Metadata = metadata,
                    Baseline = baseline,
                };
                var promoted = await disk.PromoteAsync(candidate, cancellationToken);
                LogResult(version, "source-200", fresh: true, "modified", "validated",
                    promoted.Metadata.Sha256, promoted.Metadata.SourceETag, stopwatch.ElapsedMilliseconds);
                _ = CleanupBestEffortAsync(key);
                return Success(promoted, "source-200");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
            {
                log.LogWarning(exception, "Rejected or could not persist changed Codex catalog for {ClientVersion}", version);
                if (lastKnownGood is not null) return Stale(version, lastKnownGood, "invalid-source", stopwatch.ElapsedMilliseconds);
                LogFailure(version, "modified", "rejected", stopwatch.ElapsedMilliseconds);
                return Failure("invalid-source", "Exact Codex catalog source was invalid or could not be persisted.");
            }
        }

        if (lastKnownGood is not null)
            return Stale(version, lastKnownGood, fetched.Status.ToString(), stopwatch.ElapsedMilliseconds);

        // A 404 is positive information — this tag does not exist — unlike a
        // timeout, which is merely absence of information. Only the former
        // enables the bundled snapshot; every transient failure still fails
        // closed so an outage is never silently downgraded.
        if (fetched.Status == CodexCatalogSourceStatus.NotFound)
        {
            RecordConfirmedAbsence(version);
            if (_options.BuiltinFallbackEnabled)
                return BundledFallback(version, "absent-confirmed", stopwatch.ElapsedMilliseconds);
        }

        LogFailure(version, fetched.Status.ToString(), "unavailable", stopwatch.ElapsedMilliseconds);
        return Failure(fetched.Status.ToString(), fetched.Error ?? "Exact Codex catalog source is unavailable.");
    }

    private bool TryUseConfirmedAbsence(CodexClientVersion version)
    {
        if (!_options.BuiltinFallbackEnabled) return false;
        var key = version.ToString();
        lock (_confirmedAbsent)
        {
            if (!_confirmedAbsent.TryGetValue(key, out var observedAt)) return false;
            if (_time.GetUtcNow() - observedAt < TimeSpan.FromHours(_options.AbsenceTtlHours)) return true;
            _confirmedAbsent.Remove(key);
            return false;
        }
    }

    private void RecordConfirmedAbsence(CodexClientVersion version)
    {
        lock (_confirmedAbsent) _confirmedAbsent[version.ToString()] = _time.GetUtcNow();
    }

    // Returns the bundled snapshot WITHOUT an Entry, so it can never be
    // promoted to disk or published as a validated cache entry for the
    // requested version. Its Baseline carries the snapshot's own captured
    // identity, so the projected ETag differs from any official response.
    private CodexCatalogResolution BundledFallback(CodexClientVersion version, string sourceOutcome, long elapsed)
    {
        log.LogInformation(
            "Codex catalog resolved version={ClientVersion} cache={CacheOutcome} fresh={Fresh} source={SourceOutcome} validation={ValidationOutcome} snapshot_version={SnapshotVersion} digest={Digest} elapsed_ms={ElapsedMs}",
            version, BuiltinFallbackOutcome, false, sourceOutcome, "validated",
            bundled.CapturedVersion, Abbreviate(bundled.Baseline.SourceDigest), elapsed);
        return new CodexCatalogResolution
        {
            Success = true,
            Baseline = bundled.Baseline,
            Outcome = BuiltinFallbackOutcome,
        };
    }

    private CodexCatalogCacheEntry RefreshMetadata(CodexCatalogCacheEntry entry, string? etag)
    {
        var now = _time.GetUtcNow();
        var metadata = entry.Metadata with
        {
            SourceETag = etag ?? entry.Metadata.SourceETag,
            FetchedAtUtc = now,
            ValidatedAtUtc = now,
        };
        return entry with
        {
            Metadata = metadata,
            Baseline = CodexCatalogBaseline.Parse(entry.SourceBytes, metadata),
        };
    }

    private async Task CleanupBestEffortAsync(string currentVersion)
    {
        if (Interlocked.Exchange(ref _cleanupRunning, 1) != 0) return;
        try
        {
            await disk.CleanupAsync(version => version == currentVersion);
        }
        catch (Exception exception)
        {
            log.LogWarning(exception, "Codex catalog cache cleanup failed");
        }
        finally
        {
            Volatile.Write(ref _cleanupRunning, 0);
        }
    }

    private bool IsFresh(CodexCatalogCacheEntry entry) =>
        _time.GetUtcNow() - entry.Metadata.FetchedAtUtc < TimeSpan.FromHours(_options.SourceTtlHours);

    private HybridCacheEntryOptions EntryOptions() => new()
    {
        Expiration = TimeSpan.FromHours(_options.SourceTtlHours),
        LocalCacheExpiration = TimeSpan.FromHours(_options.SourceTtlHours),
        Flags = HybridCacheEntryFlags.DisableDistributedCache,
    };

    private static HybridCacheEntryOptions RefreshOptions() => new()
    {
        Flags = HybridCacheEntryFlags.DisableDistributedCache |
            HybridCacheEntryFlags.DisableLocalCacheRead,
    };

    private static string CacheKey(CodexClientVersion version) => "codex-catalog:" + version;

    // Throwing prevents HybridCache from publishing failures or stale fallback as a
    // fresh L1 entry. The exception itself is shared by HybridCache's stampede task,
    // so the owner and every coalesced waiter observe the same stale outcome without
    // any waiter launching a second refresh. The bundled fallback is excluded for the
    // same reason and one more: caching a compile-time snapshot under the requested
    // version would make the bridge prefer it over the real tag once published.
    private static CodexCatalogResolution RequireCacheable(CodexCatalogResolution resolution) =>
        resolution.Success && resolution.Outcome != "stale" && resolution.Outcome != BuiltinFallbackOutcome
            ? resolution
            : throw new CatalogResolutionException(resolution);

    private CodexCatalogResolution Stale(
        CodexClientVersion version,
        CodexCatalogCacheEntry entry,
        string reason,
        long elapsed)
    {
        log.LogWarning("Serving stale validated Codex catalog for {ClientVersion}; source outcome {SourceOutcome}", version, reason);
        LogResult(version, "stale", fresh: false, reason, "stale-last-known-good",
            entry.Metadata.Sha256, entry.Metadata.SourceETag, elapsed);
        return Success(entry, "stale");
    }

    private void LogResult(
        CodexClientVersion version,
        string outcome,
        bool fresh,
        string sourceOutcome,
        string validationOutcome,
        string digest,
        string? etag,
        long elapsed) =>
        log.LogInformation(
            "Codex catalog resolved version={ClientVersion} cache={CacheOutcome} fresh={Fresh} source={SourceOutcome} validation={ValidationOutcome} digest={Digest} etag={ETag} elapsed_ms={ElapsedMs}",
            version, outcome, fresh, sourceOutcome, validationOutcome,
            Abbreviate(digest), Abbreviate(etag), elapsed);

    private void LogFailure(CodexClientVersion version, string sourceOutcome, string validationOutcome, long elapsed) =>
        log.LogWarning(
            "Codex catalog resolution failed version={ClientVersion} cache=none fresh=false source={SourceOutcome} validation={ValidationOutcome} elapsed_ms={ElapsedMs}",
            version, sourceOutcome, validationOutcome, elapsed);

    private static string Abbreviate(string? value) =>
        string.IsNullOrEmpty(value) ? "none" : value[..Math.Min(12, value.Length)];

    private static CodexCatalogResolution Success(CodexCatalogCacheEntry entry, string outcome) => new()
    {
        Success = true,
        Baseline = entry.Baseline,
        Outcome = outcome,
        Entry = entry,
    };

    private static CodexCatalogResolution Failure(string outcome, string error) => new()
    {
        Success = false,
        Outcome = outcome,
        Error = error,
    };

    private sealed class CatalogResolutionException(CodexCatalogResolution resolution) : Exception
    {
        public CodexCatalogResolution Resolution { get; } = resolution;
    }
}
