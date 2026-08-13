using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCatalogOverlayServiceTests
{
    [Fact]
    public async Task ConcurrentColdRequestsShareOneRefresh()
    {
        var release = new TaskCompletionSource<CopilotModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(() => release.Task);
        var service = NewService(client);

        var requests = Enumerable.Range(0, 8).Select(_ => service.GetAsync().AsTask()).ToArray();
        await WaitForAsync(() => client.CallCount == 1);
        release.SetResult(Response("gpt-5.4"));
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, client.CallCount);
        Assert.All(results, result =>
        {
            Assert.True(result.IsValidated);
            Assert.False(result.IsStale);
            Assert.Single(result.Models);
        });
    }

    [Fact]
    public async Task FailedRefreshAfterSuccessUsesAtomicLastKnownGood()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var client = new FakeClient(
            () => Task.FromResult(Response("gpt-5.4")),
            () => Task.FromException<CopilotModelsResponse>(new HttpRequestException("metadata unavailable")));
        var service = NewService(
            client, () => now, failureCooldown: TimeSpan.FromMinutes(10));
        var first = await service.GetAsync();
        now = now.AddMinutes(6);

        var stale = await service.GetAsync();
        now = now.AddMinutes(9);
        var suppressed = await service.GetAsync();

        Assert.True(first.IsValidated);
        Assert.True(stale.IsValidated);
        Assert.True(stale.IsStale);
        Assert.Equal("gpt-5.4", Assert.Single(stale.Models).Id);
        Assert.True(suppressed.IsStale);
        Assert.Equal("gpt-5.4", Assert.Single(suppressed.Models).Id);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task ColdFailureReturnsStaticOnlyFallback()
    {
        var client = new FakeClient(() => Task.FromException<CopilotModelsResponse>(new TimeoutException()));
        var result = await NewService(client).GetAsync();

        Assert.False(result.IsValidated);
        Assert.False(result.IsStale);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task HangingColdRefreshReturnsStaticFallbackWithinCatalogBudget()
    {
        var never = new TaskCompletionSource<CopilotModelsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(() => never.Task);

        var result = await NewService(
            client,
            refreshTimeout: TimeSpan.FromMilliseconds(50)).GetAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsValidated);
        Assert.False(result.IsStale);
        Assert.Empty(result.Models);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task HangingRefreshAfterSuccessReturnsLastKnownGoodWithinCatalogBudget()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var never = new TaskCompletionSource<CopilotModelsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(
            () => Task.FromResult(Response("gpt-5.4")),
            () => never.Task);
        var service = NewService(
            client,
            () => now,
            refreshTimeout: TimeSpan.FromMilliseconds(50));
        _ = await service.GetAsync();
        now = now.AddMinutes(6);

        var result = await service.GetAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.IsValidated);
        Assert.True(result.IsStale);
        Assert.Equal("gpt-5.4", Assert.Single(result.Models).Id);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task FreshTtlDoesNotRefetch()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var client = new FakeClient(() => Task.FromResult(Response("gpt-5.4")));
        var service = NewService(client, () => now);

        _ = await service.GetAsync();
        now = now.AddMinutes(4);
        _ = await service.GetAsync();

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task ColdFailureSuppressesPollsUntilExactCooldownAndWarnsOnce()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var client = new FakeClient(
            () => Task.FromException<CopilotModelsResponse>(
                new HttpRequestException("metadata unavailable")),
            () => Task.FromResult(Response("gpt-5.6")));
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var service = NewService(
            client,
            () => now,
            failureCooldown: TimeSpan.FromSeconds(30),
            logger: loggerFactory.CreateLogger<CodexCatalogOverlayService>());

        var failed = await service.GetAsync();
        for (var second = 1; second < 30; second++)
        {
            now = DateTimeOffset.Parse("2026-08-05T00:00:00Z").AddSeconds(second);
            var suppressed = await service.GetAsync();
            Assert.False(suppressed.IsValidated);
            Assert.Empty(suppressed.Models);
        }

        Assert.False(failed.IsValidated);
        Assert.Equal(1, client.CallCount);
        var warning = Assert.Single(provider.Events, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(30L, warning.Properties["RetryInSeconds"]);

        now = DateTimeOffset.Parse("2026-08-05T00:00:30Z");
        var recovered = await service.GetAsync();

        Assert.True(recovered.IsValidated);
        Assert.Equal("gpt-5.6", Assert.Single(recovered.Models).Id);
        Assert.Equal(2, client.CallCount);
        Assert.Single(provider.Events, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CooldownExpiryCoalescesConcurrentRetryAndSuccessRestoresFreshTtl()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var retryRelease = new TaskCompletionSource<CopilotModelsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(
            () => Task.FromResult(Response("gpt-5.4")),
            () => Task.FromException<CopilotModelsResponse>(new TimeoutException()),
            () => retryRelease.Task,
            () => Task.FromResult(Response("gpt-5.7")));
        var service = NewService(
            client, () => now, failureCooldown: TimeSpan.FromSeconds(30));
        _ = await service.GetAsync();
        now = now.AddMinutes(6);
        _ = await service.GetAsync();

        now = now.AddSeconds(30);
        var callers = Enumerable.Range(0, 12)
            .Select(_ => service.GetAsync().AsTask())
            .ToArray();
        await WaitForAsync(() => client.CallCount == 3);
        retryRelease.SetResult(Response("gpt-5.6"));
        var results = await Task.WhenAll(callers);

        Assert.Equal(3, client.CallCount);
        Assert.All(results, result =>
        {
            Assert.True(result.IsValidated);
            Assert.False(result.IsStale);
            Assert.Equal("gpt-5.6", Assert.Single(result.Models).Id);
        });

        now = now.AddMinutes(4);
        _ = await service.GetAsync();
        Assert.Equal(3, client.CallCount);
        now = now.AddMinutes(1);
        var afterFreshTtl = await service.GetAsync();
        Assert.Equal(4, client.CallCount);
        Assert.Equal("gpt-5.7", Assert.Single(afterFreshTtl.Models).Id);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedRefresh()
    {
        var release = new TaskCompletionSource<CopilotModelsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient(() => release.Task);
        var service = NewService(client);
        using var cts = new CancellationTokenSource();

        var canceledCaller = service.GetAsync(cts.Token).AsTask();
        var survivingCaller = service.GetAsync().AsTask();
        await WaitForAsync(() => client.CallCount == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        release.SetResult(Response("gpt-5.6"));

        var result = await survivingCaller;
        Assert.True(result.IsValidated);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task NewServiceDoesNotInheritAnotherProcessFailureDeadline()
    {
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");
        var client = new FakeClient(
            () => Task.FromException<CopilotModelsResponse>(new HttpRequestException()),
            () => Task.FromException<CopilotModelsResponse>(new HttpRequestException()));

        _ = await NewService(
            client, () => now, failureCooldown: TimeSpan.FromHours(1)).GetAsync();
        _ = await NewService(
            client, () => now, failureCooldown: TimeSpan.FromHours(1)).GetAsync();

        Assert.Equal(2, client.CallCount);
    }

    private static CodexCatalogOverlayService NewService(
        FakeClient client,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? refreshTimeout = null,
        TimeSpan? failureCooldown = null,
        ILogger<CodexCatalogOverlayService>? logger = null) =>
        new(
            client,
            logger ?? NullLogger<CodexCatalogOverlayService>.Instance,
            TimeSpan.FromMinutes(5),
            refreshTimeout ?? TimeSpan.FromSeconds(5),
            failureCooldown ?? TimeSpan.FromMinutes(5),
            utcNow ?? (() => DateTimeOffset.UtcNow));

    private static CopilotModelsResponse Response(string id) => new()
    {
        Data = [new CopilotModel { Id = id, SupportedEndpoints = ["/responses"] }],
    };

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class FakeClient(params Func<Task<CopilotModelsResponse>>[] results) : ICopilotClient
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            return new ValueTask<CopilotModelsResponse>(results[Math.Min(index, results.Length - 1)]());
        }

        public ValueTask<HttpResponseMessage> PostMessagesAsync(ReadOnlyMemory<byte> body, bool vision = false, IReadOnlyList<string>? anthropicBeta = null, IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostResponsesAsync(ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
