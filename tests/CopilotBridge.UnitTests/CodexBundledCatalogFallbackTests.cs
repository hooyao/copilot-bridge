using System.Text.Json;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: OpenAI ships Codex clients before tagging the matching release, so
/// a brand-new client's exact tag can legitimately not exist. The bridge must
/// still hand that client a Copilot-calibrated catalog instead of failing
/// closed — but only on CONFIRMED absence, never by caching the substitute, and
/// never in a way that outranks the real catalog once it is published.
/// </summary>
public sealed class CodexBundledCatalogFallbackTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
    private const string UnpublishedVersion = "0.147.0";

    [Fact]
    public void BundledSnapshotIsValidAndCarriesItsOwnCapturedIdentity()
    {
        // Contract: the snapshot must be a complete, validated official catalog
        // whose provenance is its OWN captured version — forging the requested
        // version would make two different payloads indistinguishable.
        var bundled = CodexBundledCatalog.Load();

        Assert.NotEmpty(bundled.Baseline.Models);
        Assert.Equal(bundled.CapturedVersion, bundled.Baseline.SourceVersion);
        Assert.NotEqual(UnpublishedVersion, bundled.CapturedVersion);
        Assert.Matches("^[0-9a-f]{64}$", bundled.Baseline.SourceDigest);
    }

    [Fact]
    public async Task ConfirmedAbsenceServesACatalogInsteadOfFailingClosed()
    {
        // Contract: this is the whole point of the change — the 400 storm the
        // real bridge log showed every ~3 minutes becomes a served catalog.
        var source = NotFoundSource();

        var result = await Cache(source).ResolveAsync(UnpublishedVersion);

        Assert.True(result.Success);
        Assert.NotNull(result.Baseline);
        Assert.NotEmpty(result.Baseline!.Models);
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("TransportFailure")]
    [InlineData("Throttled")]
    [InlineData("ServerError")]
    [InlineData("Failed")]
    public async Task TransientFailureStillFailsClosed(string statusName)
    {
        // Contract: a 404 is positive information; a timeout is the ABSENCE of
        // information. Falling back on the latter would mask an outage.
        var source = new CountingSource(Enum.Parse<CodexCatalogSourceStatus>(statusName));

        var result = await Cache(source).ResolveAsync(UnpublishedVersion);

        Assert.False(result.Success);
        Assert.Null(result.Baseline);
    }

    [Fact]
    public async Task OperatorCanRestoreStrictFailClosedBehaviour()
    {
        var result = await Cache(NotFoundSource(), builtinFallbackEnabled: false)
            .ResolveAsync(UnpublishedVersion);

        Assert.False(result.Success);
        Assert.Null(result.Baseline);
    }

    [Fact]
    public void FallbackDefaultsOnSoUpgradedInstallationsGainItWithoutEditingConfig()
    {
        // Contract: an installation whose appsettings.json predates this key
        // must still get the fallback, mirroring how Enabled defaults on.
        var options = new CodexModelCatalogOptions();

        Assert.True(options.BuiltinFallbackEnabled);
        Assert.InRange(options.AbsenceTtlHours, 1, options.SourceTtlHours - 1);
    }

    [Fact]
    public async Task BundledSnapshotIsNeverPersistedAsALastKnownGood()
    {
        // Contract: caching it would make the bridge prefer a compile-time
        // snapshot over the real tag once OpenAI publishes it.
        var disk = new RecordingDisk();

        var result = await Cache(NotFoundSource(), disk).ResolveAsync(UnpublishedVersion);

        Assert.True(result.Success);
        Assert.Equal(0, disk.Promotions);
    }

    [Fact]
    public async Task BundledResolutionCarriesNoCacheEntryThatCouldBeServedAsAHit()
    {
        // Contract: a resolution with a cache Entry is eligible to be returned
        // as a fresh cached hit on later requests, which is precisely how a
        // compile-time snapshot would shadow a tag published afterwards. The
        // bundled path must therefore produce a baseline WITHOUT an entry —
        // this, not RequireCacheable, is what makes the never-cache rule hold.
        var bundledResult = await Cache(NotFoundSource()).ResolveAsync(UnpublishedVersion);

        Assert.True(bundledResult.Success);
        Assert.NotNull(bundledResult.Baseline);
        Assert.Null(bundledResult.Entry);
    }

    [Fact]
    public async Task BundledSnapshotNeverBecomesAFreshCachedHitForALaterRequest()
    {
        // Contract: the observable consequence of never caching the fallback —
        // once the real tag exists, the very next resolve must return it, with
        // no stale-memory or fresh-hit path able to keep serving the snapshot.
        // The absence window is bypassed here so the cache layer, not the
        // negative cache, is what is under test.
        var published = false;
        var source = new SwitchingSource(() => published);
        var clock = new ManualTimeProvider(Epoch);
        var cache = Cache(source, clock: clock, absenceTtlHours: 1);

        var first = await cache.ResolveAsync(UnpublishedVersion);
        Assert.Equal(CodexBundledCatalog.Load().CapturedVersion, first.Baseline!.SourceVersion);

        published = true;
        clock.Advance(TimeSpan.FromHours(2));

        var second = await cache.ResolveAsync(UnpublishedVersion);
        Assert.Equal(UnpublishedVersion, second.Baseline!.SourceVersion);
        Assert.NotEqual(CodexBundledCatalog.Load().CapturedVersion, second.Baseline!.SourceVersion);
    }

    [Fact]
    public async Task ConfirmedAbsenceStopsRefetchingTheMissingTag()
    {
        // Contract: the real bridge re-fetched a nonexistent tag every ~3
        // minutes. One confirmed 404 must suppress the rest within its TTL.
        var source = NotFoundSource();
        var cache = Cache(source);

        Assert.True((await cache.ResolveAsync(UnpublishedVersion)).Success);
        Assert.True((await cache.ResolveAsync(UnpublishedVersion)).Success);
        Assert.True((await cache.ResolveAsync(UnpublishedVersion)).Success);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task PublishedTagSupersedesTheBundledSnapshotAfterAbsenceExpires()
    {
        // Contract: the fallback must be self-healing. Once the tag exists and
        // the absence observation lapses, the REAL catalog must win.
        var published = false;
        var source = new SwitchingSource(() => published);
        var clock = new ManualTimeProvider(Epoch);
        var cache = Cache(source, clock: clock);

        var fallback = await cache.ResolveAsync(UnpublishedVersion);
        Assert.True(fallback.Success);
        Assert.Equal(CodexBundledCatalog.Load().CapturedVersion, fallback.Baseline!.SourceVersion);

        published = true;
        clock.Advance(TimeSpan.FromHours(7));
        var real = await cache.ResolveAsync(UnpublishedVersion);

        Assert.True(real.Success);
        Assert.Equal(UnpublishedVersion, real.Baseline!.SourceVersion);
    }

    [Fact]
    public async Task ValidatedStaleEntryOutranksTheBundledSnapshot()
    {
        // Contract: the client's OWN catalog, even stale, is always a better
        // answer than another version's snapshot.
        var bytes = CodexCatalogDiskStoreTests.Catalog("client-own-model");
        var disk = new RecordingDisk(CodexCatalogDiskStoreTests.Entry(UnpublishedVersion, bytes, Epoch));

        var result = await Cache(NotFoundSource(), disk, new ManualTimeProvider(Epoch.AddDays(30)))
            .ResolveAsync(UnpublishedVersion);

        Assert.True(result.Success);
        Assert.Equal("client-own-model", Assert.Single(result.Baseline!.Models).GetProperty("slug").GetString());
    }

    [Fact]
    public async Task AbsenceOfOneVersionDoesNotSuppressAnother()
    {
        var source = new PerVersionSource(UnpublishedVersion);
        var cache = Cache(source);

        var absent = await cache.ResolveAsync(UnpublishedVersion);
        var present = await cache.ResolveAsync("0.146.0");

        Assert.True(absent.Success);
        Assert.True(present.Success);
        Assert.Equal("0.146.0", present.Baseline!.SourceVersion);
    }

    [Fact]
    public async Task BundledAndOfficialResponsesNeverShareAValidator()
    {
        // Contract: a cached ETag must not let a client keep a bundled body
        // after its real catalog is available.
        var projector = new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new CopilotModelRegistry(),
            NullLogger<CodexCatalogProjector>.Instance);
        var bundled = await Cache(NotFoundSource()).ResolveAsync(UnpublishedVersion);
        var officialSource = new SwitchingSource(() => true);
        var official = await Cache(officialSource).ResolveAsync(UnpublishedVersion);

        var bundledTag = projector.Project(bundled.Baseline!, [], false).ETag;
        var officialTag = projector.Project(official.Baseline!, [], false).ETag;

        Assert.NotEqual(bundledTag, officialTag);
    }

    [Fact]
    public async Task ServedBundledFallbackIsIdentifiableInDiagnostics()
    {
        // Contract: an operator must be able to tell a fallback response from a
        // real one without reading the body.
        var logger = new CaptureLogger<CodexCatalogSourceCache>();

        await Cache(NotFoundSource(), logger: logger).ResolveAsync(UnpublishedVersion);

        var combined = string.Join("\n", logger.Messages);
        Assert.Contains("builtin-fallback", combined, StringComparison.Ordinal);
        Assert.Contains(UnpublishedVersion, combined, StringComparison.Ordinal);
        Assert.Contains(CodexBundledCatalog.Load().CapturedVersion, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledBaselineReceivesTheSameCopilotUpliftAsAnOfficialOne()
    {
        // Contract: the whole value of serving the fallback is that the client
        // still learns Copilot's real capacity. The bundled baseline must go
        // through the SAME projection as an official one — the 272,000 catalog
        // value must be lifted to Copilot's live 1M-class limits, with no
        // bundled-specific special-casing.
        var projector = new CodexCatalogProjector(
            new CodexModelProfileCatalog(),
            new CopilotModelRegistry(),
            NullLogger<CodexCatalogProjector>.Instance);
        var liveFacts = new[] { Live("gpt-5.6-sol", 1_050_000, 922_000, 128_000) };

        var bundled = await Cache(NotFoundSource()).ResolveAsync(UnpublishedVersion);
        var projected = projector.Project(bundled.Baseline!, liveFacts, liveOverlayValidated: true);

        var model = projected.Models.Single(
            candidate => candidate.GetProperty("slug").GetString() == "gpt-5.6-sol");
        Assert.Equal(1_050_000, model.GetProperty("context_window").GetInt32());
        Assert.Equal(1_050_000, model.GetProperty("max_context_window").GetInt32());
        var compact = model.GetProperty("auto_compact_token_limit").GetInt32();
        Assert.InRange(compact, 1, 922_000 - 1);
    }

    private static CountingSource NotFoundSource() => new(CodexCatalogSourceStatus.NotFound);

    private static CopilotModel Live(string id, int? total, int? prompt, int? output) => new()
    {
        Id = id,
        SupportedEndpoints = ["/responses"],
        Capabilities = new CopilotModelCapabilities
        {
            Limits = new CopilotModelLimits
            {
                MaxContextWindowTokens = total,
                MaxPromptTokens = prompt,
                MaxOutputTokens = output,
            },
        },
    };

    private static CodexCatalogSourceResult Modified(byte[] bytes) => new()
    {
        Status = CodexCatalogSourceStatus.Modified,
        Bytes = bytes,
        ETag = "\"etag-real\"",
        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
    };

    private static CodexCatalogSourceCache Cache(
        ICodexCatalogSourceClient source,
        ICodexCatalogDiskStore? disk = null,
        TimeProvider? clock = null,
        bool builtinFallbackEnabled = true,
        ILogger<CodexCatalogSourceCache>? logger = null,
        int absenceTtlHours = 6) => new(
            CreateHybridCache(),
            source,
            disk ?? new RecordingDisk(),
            CodexBundledCatalog.Load(),
            Options.Create(new CodexModelCatalogOptions
            {
                SourceTtlHours = 24,
                AbsenceTtlHours = absenceTtlHours,
                BuiltinFallbackEnabled = builtinFallbackEnabled,
                MaxSourceBytes = 4 * 1024 * 1024,
                RetentionDays = 90,
                MaxRetainedVersions = 32,
            }),
            logger ?? NullLogger<CodexCatalogSourceCache>.Instance,
            clock ?? new ManualTimeProvider(Epoch));

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache, ThrowingDistributedCache>();
        services.AddHybridCache(options =>
        {
            options.MaximumKeyLength = 128;
            options.MaximumPayloadBytes = 16 * 1024 * 1024;
        }).AddSerializer(new CodexCatalogResolutionHybridCacheSerializer());
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private sealed class CountingSource(CodexCatalogSourceStatus status) : ICodexCatalogSourceClient
    {
        public int Calls;

        public ValueTask<CodexCatalogSourceResult> FetchAsync(
            CodexClientVersion version, string? etag, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(new CodexCatalogSourceResult { Status = status });
        }
    }

    private sealed class SwitchingSource(Func<bool> published) : ICodexCatalogSourceClient
    {
        public ValueTask<CodexCatalogSourceResult> FetchAsync(
            CodexClientVersion version, string? etag, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(published()
                ? Modified(CodexCatalogDiskStoreTests.Catalog("real-model"))
                : new CodexCatalogSourceResult { Status = CodexCatalogSourceStatus.NotFound });
    }

    private sealed class PerVersionSource(string absentVersion) : ICodexCatalogSourceClient
    {
        public ValueTask<CodexCatalogSourceResult> FetchAsync(
            CodexClientVersion version, string? etag, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(version.ToString() == absentVersion
                ? new CodexCatalogSourceResult { Status = CodexCatalogSourceStatus.NotFound }
                : Modified(CodexCatalogDiskStoreTests.Catalog("other-model")));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(state?.ToString() ?? formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("L2 is forbidden.");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("L2 is forbidden.");

        public void Refresh(string key) => throw new InvalidOperationException("L2 is forbidden.");

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("L2 is forbidden.");

        public void Remove(string key) => throw new InvalidOperationException("L2 is forbidden.");

        public Task RemoveAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("L2 is forbidden.");

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new InvalidOperationException("L2 is forbidden.");

        public Task SetAsync(
            string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("L2 is forbidden.");
    }

    private sealed class RecordingDisk(CodexCatalogCacheEntry? seeded = null) : ICodexCatalogDiskStore    {
        public int Promotions;

        public ValueTask<CodexCatalogCacheEntry?> TryLoadAsync(
            CodexClientVersion version, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(seeded is not null && seeded.Version.ToString() == version.ToString()
                ? seeded
                : null);

        public ValueTask<CodexCatalogCacheEntry> PromoteAsync(
            CodexCatalogCacheEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Promotions);
            return ValueTask.FromResult(entry);
        }

        public ValueTask CleanupAsync(
            Func<string, bool> isProtectedVersion, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
