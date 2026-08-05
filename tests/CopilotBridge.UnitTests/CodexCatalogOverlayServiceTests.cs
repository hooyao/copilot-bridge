using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Copilot;
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
        var service = NewService(client, () => now);
        var first = await service.GetAsync();
        now = now.AddMinutes(6);

        var stale = await service.GetAsync();

        Assert.True(first.IsValidated);
        Assert.True(stale.IsValidated);
        Assert.True(stale.IsStale);
        Assert.Equal("gpt-5.4", Assert.Single(stale.Models).Id);
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

    private static CodexCatalogOverlayService NewService(FakeClient client, Func<DateTimeOffset>? utcNow = null) =>
        new(client, NullLogger<CodexCatalogOverlayService>.Instance, TimeSpan.FromMinutes(5), utcNow ?? (() => DateTimeOffset.UtcNow));

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
