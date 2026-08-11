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

    /// <summary>
    /// A refusal shaped like GitHub's real rate-limit response: the primary limit
    /// is reported as <c>x-ratelimit-remaining: 0</c> (verified against a live
    /// anonymous 403 from the Releases API).
    /// </summary>
    public static HttpResponseMessage RateLimited(HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        var resp = Json("""{"message":"API rate limit exceeded for 203.0.113.7."}""", status: status);
        resp.Headers.TryAddWithoutValidation("x-ratelimit-limit", "60");
        resp.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        resp.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1786420368");
        return resp;
    }
}

/// <summary>Deterministic clock that only advances when a test tells it to.</summary>
internal sealed class FakeClock : IMonotonicClock
{
    private long _now;

    /// <summary>Every delay the code under test awaited, in order.</summary>
    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan by) => _now += (long)by.TotalMilliseconds;
    public long GetTimestamp() => _now;
    public TimeSpan Elapsed(long startTimestamp) => TimeSpan.FromMilliseconds(_now - startTimestamp);

    /// <summary>
    /// Records the delay and advances virtual time by it, without sleeping — so a
    /// retry consumes the traversal budget in tests exactly as it does in
    /// production, but the suite stays wall-clock-free.
    /// </summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }
}
