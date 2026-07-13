using System.Net;
using System.Net.Http;
using CopilotBridge.Cli.Update;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Scriptable <see cref="HttpMessageHandler"/> for release-client tests: returns
/// a queued response per request and records every outgoing request so tests can
/// assert on headers (e.g. absence of Authorization) and URLs. No real network.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Requests.Count;
        Requests.Add(request);
        return Task.FromResult(_responder(request, index));
    }

    public static HttpResponseMessage Json(string body, string? nextLink = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (nextLink is not null)
        {
            resp.Headers.TryAddWithoutValidation("Link", $"<{nextLink}>; rel=\"next\"");
        }
        return resp;
    }
}

/// <summary>Deterministic clock that only advances when a test tells it to.</summary>
internal sealed class FakeClock : IMonotonicClock
{
    private long _now;
    public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    public long GetTimestamp() => _now;
    public TimeSpan Elapsed(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);
}
