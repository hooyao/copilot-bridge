using System.Net;
using System.Text;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins the load-bearing .NET semantic behind the first-byte-timeout CTS lifetime:
/// after <c>SendAsync(ResponseHeadersRead, linkedToken)</c> returns headers, is it
/// safe to DISPOSE the linked CTS before the caller reads the (still-streaming)
/// response body? If HttpClient keeps the send token associated with the content
/// stream and re-registers on it per body read, an early dispose would throw
/// ObjectDisposedException mid-read. This test answers that empirically so the fix
/// for the CTS leak (PR #32 review) is grounded, not guessed.
/// </summary>
public class FirstByteCtsLifetimeProbe
{
    // Handler that returns response headers immediately with a body stream that
    // only produces its bytes on the FIRST ReadAsync AFTER a short delay — i.e. the
    // body is genuinely read after SendAsync has returned, mirroring the SSE relay.
    private sealed class HeadersThenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedBodyStream(Encoding.UTF8.GetBytes("hello-body"))),
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class DelayedBodyStream(byte[] payload) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // A real await between SendAsync-return and byte delivery, so any
            // per-read re-registration on a disposed token would surface here.
            await Task.Yield();
            if (_pos >= payload.Length) return 0;
            var n = Math.Min(buffer.Length, payload.Length - _pos);
            payload.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>
    /// Replicates the exact product pattern: linked CTS from the caller token, arm
    /// with CancelAfter, SendAsync(ResponseHeadersRead, linked), disarm with
    /// CancelAfter(Infinite), DISPOSE the CTS, then read the body with the CALLER's
    /// token. Asserts the body read still succeeds — which is what makes an
    /// immediate `using`/dispose safe in the product.
    /// </summary>
    [Fact]
    public async Task DisposingLinkedCts_AfterHeaders_DoesNotBreakBodyRead()
    {
        using var http = new HttpClient(new HeadersThenBodyHandler());
        using var callerCts = new CancellationTokenSource();
        var ct = callerCts.Token;

        HttpResponseMessage resp;
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.test/x");
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan); // disarm
        }
        finally
        {
            timeoutCts.Dispose(); // the disposal the leak fix would add
        }

        // Body is read AFTER the CTS is disposed, with the caller's token.
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var sr = new StreamReader(body);
        var text = await sr.ReadToEndAsync(ct);

        Assert.Equal("hello-body", text);
        resp.Dispose();
    }
}
