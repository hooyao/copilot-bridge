using System.Collections.Concurrent;
using System.Buffers;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCatalogSourceCacheTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public async Task FreshMemoryHitSkipsDiskAndSource()
    {
        var source = new FakeSource((version, _, _) => Task.FromResult(Modified(version, "one")));
        var disk = new MemoryDisk();
        var clock = new ManualTimeProvider(Epoch);
        var cache = Cache(source, disk, clock);

        Assert.True((await cache.ResolveAsync("0.147.0")).Success);
        Assert.True((await cache.ResolveAsync("0.147.0")).Success);

        Assert.Equal(1, source.Calls);
        Assert.Equal(1, disk.Loads);
        Assert.Equal(1, disk.Promotions);
    }

    [Fact]
    public void CatalogHybridCacheSerializerIsAotSafeAndLocalOnly()
    {
        var serializer = new CodexCatalogResolutionHybridCacheSerializer();
        var writer = new ArrayBufferWriter<byte>();
        serializer.Serialize(new CodexCatalogResolution
        {
            Success = false,
            Outcome = "test",
        }, writer);

        Assert.Equal(new byte[] { 1 }, writer.WrittenSpan.ToArray());
        Assert.Throws<InvalidOperationException>(() =>
            serializer.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory)));
    }

    [Fact]
    public void HybridCacheHasNoReflectionSerializerFallback()
    {
        var factory = new ExplicitOnlyHybridCacheSerializerFactory();

        Assert.False(factory.TryCreateSerializer<object>(out var serializer));
        Assert.Null(serializer);
    }

    [Fact]
    public void BridgeRegistersOnlyTheAotSafeHybridCacheSerializerFactory()
    {
        var services = new ServiceCollection();
        services.AddBridgeServer(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services,
            candidate => candidate.ServiceType == typeof(IHybridCacheSerializerFactory));
        Assert.IsType<ExplicitOnlyHybridCacheSerializerFactory>(descriptor.ImplementationInstance);
    }

    [Fact]
    public void BridgeResolvesTheExplicitCatalogSerializerWithoutAnyDefaultFallback()
    {
        var services = new ServiceCollection();
        services.AddBridgeServer(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var factories = provider.GetServices<IHybridCacheSerializerFactory>().ToArray();
        Assert.Single(factories);
        Assert.IsType<ExplicitOnlyHybridCacheSerializerFactory>(factories[0]);
        Assert.IsType<CodexCatalogResolutionHybridCacheSerializer>(
            provider.GetRequiredService<IHybridCacheSerializer<CodexCatalogResolution>>());
        Assert.Null(provider.GetService<IDistributedCache>());
    }

    [Fact]
    public async Task FreshDiskHitRepopulatesMemoryWithoutContactingSource()
    {
        var disk = new MemoryDisk(CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("disk"), Epoch));
        var source = new FakeSource((_, _, _) => throw new InvalidOperationException("source must not run"));
        var cache = Cache(source, disk, new ManualTimeProvider(Epoch.AddHours(23)));

        var first = await cache.ResolveAsync("0.147.0");
        var second = await cache.ResolveAsync("0.147.0");

        Assert.Equal("disk", Assert.Single(first.Baseline!.Models).GetProperty("slug").GetString());
        Assert.True(second.Success);
        Assert.Equal(0, source.Calls);
        Assert.Equal(1, disk.Loads);
    }

    [Fact]
    public async Task ExactTtlBoundaryConditionallyRevalidatesAndRefreshesMetadataOnly()
    {
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("same"), Epoch);
        var disk = new MemoryDisk(old);
        var source = new FakeSource((_, etag, _) =>
        {
            Assert.Equal(old.Metadata.SourceETag, etag);
            return Task.FromResult(new CodexCatalogSourceResult
            {
                Status = CodexCatalogSourceStatus.NotModified,
                ETag = "\"new-etag\"",
            });
        });
        var clock = new ManualTimeProvider(Epoch.AddHours(24));

        var result = await Cache(source, disk, clock).ResolveAsync("0.147.0");

        Assert.Equal("source-304", result.Outcome);
        Assert.Equal(old.SourceBytes, disk.Current!.SourceBytes);
        Assert.Equal(old.Metadata.Sha256, disk.Current.Metadata.Sha256);
        Assert.Equal("\"new-etag\"", disk.Current.Metadata.SourceETag);
        Assert.Equal(clock.GetUtcNow(), disk.Current.Metadata.FetchedAtUtc);
    }

    [Fact]
    public async Task ChangedValidSourceAtomicallyReplacesLastKnownGood()
    {
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("old"), Epoch);
        var disk = new MemoryDisk(old);
        var source = new FakeSource((version, _, _) => Task.FromResult(Modified(version, "new")));

        var result = await Cache(source, disk, new ManualTimeProvider(Epoch.AddDays(2)))
            .ResolveAsync("0.147.0");

        Assert.Equal("source-200", result.Outcome);
        Assert.Equal("new", Assert.Single(result.Baseline!.Models).GetProperty("slug").GetString());
        Assert.NotEqual(old.Metadata.Sha256, disk.Current!.Metadata.Sha256);
    }

    [Fact]
    public async Task SameDigestResponseRefreshesMetadataWhileRetainingKnownGoodBytes()
    {
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("same"), Epoch);
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.Modified,
            Bytes = old.SourceBytes.ToArray(),
            ETag = "\"changed-etag\"",
            Sha256 = old.Metadata.Sha256,
        }));
        var disk = new MemoryDisk(old);

        var result = await Cache(source, disk, new ManualTimeProvider(Epoch.AddDays(2)))
            .ResolveAsync("0.147.0");

        Assert.Equal("source-200-unchanged", result.Outcome);
        Assert.Same(old.SourceBytes, disk.Current!.SourceBytes);
        Assert.Equal("\"changed-etag\"", disk.Current.Metadata.SourceETag);
    }

    [Fact]
    public async Task MetadataRefreshWriteFailureStillServesTheOldLastKnownGood()
    {
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("old"), Epoch);
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.NotModified,
        }));
        var disk = new MemoryDisk(old) { PromotionFailure = new IOException("disk full") };

        var result = await Cache(source, disk, new ManualTimeProvider(Epoch.AddDays(2)))
            .ResolveAsync("0.147.0");

        Assert.True(result.Success);
        Assert.Equal("stale", result.Outcome);
        Assert.Equal(old.Metadata, disk.Current!.Metadata);
    }

    [Fact]
    public async Task InvalidChangedSourceKeepsAndServesStaleLastKnownGood()
    {
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("old"), Epoch);
        var invalid = System.Text.Encoding.UTF8.GetBytes("{\"models\":[]}");
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.Modified,
            Bytes = invalid,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(invalid)),
        }));
        var disk = new MemoryDisk(old);

        var result = await Cache(source, disk, new ManualTimeProvider(Epoch.AddDays(2)))
            .ResolveAsync("0.147.0");

        Assert.True(result.Success);
        Assert.Equal("stale", result.Outcome);
        Assert.Equal(old.Metadata.Sha256, disk.Current!.Metadata.Sha256);
        Assert.Equal("old", Assert.Single(result.Baseline!.Models).GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("TransportFailure")]
    [InlineData("Throttled")]
    [InlineData("ServerError")]
    [InlineData("NotFound")]
    public async Task StaleDiskCacheRemainsUsableWhenSourceCannotRefresh(string statusName)
    {
        var status = Enum.Parse<CodexCatalogSourceStatus>(statusName);
        var old = CodexCatalogDiskStoreTests.Entry(
            "0.147.0", CodexCatalogDiskStoreTests.Catalog("offline"), Epoch);
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = status,
            Error = "unavailable",
        }));

        var result = await Cache(source, new MemoryDisk(old), new ManualTimeProvider(Epoch.AddDays(30)))
            .ResolveAsync("0.147.0");

        Assert.True(result.Success);
        Assert.Equal("stale", result.Outcome);
        Assert.Equal("offline", Assert.Single(result.Baseline!.Models).GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("NotFound")]
    public async Task ColdFailureHasNoCrossVersionOrEmbeddedFallback(string statusName)
    {
        var status = Enum.Parse<CodexCatalogSourceStatus>(statusName);
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = status,
            Error = "unavailable",
        }));

        var result = await Cache(source, new MemoryDisk(), new ManualTimeProvider(Epoch))
            .ResolveAsync("0.147.0-alpha.1.2");

        Assert.False(result.Success);
        Assert.Null(result.Baseline);
        Assert.Equal(status.ToString(), result.Outcome);
    }

    [Fact]
    public async Task SameVersionRequestsCoalesceWhileAnyCallerStillWaits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(async (version, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Modified(version, "shared");
        });
        var cache = Cache(source, new MemoryDisk(), new ManualTimeProvider(Epoch));
        using var cancellation = new CancellationTokenSource();
        var canceledCaller = cache.ResolveAsync("0.147.0", cancellation.Token).AsTask();
        await entered.Task;
        var survivingCaller = cache.ResolveAsync("0.147.0").AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);

        await Task.Delay(30);
        Assert.Equal(1, source.Calls);
        release.TrySetResult();

        Assert.True((await survivingCaller).Success);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task FailedSingleFlightIsRemovedSoLaterRequestCanRetry()
    {
        var calls = 0;
        var source = new FakeSource((version, _, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? new CodexCatalogSourceResult { Status = CodexCatalogSourceStatus.TransportFailure, Error = "offline" }
                : Modified(version, "retry")));
        var cache = Cache(source, new MemoryDisk(), new ManualTimeProvider(Epoch));

        Assert.False((await cache.ResolveAsync("0.147.0")).Success);
        Assert.True((await cache.ResolveAsync("0.147.0")).Success);

        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task DifferentVersionsPerformNetworkWorkConcurrently()
    {
        var active = 0;
        var maximum = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(async (version, _, _) =>
        {
            var now = Interlocked.Increment(ref active);
            if (now == 2) bothEntered.TrySetResult();
            InterlockedExtensions.Max(ref maximum, now);
            await release.Task;
            Interlocked.Decrement(ref active);
            return Modified(version, version.ToString());
        });
        var cache = Cache(source, new MemoryDisk(), new ManualTimeProvider(Epoch));

        var one = cache.ResolveAsync("0.147.0-alpha.1.1").AsTask();
        var two = cache.ResolveAsync("0.147.0-alpha.1.2").AsTask();
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(one, two);

        Assert.Equal(2, maximum);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task StaleMemoryRequestsShareOneHybridCacheRefresh()
    {
        var clock = new ManualTimeProvider(Epoch);
        var behaviorCall = 0;
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(async (version, _, _) =>
        {
            if (Interlocked.Increment(ref behaviorCall) == 1) return Modified(version, "initial");
            refreshEntered.TrySetResult();
            await releaseRefresh.Task;
            return Modified(version, "refreshed");
        });
        var cache = Cache(source, new MemoryDisk(), clock);

        Assert.True((await cache.ResolveAsync("0.147.0")).Success);
        clock.Advance(TimeSpan.FromHours(24));

        var one = cache.ResolveAsync("0.147.0").AsTask();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var two = cache.ResolveAsync("0.147.0").AsTask();
        await Task.Delay(30);

        Assert.Equal(2, source.Calls); // initial + one shared refresh
        releaseRefresh.TrySetResult();
        var results = await Task.WhenAll(one, two);
        Assert.All(results, result =>
            Assert.Equal("refreshed", Assert.Single(result.Baseline!.Models).GetProperty("slug").GetString()));
    }

    [Fact]
    public async Task DiagnosticsAreStructuredAndNeverContainCatalogOrCredentialContents()
    {
        const string catalogCanary = "catalog-body-secret-canary";
        const string credentialCanary = "github_pat_secret_canary";
        var bytes = CodexCatalogDiskStoreTests.Catalog(catalogCanary);
        var fullEtag = "\"etag-1234567890-should-be-abbreviated\"";
        var source = new FakeSource((_, _, _) => Task.FromResult(new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.Modified,
            Bytes = bytes,
            ETag = fullEtag,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Error = credentialCanary,
        }));
        var logger = new CaptureLogger<CodexCatalogSourceCache>();
        var cache = new CodexCatalogSourceCache(
            CreateHybridCache(),
            source,
            new MemoryDisk(),
            Options.Create(new CodexModelCatalogOptions
            {
                SourceTtlHours = 24,
                MaxSourceBytes = 4 * 1024 * 1024,
                RetentionDays = 90,
                MaxRetainedVersions = 32,
            }),
            logger,
            new ManualTimeProvider(Epoch));

        Assert.True((await cache.ResolveAsync("0.147.0-alpha.1.2")).Success);

        var combined = string.Join("\n", logger.Messages);
        Assert.Contains("version=0.147.0-alpha.1.2", combined, StringComparison.Ordinal);
        Assert.Contains("cache=source-200", combined, StringComparison.Ordinal);
        Assert.Contains("fresh=True", combined, StringComparison.Ordinal);
        Assert.Contains("source=modified", combined, StringComparison.Ordinal);
        Assert.Contains("validation=validated", combined, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(catalogCanary, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialCanary, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(fullEtag, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreshHybridCacheEntriesRemainMemoryHitsUntilSourceTtl()
    {
        var source = new FakeSource((version, _, _) =>
            Task.FromResult(Modified(version, version.ToString())));
        var disk = new MemoryDisk();
        var cache = Cache(source, disk, new ManualTimeProvider(Epoch), maxRetainedVersions: 1);

        Assert.True((await cache.ResolveAsync("0.147.1")).Success);
        Assert.True((await cache.ResolveAsync("0.147.2")).Success);
        Assert.True((await cache.ResolveAsync("0.147.3")).Success);
        var old = await cache.ResolveAsync("0.147.1");

        Assert.True(old.Success);
        Assert.Equal("memory", old.Outcome);
        Assert.Equal(3, source.Calls);
    }

    private static CodexCatalogSourceCache Cache(
        ICodexCatalogSourceClient source,
        ICodexCatalogDiskStore disk,
        TimeProvider clock,
        int maxRetainedVersions = 32) => new(
            CreateHybridCache(),
            source,
            disk,
            Options.Create(new CodexModelCatalogOptions
            {
                SourceTtlHours = 24,
                MaxSourceBytes = 4 * 1024 * 1024,
                RetentionDays = 90,
                MaxRetainedVersions = maxRetainedVersions,
            }),
            NullLogger<CodexCatalogSourceCache>.Instance,
            clock);

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

    private static CodexCatalogSourceResult Modified(CodexClientVersion version, string slug)
    {
        var bytes = CodexCatalogDiskStoreTests.Catalog(slug);
        return new CodexCatalogSourceResult
        {
            Status = CodexCatalogSourceStatus.Modified,
            Bytes = bytes,
            ETag = "\"etag-" + version + "\"",
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
        };
    }

    private sealed class FakeSource(
        Func<CodexClientVersion, string?, CancellationToken, Task<CodexCatalogSourceResult>> fetch)
        : ICodexCatalogSourceClient
    {
        public int Calls;

        public async ValueTask<CodexCatalogSourceResult> FetchAsync(
            CodexClientVersion version,
            string? etag,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return await fetch(version, etag, cancellationToken);
        }
    }

    private sealed class MemoryDisk(params CodexCatalogCacheEntry[] entries) : ICodexCatalogDiskStore
    {
        private readonly ConcurrentDictionary<string, CodexCatalogCacheEntry> _entries =
            new(entries.ToDictionary(entry => entry.Version.ToString(), StringComparer.Ordinal), StringComparer.Ordinal);

        public int Loads;
        public int Promotions;
        public Exception? PromotionFailure;
        public CodexCatalogCacheEntry? Current => _entries.Values.SingleOrDefault();

        public ValueTask<CodexCatalogCacheEntry?> TryLoadAsync(
            CodexClientVersion version,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            _entries.TryGetValue(version.ToString(), out var entry);
            return ValueTask.FromResult(entry);
        }

        public ValueTask<CodexCatalogCacheEntry> PromoteAsync(
            CodexCatalogCacheEntry candidate,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Promotions);
            if (PromotionFailure is { } failure) throw failure;
            _entries.AddOrUpdate(candidate.Version.ToString(), candidate, (_, current) =>
                current.Metadata.ValidatedAtUtc > candidate.Metadata.ValidatedAtUtc ? current : candidate);
            return ValueTask.FromResult(_entries[candidate.Version.ToString()]);
        }

        public ValueTask CleanupAsync(
            Func<string, bool> isProtectedVersion,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
