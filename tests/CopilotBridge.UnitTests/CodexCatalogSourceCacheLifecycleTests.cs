using System.Collections.Concurrent;
using System.Security.Cryptography;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCatalogSourceCacheLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-codex-lifecycle-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OnlinePublicationMemoryHitRevalidationChangeRestartAndOfflineFallbackFormOneCoherentCache()
    {
        const string version = "0.147.0-alpha.1.2";
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        var source = new ScriptedSource(
            Modified("first", "\"etag-1\""),
            new CodexCatalogSourceResult { Status = CodexCatalogSourceStatus.NotModified, ETag = "\"etag-1\"" },
            Modified("changed", "\"etag-2\""));
        using var disk = Disk(clock);
        var cache = Cache(source, disk, clock);

        var first = await cache.ResolveAsync(version);
        var memory = await cache.ResolveAsync(version);
        Assert.Equal("source-200", first.Outcome);
        Assert.Equal("memory", memory.Outcome);
        Assert.Equal(1, source.Calls);
        Assert.Single(Directory.EnumerateFiles(_root, "catalog-*.cache"));

        clock.Advance(TimeSpan.FromHours(24));
        var unchanged = await cache.ResolveAsync(version);
        Assert.Equal("source-304", unchanged.Outcome);
        Assert.Equal([null, "\"etag-1\""], source.ObservedEtags);

        clock.Advance(TimeSpan.FromHours(24));
        var changed = await cache.ResolveAsync(version);
        Assert.Equal("source-200", changed.Outcome);
        Assert.Equal("changed", Assert.Single(changed.Baseline!.Models).GetProperty("slug").GetString());
        Assert.Equal([null, "\"etag-1\"", "\"etag-1\""], source.ObservedEtags);

        clock.Advance(TimeSpan.FromDays(30));
        var offline = new ScriptedSource(new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.TransportFailure,
            Error = "GitHub unavailable",
        });
        var restarted = Cache(offline, disk, clock);
        var stale = await restarted.ResolveAsync(version);

        Assert.True(stale.Success);
        Assert.Equal("stale", stale.Outcome);
        Assert.Equal("changed", Assert.Single(stale.Baseline!.Models).GetProperty("slug").GetString());
        Assert.Equal(1, offline.Calls);
        Assert.Equal("\"etag-2\"", Assert.Single(offline.ObservedEtags));
    }

    private CodexCatalogDiskStore Disk(TimeProvider time) => new(
        Options.Create(OptionsValue()),
        NullLogger<CodexCatalogDiskStore>.Instance,
        timeProvider: time);

    private CodexCatalogSourceCache Cache(
        ICodexCatalogSourceClient source,
        ICodexCatalogDiskStore disk,
        TimeProvider time) => new(
            CreateHybridCache(),
            source,
            disk,
            Options.Create(OptionsValue()),
            NullLogger<CodexCatalogSourceCache>.Instance,
            time);

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

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Unexpected() =>
            new("Catalog HybridCache must not use IDistributedCache.");

        public byte[]? Get(string key) => throw Unexpected();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Unexpected();
        public void Refresh(string key) => throw Unexpected();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Unexpected();
        public void Remove(string key) => throw Unexpected();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Unexpected();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Unexpected();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Unexpected();
    }

    private CodexModelCatalogOptions OptionsValue() => new()
    {
        CacheDirectory = _root,
        SourceTtlHours = 24,
        SourceTimeoutSeconds = 10,
        MaxSourceBytes = 4 * 1024 * 1024,
        RetentionDays = 90,
        MaxRetainedVersions = 32,
    };

    private static CodexCatalogSourceResult Modified(string slug, string etag)
    {
        var bytes = CodexCatalogDiskStoreTests.Catalog(slug);
        return new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.Modified,
            ETag = etag,
            Bytes = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
    }

    private sealed class ScriptedSource(params CodexCatalogSourceResult[] results) : ICodexCatalogSourceClient
    {
        private readonly ConcurrentQueue<CodexCatalogSourceResult> _results = new(results);
        public readonly List<string?> ObservedEtags = [];
        public int Calls;

        public ValueTask<CodexCatalogSourceResult> FetchAsync(
            CodexClientVersion version,
            string? etag,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            lock (ObservedEtags) ObservedEtags.Add(etag);
            return _results.TryDequeue(out var result)
                ? ValueTask.FromResult(result)
                : throw new InvalidOperationException("Source script exhausted.");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
