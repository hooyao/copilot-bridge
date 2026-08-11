using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// The bounded outcome of a discovery traversal. Only <see cref="Releases"/>
/// with <see cref="Succeeded"/> true may be used to pick an update; any other
/// result is fail-open (the caller logs a Warning and starts the current
/// version). <see cref="FailureReason"/> is a short operator-facing phrase.
/// </summary>
internal readonly struct ReleaseDiscoveryResult
{
    private ReleaseDiscoveryResult(bool succeeded, IReadOnlyList<GitHubRelease> releases, string? failureReason)
    {
        Succeeded = succeeded;
        Releases = releases;
        FailureReason = failureReason;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<GitHubRelease> Releases { get; }
    public string? FailureReason { get; }

    public static ReleaseDiscoveryResult Success(IReadOnlyList<GitHubRelease> releases) =>
        new(true, releases, null);

    public static ReleaseDiscoveryResult Fail(string reason) =>
        new(false, [], reason);
}

/// <summary>
/// Time source used to enforce the whole-traversal deadline and to wait between
/// rate-limit retries. Injected so tests can advance a virtual clock without
/// wall-clock sleeps.
/// </summary>
internal interface IMonotonicClock
{
    /// <summary>A monotonically increasing timestamp (never wall-clock-adjusted).</summary>
    long GetTimestamp();

    /// <summary>Elapsed time since <paramref name="startTimestamp"/>.</summary>
    TimeSpan Elapsed(long startTimestamp);

    /// <summary>
    /// Wait <paramref name="delay"/>. Owned by the clock so a virtual clock can
    /// make the wait instantaneous while still advancing elapsed time — a retry
    /// must consume the traversal budget in tests exactly as it does in production.
    /// </summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary><see cref="Stopwatch"/>-backed monotonic clock for production use.</summary>
internal sealed class StopwatchClock : IMonotonicClock
{
    public static readonly StopwatchClock Instance = new();
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// Discovers releases from the project's public GitHub Releases REST API,
/// anonymously (no <c>Authorization</c> header, no <c>gh</c> executable). The
/// traversal is aggressively bounded so it can never keep the synchronous serve
/// gate from starting Kestrel:
/// <list type="bullet">
///   <item>a finite per-request timeout;</item>
///   <item>one monotonic wall-clock deadline for the WHOLE traversal;</item>
///   <item>repeated <c>next</c>-link/page detection (cycle guard);</item>
///   <item>a defensive maximum page count;</item>
///   <item>a bounded number of rate-limit retries per page.</item>
/// </list>
/// Every failure — DNS/TLS/HTTP/schema/rate-limit/per-request-timeout/overall-
/// deadline/pagination-cycle/page-limit — discards partial results and returns a
/// fail-open <see cref="ReleaseDiscoveryResult"/>. Caller/application-shutdown
/// cancellation is NOT converted to a warning: it propagates as
/// <see cref="OperationCanceledException"/> so shutdown stays shutdown.
/// </summary>
internal sealed class GitHubReleaseClient
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100";
    private const int DefaultMaxPages = 20;
    private const int DefaultMaxRateLimitRetries = 5;
    private static readonly TimeSpan DefaultRateLimitRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly IMonotonicClock _clock;
    private readonly TimeSpan _perRequestTimeout;
    private readonly TimeSpan _overallDeadline;
    private readonly int _maxPages;
    private readonly int _maxRateLimitRetries;
    private readonly TimeSpan _rateLimitRetryDelay;

    public GitHubReleaseClient(
        HttpClient http,
        TimeSpan perRequestTimeout,
        TimeSpan overallDeadline,
        IMonotonicClock? clock = null,
        int maxPages = DefaultMaxPages,
        int maxRateLimitRetries = DefaultMaxRateLimitRetries,
        TimeSpan? rateLimitRetryDelay = null)
    {
        _http = http;
        _clock = clock ?? StopwatchClock.Instance;
        _perRequestTimeout = perRequestTimeout;
        _overallDeadline = overallDeadline;
        _maxPages = maxPages;
        _maxRateLimitRetries = maxRateLimitRetries;
        _rateLimitRetryDelay = rateLimitRetryDelay ?? DefaultRateLimitRetryDelay;
    }

    /// <summary>
    /// The handler discovery must run on. <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
    /// is <see cref="TimeSpan.Zero"/> — connections are NOT reused — because that
    /// is what makes the rate-limit retry in <see cref="DiscoverAsync"/> worth
    /// anything.
    /// <para>
    /// GitHub's anonymous quota is 60 requests/hour keyed on the SOURCE IP, and a
    /// bridge behind a corporate/cloud NAT shares that per-IP bucket with everyone
    /// else on the same address — so the check gets a 403 even though it spends
    /// one request per startup. Measured against the live API from such a NAT
    /// (an 8-address pool): a pooled connection pins every request to ONE egress
    /// address (10/10 requests, same IP), so retrying on it re-hits the exact
    /// bucket that just returned 0 remaining. Opening a fresh connection instead
    /// re-runs source-address selection (10 requests → 6 distinct addresses), so a
    /// retry can land on an address that still has quota — with one address
    /// exhausted, 10 of 12 fresh-connection attempts succeeded.
    /// </para>
    /// <para>
    /// This MITIGATES the shared-bucket case; it does not fix rate limiting. If
    /// every address in the pool is exhausted the retry cannot help (measured at
    /// full exhaustion: 0/10 recovered even at 20 attempts over 10s), and a
    /// single-address egress has nothing to re-select. Those cases still fail open
    /// with the same warning, just after a bounded retry. Quota resets on a fixed
    /// hourly window, not a sliding one, so a drained pool stays drained until the
    /// boundary.
    /// </para>
    /// Redirects stay disabled, matching the rest of the update path.
    /// </summary>
    public static SocketsHttpHandler CreateDiscoveryHandler() => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.Zero,
    };

    /// <summary>
    /// Fetch all published releases, following pagination until exhaustion is
    /// proven within the bounds. On any bound hit or transport/parse failure,
    /// returns a fail-open result with partial data discarded.
    /// </summary>
    /// <param name="ct">Application-shutdown token; its cancellation propagates.</param>
    public async Task<ReleaseDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        var releases = new List<GitHubRelease>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = ReleasesUrl;
        var pages = 0;
        // Retries are counted per page and reset once a page is fetched, so a
        // paginated traversal gets the same allowance on every page.
        var rateLimitRetries = 0;
        var isRetry = false;

        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (_clock.Elapsed(started) >= _overallDeadline)
            {
                return ReleaseDiscoveryResult.Fail("update-check deadline exceeded");
            }
            if (pages >= _maxPages)
            {
                return ReleaseDiscoveryResult.Fail("update-check page limit exceeded");
            }
            // A retry re-fetches a url already registered as visited, so it must
            // skip the cycle guard — but only for that one re-fetch. Any url the
            // server hands back via a next link still goes through the guard.
            if (!isRetry && !visited.Add(next))
            {
                // GitHub returned a next link we already fetched — a cycle.
                return ReleaseDiscoveryResult.Fail("update-check pagination cycle");
            }
            isRetry = false;

            HttpResponseMessage resp;
            List<GitHubRelease>? page;
            try
            {
                // Per-request timeout, further capped by the REMAINING overall
                // budget so a request begun just before the deadline can't run the
                // full per-request timeout and blow past the whole-traversal bound.
                var remaining = _overallDeadline - _clock.Elapsed(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return ReleaseDiscoveryResult.Fail("update-check deadline exceeded");
                }
                var effectiveTimeout = remaining < _perRequestTimeout ? remaining : _perRequestTimeout;

                using var perReq = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perReq.CancelAfter(effectiveTimeout);

                using var req = BuildRequest(next);
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token)
                    .ConfigureAwait(false);

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429)
                    {
                        // The per-IP anonymous quota is shared with everyone behind
                        // the same NAT, so an exhausted bucket says nothing about
                        // whether THIS check can proceed. Retrying on a fresh
                        // connection (see CreateDiscoveryHandler) re-runs egress
                        // address selection and can land on an address that still
                        // has quota — a mitigation, not a fix: a single-address
                        // egress retries and still fails, just bounded and fail-open.
                        //
                        // Only retry a response GitHub actually attributes to the
                        // rate limit. A 403 is also how it reports reasons a retry
                        // can never clear (blocked repository, banned UA), and
                        // re-requesting those just burns the traversal budget.
                        if (IsRateLimited(resp) && rateLimitRetries < _maxRateLimitRetries)
                        {
                            rateLimitRetries++;
                            // Never retry past the traversal deadline — the whole
                            // point of the bound is that startup cannot be delayed.
                            if (_clock.Elapsed(started) + _rateLimitRetryDelay >= _overallDeadline)
                            {
                                return ReleaseDiscoveryResult.Fail("GitHub API rate limit reached");
                            }
                            await _clock.DelayAsync(_rateLimitRetryDelay, ct).ConfigureAwait(false);
                            isRetry = true;
                            continue; // re-fetch the same url on a new connection
                        }
                        return ReleaseDiscoveryResult.Fail("GitHub API rate limit reached");
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        return ReleaseDiscoveryResult.Fail($"GitHub API returned HTTP {(int)resp.StatusCode}");
                    }

                    // Read the body under the SAME per-request budget. With
                    // ResponseHeadersRead the server can return headers and then
                    // stall the body — the perReq timeout must cover this read too,
                    // else its OperationCanceledException escapes the fail-open path.
                    page = await resp.Content
                        .ReadFromJsonAsync(JsonContext.Default.ListGitHubRelease, perReq.Token)
                        .ConfigureAwait(false);

                    // A present-but-malformed next relation means exhaustion is not
                    // proven → fail open rather than report a partial release set.
                    switch (ParseNextLink(resp.Headers, out var nextUrl))
                    {
                        case NextLinkKind.Absent:
                            next = null;
                            break;
                        case NextLinkKind.Valid:
                            next = nextUrl;
                            break;
                        default:
                            return ReleaseDiscoveryResult.Fail("update-check pagination link malformed");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Application shutdown — do NOT swallow.
                throw;
            }
            catch (OperationCanceledException)
            {
                // The per-request budget (or the remaining overall budget) fired on
                // EITHER the header send or the body read — not shutdown (guarded
                // above) — so fail open with the current version.
                return ReleaseDiscoveryResult.Fail("update-check request timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException)
            {
                return ReleaseDiscoveryResult.Fail($"update check failed: {ex.GetType().Name}");
            }

            if (page is null)
            {
                return ReleaseDiscoveryResult.Fail("GitHub API returned an unparseable body");
            }

            releases.AddRange(page);
            pages++;
            rateLimitRetries = 0;
        }

        return ReleaseDiscoveryResult.Success(releases);
    }

    /// <summary>
    /// Whether GitHub attributes this refusal to the rate limit, so a retry has a
    /// chance of clearing it. GitHub signals an exhausted primary limit with
    /// <c>x-ratelimit-remaining: 0</c> (observed on a live anonymous 403 alongside
    /// <c>x-ratelimit-limit: 60</c> and <c>x-ratelimit-reset</c>), and a secondary
    /// / abuse limit with <c>retry-after</c>. A 403 carrying neither is a
    /// permission-style refusal a retry cannot fix, so it is not retried.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), out var left))
        {
            return left <= 0;
        }
        // A secondary rate limit may omit the primary counters but sends Retry-After.
        return resp.Headers.TryGetValues("retry-after", out _);
    }

    private static HttpRequestMessage BuildRequest(string url)    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub requires a User-Agent; send the installed version but NO
        // Authorization header — discovery is anonymous by design.
        req.Headers.UserAgent.ParseAdd($"{ProductInfo.Name}/{ProductInfo.Version}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    // GitHub paginates via RFC 5988 Link headers: <url>; rel="next".
    // Distinguishes ABSENT (no next relation → traversal is genuinely exhausted)
    // from PRESENT-BUT-MALFORMED (a next relation whose URL is missing/non-HTTPS →
    // exhaustion is NOT proven, so the caller must fail open rather than silently
    // report a partial set).
    private enum NextLinkKind { Absent, Valid, Malformed }

    private static NextLinkKind ParseNextLink(HttpResponseHeaders headers, out string? url)
    {
        url = null;
        if (!headers.TryGetValues("Link", out var values))
        {
            return NextLinkKind.Absent;
        }
        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                var segments = part.Split(';');
                if (segments.Length < 2)
                {
                    continue;
                }
                var rel = segments[1].Trim();
                if (rel is not ("rel=\"next\"" or "rel=next"))
                {
                    continue;
                }
                // A next relation IS present. Its URL must be a usable HTTPS link;
                // anything else is malformed, not "no more pages".
                var candidate = segments[0].Trim().Trim('<', '>', ' ');
                if (candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = candidate;
                    return NextLinkKind.Valid;
                }
                return NextLinkKind.Malformed;
            }
        }
        return NextLinkKind.Absent;
    }
}
