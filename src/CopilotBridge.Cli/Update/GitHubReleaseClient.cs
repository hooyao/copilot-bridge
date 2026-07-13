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
/// Time source used to enforce the whole-traversal deadline. Injected so tests
/// can advance a virtual clock without wall-clock sleeps.
/// </summary>
internal interface IMonotonicClock
{
    /// <summary>A monotonically increasing timestamp (never wall-clock-adjusted).</summary>
    long GetTimestamp();

    /// <summary>Elapsed time since <paramref name="startTimestamp"/>.</summary>
    TimeSpan Elapsed(long startTimestamp);
}

/// <summary><see cref="Stopwatch"/>-backed monotonic clock for production use.</summary>
internal sealed class StopwatchClock : IMonotonicClock
{
    public static readonly StopwatchClock Instance = new();
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
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
///   <item>a defensive maximum page count.</item>
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

    private readonly HttpClient _http;
    private readonly IMonotonicClock _clock;
    private readonly TimeSpan _perRequestTimeout;
    private readonly TimeSpan _overallDeadline;
    private readonly int _maxPages;

    public GitHubReleaseClient(
        HttpClient http,
        TimeSpan perRequestTimeout,
        TimeSpan overallDeadline,
        IMonotonicClock? clock = null,
        int maxPages = DefaultMaxPages)
    {
        _http = http;
        _clock = clock ?? StopwatchClock.Instance;
        _perRequestTimeout = perRequestTimeout;
        _overallDeadline = overallDeadline;
        _maxPages = maxPages;
    }

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
            if (!visited.Add(next))
            {
                // GitHub returned a next link we already fetched — a cycle.
                return ReleaseDiscoveryResult.Fail("update-check pagination cycle");
            }

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
                try
                {
                    using var req = BuildRequest(next);
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReq.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (perReq.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // The per-request budget (or the remaining overall budget)
                    // fired, not shutdown — fail open.
                    return ReleaseDiscoveryResult.Fail("update-check request timeout");
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429)
                    {
                        return ReleaseDiscoveryResult.Fail("GitHub API rate limit reached");
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        return ReleaseDiscoveryResult.Fail($"GitHub API returned HTTP {(int)resp.StatusCode}");
                    }

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
        }

        return ReleaseDiscoveryResult.Success(releases);
    }

    private static HttpRequestMessage BuildRequest(string url)
    {
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
