using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class ResponsesFailureDetailReaderTests
{
    [Fact]
    public async Task StalledDiagnosticTailIsBoundedAndThePendingReadIsCancelled()
    {
        using var readCts = new CancellationTokenSource();
        var stopped = false;
        await using var tail = SilentTail(() => stopped = true, readCts.Token).GetAsyncEnumerator();
        var original = new UpstreamResponseFailedException(null);
        var timer = Stopwatch.StartNew();
        var result = await ResponsesFailureDetailReader.EnrichAsync(tail, readCts, original, default);
        Assert.Same(original, result);
        Assert.True(stopped, "The abandoned read must be observed and stopped before returning.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ClientCancellationWinsOverDiagnosticFallback()
    {
        using var clientCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(clientCts.Token);
        await using var tail = SilentTail(() => { }, readCts.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResponsesFailureDetailReader.EnrichAsync(
            tail, readCts, new UpstreamResponseFailedException(null), clientCts.Token));
    }

    [Theory]
    [InlineData("{\"type\":\"ping\",\"code\":\"invalid_request_body\"}")]
    [InlineData("not-json")]
    public async Task OnlyTheFollowingStructuredErrorCanEnrichAFailure(string data)
    {
        using var readCts = new CancellationTokenSource();
        await using var tail = OneEvent(data).GetAsyncEnumerator();
        var original = new UpstreamResponseFailedException(null);
        Assert.Same(original, await ResponsesFailureDetailReader.EnrichAsync(tail, readCts, original, default));
    }

    [Theory]
    [InlineData("server_error", "arbitrary secret text", "server_error")]
    [InlineData("bad code\nsecret", "Encrypted function output content could not be decrypted or decoded. suffix", "unknown")]
    public void UnreviewedTextAndUnsafeCodesNeverCrossTheTypedBoundary(string code, string message, string expected)
    {
        var failure = new UpstreamResponseFailedException(code, message);
        Assert.Equal(expected, failure.Code);
        Assert.Equal("the upstream model backend failed mid-stream", failure.ClientMessage);
        Assert.DoesNotContain("secret", failure.Message);
    }

    private static async IAsyncEnumerable<SseItem<string>> SilentTail(
        Action stopped, [EnumeratorCancellation] CancellationToken ct)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        finally { stopped(); }
        yield break;
    }

    private static async IAsyncEnumerable<SseItem<string>> OneEvent(string data)
    {
        await Task.Yield();
        yield return new SseItem<string>(data, "error");
    }
}
