using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Measures how long Copilot will hold a <c>/v1/messages</c> SSE stream open while
/// the model is thinking and has produced no token.
/// <para><b>Why this probe exists.</b> A long deep-thinking turn that dies looks like
/// an upstream deadline, and that reading has been proposed twice with different
/// numbers — a hard ~300s close of any token-less stream, and a measured 600s of
/// upstream silence. Both would change how the bridge's budgets should be set. The
/// corpus scan in <c>scripts/scan-stream-durations.ps1</c> answers it statistically;
/// this probe answers it directly. See <c>docs/stream-cap-investigation.md</c>.</para>
/// <para><b>What makes the result admissible.</b> The end of the stream is
/// classified by its <i>cause</i>, not by elapsed time: a zero-length read means the
/// peer closed the connection, an <see cref="OperationCanceledException"/> means this
/// probe gave up first. The client-side budget is set far above the disputed value
/// so a local timer cannot masquerade as a server cap — if this probe's own deadline
/// fires, the run is reported as INCONCLUSIVE rather than as a measurement.</para>
/// </summary>
/// <remarks>
/// Not part of any automated suite's meaningful signal: each run costs minutes of
/// wall clock and real quota. Run explicitly:
/// <c>dotnet test tests/CopilotBridge.Playground --filter FullyQualifiedName~StreamCapProbe</c>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
[SupportedOSPlatform("windows")]
public class StreamCapProbe
{
    private readonly ITestOutputHelper _out;

    public StreamCapProbe(ITestOutputHelper output) => _out = output;

    /// <summary>How long this probe will wait before giving up. Deliberately far
    /// above both disputed values (~305s and 600s) so that neither can be produced
    /// by this timer.</summary>
    private static readonly TimeSpan ClientBudget = TimeSpan.FromSeconds(900);

    /// <summary>How the stream ended — the load-bearing distinction.</summary>
    private enum Ending
    {
        /// <summary>Peer closed the connection (read returned 0 bytes). A server-side cap.</summary>
        ServerClosed,
        /// <summary>Upstream sent a terminal <c>message_stop</c>. The turn completed.</summary>
        Completed,
        /// <summary>This probe's own deadline fired first. Proves nothing about the server.</summary>
        ClientGaveUp,
        /// <summary>Transport threw. Reported verbatim; not evidence either way.</summary>
        TransportError,
    }

    private sealed record Run(
        Ending Ending,
        double ElapsedSeconds,
        double LongestGapSeconds,
        double? FirstTokenAtSeconds,
        int EventCount,
        int PingCount,
        string? Detail);

    [Theory(Skip = "Live probe: minutes of wall clock and real quota per run. Remove Skip to measure.")]
    [InlineData("claude-opus-5", "xhigh")]
    [InlineData("claude-opus-5", "high")]
    [InlineData("claude-opus-5", "medium")]
    public async Task TokenlessStream_HowDoesCopilotEndIt(string model, string effort)
    {
        var run = await MeasureAsync(model, effort, HardThinkingPrompt, CancellationToken.None);
        Report($"{model} effort={effort}", run);

        // No assertion on the bound itself — this probe exists to MEASURE it, and
        // pinning a number here would freeze whichever doc happens to be right today.
        // The one thing that must hold for the run to mean anything is that the
        // ending was decided by the server, not by this probe's own timer.
        Assert.True(run.Ending != Ending.ClientGaveUp,
            $"INCONCLUSIVE: this probe's {ClientBudget.TotalSeconds}s budget fired before the server " +
            "decided anything. Raise ClientBudget and re-run.");
    }

    /// <summary>
    /// A prompt chosen to keep the model thinking far past the disputed ~300s mark
    /// before it emits any token: a large search space, an exact-answer demand that
    /// blocks estimation, and an explicit instruction that nothing may precede the
    /// final result (so no incremental narration can start the token clock early).
    /// </summary>
    private const string HardThinkingPrompt =
        "Enumerate, up to isomorphism, every simple undirected graph on 10 labelled vertices " +
        "that is 3-regular, triangle-free, and has girth exactly 5. For each graph in your " +
        "classification, determine its independence number, its chromatic number, its " +
        "automorphism group order, and whether it is Hamiltonian. Then prove that your " +
        "enumeration is exhaustive by a counting argument over the possible neighbourhood " +
        "structures — do not appeal to any named theorem or published classification, and do " +
        "not estimate. Complete the entire classification and the exhaustiveness proof " +
        "internally before writing anything at all: your reply must begin with the exact " +
        "count as a bare integer, and no text of any kind may precede it.";

    private async Task<Run> MeasureAsync(
        string model, string effort, string prompt, CancellationToken outerCt)
    {
        var body = $$"""
          {
            "model": "{{model}}",
            "messages": [ { "role": "user", "content": {{System.Text.Json.JsonSerializer.Serialize(prompt)}} } ],
            "max_tokens": 32000,
            "thinking": { "type": "adaptive" },
            "output_config": { "effort": "{{effort}}" },
            "stream": true
          }
          """;

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-playground/0.1");
        var auth = new AuthService(new SingleClientHttpClientFactory(http));
        var headers = new CopilotHeaderFactory();

        var token = await auth.GetCopilotTokenAsync(outerCt);
        var baseUrl = auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(ClientBudget);

        var sw = Stopwatch.StartNew();
        var events = 0;
        var pings = 0;
        double? firstToken = null;
        var lastEventAt = 0.0;
        var longestGap = 0.0;
        var sawMessageStop = false;

        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                return new Run(Ending.TransportError, sw.Elapsed.TotalSeconds, 0, null, 0, 0,
                    $"HTTP {(int)resp.StatusCode}: {Truncate(err, 400)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);

                // The load-bearing branch: null means the peer closed the connection.
                // A local timeout arrives as OperationCanceledException instead, and is
                // classified separately below — the two can never be confused.
                if (line is null) break;

                if (line.Length == 0) continue;

                var at = sw.Elapsed.TotalSeconds;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    events++;
                    var gap = at - lastEventAt;
                    if (gap > longestGap) longestGap = gap;
                    lastEventAt = at;

                    var name = line[6..].Trim();
                    if (name == "ping") pings++;
                    if (name == "message_stop") sawMessageStop = true;
                    _out.WriteLine($"  {at,8:F1}s  event: {name}");
                }
                else if (firstToken is null && line.StartsWith("data:", StringComparison.Ordinal)
                         && line.Contains("\"text_delta\"", StringComparison.Ordinal))
                {
                    firstToken = at;
                    _out.WriteLine($"  {at,8:F1}s  <first text token>");
                }
            }

            var elapsed = sw.Elapsed.TotalSeconds;
            var tailGap = elapsed - lastEventAt;
            if (tailGap > longestGap) longestGap = tailGap;

            return new Run(
                sawMessageStop ? Ending.Completed : Ending.ServerClosed,
                elapsed, longestGap, firstToken, events, pings,
                sawMessageStop ? null : "clean EOF with no message_stop");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            return new Run(Ending.ClientGaveUp, sw.Elapsed.TotalSeconds, longestGap, firstToken, events, pings,
                $"probe budget {ClientBudget.TotalSeconds}s expired");
        }
        catch (Exception ex)
        {
            return new Run(Ending.TransportError, sw.Elapsed.TotalSeconds, longestGap, firstToken, events, pings,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Report(string label, Run r)
    {
        _out.WriteLine("");
        _out.WriteLine($"=== {label} ===");
        _out.WriteLine($"  ending           : {r.Ending}{(r.Detail is null ? "" : $" ({r.Detail})")}");
        _out.WriteLine($"  elapsed          : {r.ElapsedSeconds:F1}s");
        _out.WriteLine($"  longest gap      : {r.LongestGapSeconds:F1}s");
        _out.WriteLine($"  first text token : {(r.FirstTokenAtSeconds is { } f ? $"{f:F1}s" : "never")}");
        _out.WriteLine($"  events           : {r.EventCount} (pings: {r.PingCount})");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
