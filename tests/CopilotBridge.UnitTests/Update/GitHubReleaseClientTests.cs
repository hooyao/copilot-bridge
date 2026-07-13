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
    public async Task Rate_limit_fails_open()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json("[]", status: HttpStatusCode.Forbidden));
        var result = await Client(handler).DiscoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("rate limit", result.FailureReason, System.StringComparison.OrdinalIgnoreCase);
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
    public async Task Application_shutdown_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(OneReleasePage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).DiscoverAsync(cts.Token));
    }
}
