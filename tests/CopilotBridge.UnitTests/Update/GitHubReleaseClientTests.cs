using System.Net;
using CopilotBridge.Cli.Update;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="GitHubReleaseClient"/> from the "Anonymous
/// GitHub release discovery" requirement. All HTTP is faked; the whole-traversal
/// bounds, cycle guard, and cancellation semantics are exercised without network.
/// </summary>
public class GitHubReleaseClientTests
{
    private static readonly TimeSpan PerReq = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static GitHubReleaseClient Client(
        FakeHttpMessageHandler handler, IMonotonicClock? clock = null, int maxPages = 20)
        => new(new HttpClient(handler), PerReq, Deadline, clock, maxPages);

    /// <summary>
    /// A client whose rate-limit retry policy is set explicitly, for the retry
    /// contract tests. The delay runs on the injected clock, so it never sleeps.
    /// </summary>
    private static GitHubReleaseClient RetryClient(
        FakeHttpMessageHandler handler,
        IMonotonicClock clock,
        int maxRateLimitRetries,
        TimeSpan? retryDelay = null)
        => new(new HttpClient(handler), PerReq, Deadline, clock, 20, maxRateLimitRetries,
            retryDelay ?? TimeSpan.FromMilliseconds(250));

    private const string OneReleasePage = """
        [ { "tag_name": "v1.0.1", "draft": false, "prerelease": false, "assets": [] } ]
        """;

    [Fact]
    public async Task Single_page_succeeds_and_sends_no_auth_header()
    {
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(OneReleasePage));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Releases);
        Assert.Equal("v1.0.1", result.Releases[0].TagName);

        var req = Assert.Single(handler.Requests);
        Assert.Null(req.Headers.Authorization);
        Assert.Contains("api.github.com", req.RequestUri!.ToString());
        Assert.Contains(req.Headers.UserAgent, ua => ua.Product?.Name == "copilot-bridge");
    }

    [Fact]
    public async Task Multiple_pages_are_all_collected()
    {
        var page1 = """[ { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [] } ]""";
        var page2 = """[ { "tag_name": "v1.1.0", "draft": false, "prerelease": false, "assets": [] } ]""";
        var handler = new FakeHttpMessageHandler((_, i) => i == 0
            ? FakeHttpMessageHandler.Json(page1, nextLink: "https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100&page=2")
            : FakeHttpMessageHandler.Json(page2));

        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Releases.Count);
    }

    [Fact]
    public async Task Empty_release_list_succeeds_with_zero()
    {
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("[]"));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public async Task Malformed_json_fails_open()
    {
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json("{ not json"));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task Persistent_rate_limit_fails_open_after_exhausting_retries()
    {
        // Contract: a 403 that never clears must still fail open — the update check
        // may never block startup, however many retries it is allowed.
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.RateLimited());
        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 3)
            .DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("rate limit", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public async Task Rate_limit_is_retried_and_a_later_success_is_used(HttpStatusCode limited)
    {
        // Contract: GitHub's anonymous quota is keyed on the SOURCE IP and shared
        // with every other client behind the same NAT, so a 403/429 describes the
        // shared bucket at that instant — NOT a verdict on this check. Discovery
        // must retry rather than report failure on the first refusal, because a
        // retry on a fresh connection can be served by a different egress address
        // that still has quota (measured live: with one address exhausted, 10 of 12
        // fresh-connection attempts succeeded).
        var handler = new FakeHttpMessageHandler((_, i) => i < 2
            ? FakeHttpMessageHandler.RateLimited(limited)
            : FakeHttpMessageHandler.Json(OneReleasePage));

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
            .DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("v1.0.1", Assert.Single(result.Releases).TagName);
        Assert.Equal(3, handler.Requests.Count); // two refusals, then the success
    }

    [Fact]
    public async Task Rate_limit_retries_are_bounded_by_the_configured_count()
    {
        // Contract: the retry allowance is finite and honoured exactly — an
        // unbounded retry would turn a rate-limited GitHub into a startup hang.
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.RateLimited());

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 3)
            .DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(4, handler.Requests.Count); // the initial attempt + 3 retries
    }

    [Fact]
    public async Task Rate_limit_retry_never_runs_past_the_overall_deadline()
    {
        // Contract: the traversal deadline outranks the retry allowance. A retry
        // must not be STARTED when its own delay would carry the traversal past the
        // deadline — sleeping first and discovering the overrun afterwards still
        // delays startup by that delay. Here each attempt burns 9s of the 30s budget
        // and the retry delay is a further 10s: after three attempts (27s) there is
        // budget left, so a client that only checks the deadline at the top of the
        // loop would sleep 10s more and overshoot to 37s. Asserting on ELAPSED time
        // (not attempt count) is what pins the pre-retry guard.
        var clock = new FakeClock();
        var start = clock.GetTimestamp();
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(9));
            return FakeHttpMessageHandler.RateLimited();
        });

        var result = await RetryClient(handler, clock, maxRateLimitRetries: 100,
            retryDelay: TimeSpan.FromSeconds(10)).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(clock.Elapsed(start) <= Deadline,
            $"retrying overshot the {Deadline.TotalSeconds}s deadline: {clock.Elapsed(start).TotalSeconds}s");
    }

    [Fact]
    public async Task Rate_limit_retry_waits_between_attempts()
    {
        // Contract: retries are spaced, not a hot loop — an immediate re-request
        // hammers an already-refusing endpoint.
        var clock = new FakeClock();
        var handler = new FakeHttpMessageHandler((_, i) => i < 2
            ? FakeHttpMessageHandler.RateLimited()
            : FakeHttpMessageHandler.Json(OneReleasePage));

        var result = await RetryClient(handler, clock, maxRateLimitRetries: 5,
            retryDelay: TimeSpan.FromMilliseconds(250)).DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, clock.Delays.Count);
        Assert.All(clock.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(250), d));
    }

    [Fact]
    public async Task Rate_limit_retry_does_not_trip_the_pagination_cycle_guard()
    {
        // Contract: a retry re-fetches the SAME url by design. That must not be
        // mistaken for GitHub looping pagination back on itself — otherwise the
        // retry path reports a bogus "cycle" instead of recovering.
        var handler = new FakeHttpMessageHandler((_, i) => i == 0
            ? FakeHttpMessageHandler.RateLimited()
            : FakeHttpMessageHandler.Json(OneReleasePage));

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
            .DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Releases);
    }

    [Fact]
    public async Task A_genuine_pagination_cycle_is_still_detected_after_a_retry()
    {
        // Contract: the retry exemption is scoped to the one re-fetch it enables.
        // A next link pointing back at an already-fetched page is still a cycle.
        var handler = new FakeHttpMessageHandler((_, i) => i == 0
            ? FakeHttpMessageHandler.RateLimited()
            : FakeHttpMessageHandler.Json(OneReleasePage,
                nextLink: "https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100"));

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
            .DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("cycle", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rate_limit_retry_honours_application_shutdown()
    {
        // Contract: shutdown stays shutdown — a retry loop must never swallow
        // Ctrl-C and keep the process alive.
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            cts.Cancel(); // shutdown arrives while the retry is pending
            return FakeHttpMessageHandler.RateLimited();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
                .DiscoverAsync(cts.Token));
    }

    [Fact]
    public async Task A_403_not_attributed_to_the_rate_limit_is_not_retried()
    {
        // Contract: GitHub also returns 403 for refusals a retry can never clear
        // (blocked repository, banned user-agent). Those carry no exhausted
        // rate-limit counters, and re-requesting them only burns the traversal
        // budget the deadline exists to protect — so they fail open immediately.
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json("""{"message":"Repository access blocked"}""",
                status: HttpStatusCode.Forbidden));

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
            .DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(handler.Requests); // no retry
    }

    [Fact]
    public async Task A_secondary_rate_limit_signalled_by_retry_after_is_retried()
    {
        // Contract: GitHub's secondary/abuse limit can omit the primary counters
        // and signal with Retry-After instead. It is still a transient refusal, so
        // it must be retried like the primary limit.
        var handler = new FakeHttpMessageHandler((_, i) =>
        {
            if (i > 0) return FakeHttpMessageHandler.Json(OneReleasePage);
            var resp = FakeHttpMessageHandler.Json("""{"message":"secondary rate limit"}""",
                status: HttpStatusCode.Forbidden);
            resp.Headers.TryAddWithoutValidation("retry-after", "1");
            return resp;
        });

        var result = await RetryClient(handler, new FakeClock(), maxRateLimitRetries: 5)
            .DiscoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Discovery_handler_does_not_reuse_connections()
    {
        // Contract: the retry is only worth anything on a NEW connection. A pooled
        // connection pins every request to one egress address (measured: 10/10 same
        // IP), so it would re-hit the exact quota bucket that just refused; a fresh
        // connection re-runs source-address selection (10 requests → 6 addresses).
        using var handler = GitHubReleaseClient.CreateDiscoveryHandler();

        Assert.Equal(System.TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Server_error_fails_open()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json("[]", status: HttpStatusCode.InternalServerError));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Repeated_next_target_is_treated_as_a_cycle()
    {
        // Every page points 'next' back at the same first URL → cycle.
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json(OneReleasePage,
                nextLink: "https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100"));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("cycle", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_next_link_fails_open_not_treated_as_exhausted()
    {
        // A present-but-malformed rel="next" (non-HTTPS URL) must NOT be read as
        // "no more pages" — exhaustion is unproven, so discovery fails open.
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(OneReleasePage, System.Text.Encoding.UTF8, "application/json"),
        };
        resp.Headers.TryAddWithoutValidation("Link", "<ftp://evil/next>; rel=\"next\"");
        var handler = new FakeHttpMessageHandler((_, _) => resp);

        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("malformed", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Endless_distinct_full_pages_hit_the_page_limit()
    {
        var n = 0;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            n++;
            return FakeHttpMessageHandler.Json(OneReleasePage,
                nextLink: $"https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100&page={n + 1}");
        });
        var result = await Client(handler, maxPages: 3).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("page limit", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overall_deadline_exhaustion_fails_open()
    {
        var clock = new FakeClock();
        // Each page advances the virtual clock past the 30s deadline on page 2.
        var handler = new FakeHttpMessageHandler((_, i) =>
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            return FakeHttpMessageHandler.Json(OneReleasePage,
                nextLink: $"https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100&page={i + 2}");
        });
        var result = await Client(handler, clock).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("deadline", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Body_read_cancellation_after_headers_fails_open_not_escapes()
    {
        // With ResponseHeadersRead, headers arrive and THEN the body read can be
        // cancelled by the per-request budget (a stalled body). That
        // OperationCanceledException originates in ReadFromJsonAsync, NOT SendAsync.
        // The fail-open path must cover it; before the fix only the send was
        // guarded, so a body-read OCE escaped DiscoverAsync. Here the content throws
        // OCE on read (unrelated to the caller's ct), standing in for the per-request
        // timeout firing mid-body — DiscoverAsync must still fail open, not throw.
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new CancelOnReadContent() });
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task Application_shutdown_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(OneReleasePage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).DiscoverAsync(cts.Token));
    }
}

/// <summary>
/// HttpContent whose body read throws <see cref="OperationCanceledException"/> —
/// stands in for the per-request timeout firing during the body read (after
/// headers were already returned under ResponseHeadersRead). Not tied to the
/// caller's token, so it exercises the fail-open branch, not the shutdown rethrow.
/// </summary>
internal sealed class CancelOnReadContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => throw new OperationCanceledException("per-request body-read timeout");

    protected override Task SerializeToStreamAsync(
        Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        => throw new OperationCanceledException("per-request body-read timeout");

    protected override Task<Stream> CreateContentReadStreamAsync()
        => throw new OperationCanceledException("per-request body-read timeout");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
