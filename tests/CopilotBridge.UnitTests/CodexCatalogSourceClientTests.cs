using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCatalogSourceClientTests
{
    [Fact]
    public async Task ExactPrereleaseRequestUsesConditionalEtagAndNoCredentials()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"new-etag\"") },
        });
        var client = Build(handler);
        Assert.True(CodexClientVersion.TryParse("0.147.0-alpha.1.2", out var version));

        var result = await client.FetchAsync(version, "\"old-etag\"");

        Assert.Equal(CodexCatalogSourceStatus.NotModified, result.Status);
        Assert.Equal("\"new-etag\"", result.ETag);
        Assert.Equal(
            "https://raw.githubusercontent.com/openai/codex/rust-v0.147.0-alpha.1.2/codex-rs/models-manager/models.json",
            handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("\"old-etag\"", Assert.Single(handler.Request.Headers.IfNoneMatch).ToString());
        Assert.Null(handler.Request.Headers.Authorization);
        Assert.False(handler.Request.Headers.Contains("X-GitHub-Token"));
    }

    [Fact]
    public async Task SuccessfulBodyIsBoundedAndHasExactDigest()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"models\":[]}");
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
            Headers = { ETag = new EntityTagHeaderValue("\"v1\"") },
        });
        var client = Build(handler);
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal(CodexCatalogSourceStatus.Modified, result.Status);
        Assert.Equal(bytes, result.Bytes);
        Assert.Equal("a6fe9ec6e26a38d99fca418b69826e0238b7a2bb319ff05eb153a6fcfd1fa28d", result.Sha256);
    }

    [Fact]
    public async Task StreamingBodyCannotExceedConfiguredLimit()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[65_537]),
        });
        var client = Build(handler, maxBytes: 65_536);
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal(CodexCatalogSourceStatus.Failed, result.Status);
        Assert.Null(result.Bytes);
        Assert.Contains("size limit", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NotFound")]
    [InlineData(HttpStatusCode.TooManyRequests, "Throttled")]
    [InlineData(HttpStatusCode.InternalServerError, "ServerError")]
    [InlineData(HttpStatusCode.Forbidden, "Failed")]
    [InlineData(HttpStatusCode.Redirect, "Failed")]
    public async Task SourceStatusesRemainDistinct(HttpStatusCode status, string expected)
    {
        var client = Build(new CaptureHandler(_ => new HttpResponseMessage(status)));
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal(expected, result.Status.ToString());
    }

    [Fact]
    public async Task TransportFailureIsNotConfusedWithAnHttpStatus()
    {
        var client = Build(new DelegateHandler((_, _) =>
            throw new HttpRequestException("offline")));
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal("TransportFailure", result.Status.ToString());
        Assert.Contains("offline", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyStreamFailureIsAStaleEligibleTransportOutcome()
    {
        var client = Build(new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingReadStream()),
        }));
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal("TransportFailure", result.Status.ToString());
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void ProductionSourceHandlerNeverFollowsRedirectsAndBoundsConnectTime()
    {
        using var handler = CodexCatalogSourceHttpHandler.Create();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Fact]
    public async Task RequestTimeoutHasItsOwnOutcome()
    {
        var client = Build(
            new DelegateHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }),
            timeoutSeconds: 1);
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));

        var result = await client.FetchAsync(version, null);

        Assert.Equal("Timeout", result.Status.ToString());
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBecomingStaleEligibleFailure()
    {
        var client = Build(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }));
        Assert.True(CodexClientVersion.TryParse("0.147.0", out var version));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.FetchAsync(version, null, cancellation.Token));
    }

    private static CodexCatalogSourceClient Build(
        HttpMessageHandler handler,
        int maxBytes = 4 * 1024 * 1024,
        int timeoutSeconds = 10) =>
        new(new OneClientFactory(handler), Options.Create(new CodexModelCatalogOptions
        {
            SourceTimeoutSeconds = timeoutSeconds,
            MaxSourceBytes = maxBytes,
        }));

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response(request));
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(UpstreamHttpClientNames.CodexCatalogSource, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
