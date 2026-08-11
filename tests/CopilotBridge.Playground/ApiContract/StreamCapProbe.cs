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
/// <para><b>What makes the result admissible.</b> The probe measures <i>how long the
/// stream stayed alive while producing nothing</i> — a lower bound, established by the
/// bytes that kept arriving, not by whatever ended the stream afterwards. So the
/// disqualifying outcome is this probe's own deadline firing (which would measure our
/// patience rather than the stream's life), not a transport fault. Attribution is
/// deliberately withheld: a premature end to the response body is indistinguishable
/// here from a reset or a proxy dropping the connection, so no ending is reported as
/// "Copilot closed it". The token-less precondition is likewise verified from the wire
/// rather than assumed from the prompt — a run that produced content before the
/// threshold is INAPPLICABLE, because total lifetime cannot test a cap that only
/// governs streams which have produced nothing.</para>
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

    /// <summary>How the stream ended. Note that only <see cref="Completed"/> and
    /// <see cref="ClientGaveUp"/> are attributable with certainty — see each member.</summary>
    private enum Ending
    {
        /// <summary>The read returned zero bytes at the SSE layer with no
        /// <c>message_stop</c>. Consistent with the peer closing, but an intermediary
        /// closing the connection looks the same from here, so this attributes nothing.</summary>
        EndedWithoutStop,
        /// <summary>Upstream sent a terminal <c>message_stop</c>. The turn completed —
        /// the one ending that is unambiguously the model's own.</summary>
        Completed,
        /// <summary>This probe's own deadline fired first. The only ending that makes the
        /// elapsed time useless as a lower bound on the stream's life.</summary>
        ClientGaveUp,
        /// <summary>Transport threw, or upstream answered non-2xx. The stream demonstrably
        /// survived until this happened, but the actor behind it is unknown.</summary>
        TransportError,
    }

    private sealed record Run(
        Ending Ending,
        double ElapsedSeconds,
        double LongestGapSeconds,
        /// <summary>When the first CONTENT delta of any kind arrived — text, thinking,
        /// or tool-call JSON. This is what makes a run applicable or not: the disputed
        /// cap governs a stream that has produced <i>no token</i>, so a run that starts
        /// producing before the threshold cannot test it whatever its total length.</summary>
        double? FirstContentAtSeconds,
        int EventCount,
        int PingCount,
        string? Detail)
    {
        /// <summary>
        /// Whether this run can speak to a token-less cap at <paramref name="thresholdSeconds"/>.
        /// True only if the stream was still content-free when the threshold passed — i.e.
        /// it either produced nothing at all, or produced its first content only afterwards.
        /// A run that began emitting at 20s and ran to 700s says nothing about a cap on
        /// token-less streams, however impressive its duration.
        /// </summary>
        public bool IsApplicableTo(double thresholdSeconds) =>
            ElapsedSeconds >= thresholdSeconds &&
            (FirstContentAtSeconds is null || FirstContentAtSeconds > thresholdSeconds);
    }

    /// <summary>The bound under test — the disputed "token-less streams are closed at
    /// ~300s" figure. Used only to decide whether a run is APPLICABLE, never asserted.</summary>
    private const double DisputedCapSeconds = 305;

    /// <summary>
    /// Every Anthropic delta type that puts model-produced content on the wire. A stream
    /// is "token-less" only until one of these appears — matching just <c>text_delta</c>
    /// would classify a stream as silent while it streamed thinking or tool-call JSON
    /// continuously, which is the opposite of the condition under test.
    /// </summary>
    private static readonly string[] ContentDeltaTypes =
    [
        "\"text_delta\"",
        "\"thinking_delta\"",
        "\"signature_delta\"",
        "\"input_json_delta\"",
    ];

    [Theory(Skip = "Live probe: minutes of wall clock and real quota per run. Remove Skip to measure.")]
    [InlineData("claude-opus-5", "xhigh")]
    [InlineData("claude-opus-5", "high")]
    [InlineData("claude-opus-5", "medium")]
    public async Task TokenlessStream_HowLongDoesItStayOpen(string model, string effort)
    {
        var run = await MeasureAsync(model, effort, HardThinkingPrompt, CancellationToken.None);
        Report($"{model} effort={effort}", run);

        // A run only counts as evidence if BOTH hold, and each failure mode is reported
        // separately because they call for different corrective action.
        //
        // NOTE what is NOT required: that the ending be attributable to Copilot. This
        // probe answers "was the stream still alive at T?", and survival past a
        // threshold is established by the bytes that kept arriving before it — not by
        // whatever ended the stream afterwards. A run that reaches 700s and then dies
        // to an unattributable transport fault has still disproved a 305s cap.

        // (1) The ending must not be this probe's own deadline. That is the one outcome
        // that makes the elapsed time meaningless as a lower bound, because the stream
        // might have been closed a millisecond later and we would never know — the
        // number would measure our patience, not the stream's life. A transport fault
        // is different: the stream demonstrably survived until it happened.
        Assert.NotEqual(Ending.ClientGaveUp, run.Ending);

        // (2) The stream must actually have been token-less across the disputed threshold.
        // A prompt instruction cannot GUARANTEE wire silence — the model may narrate
        // immediately — so the precondition this probe is named for is verified from the
        // wire, not assumed. Without this the probe silently degrades into a
        // total-lifetime measurement, which cannot test a token-less cap at all.
        Assert.True(
            run.IsApplicableTo(DisputedCapSeconds),
            $"INAPPLICABLE: first content arrived at " +
            $"{(run.FirstContentAtSeconds is { } c ? $"{c:F1}s" : "never")} and the stream " +
            $"ran {run.ElapsedSeconds:F1}s, so it was not token-less across " +
            $"{DisputedCapSeconds}s. This run cannot test a token-less cap — use a prompt " +
            "that defers output for longer.");
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

        var lease = await auth.GetCopilotTokenAsync(ct: outerCt);
        var token = lease.Token;
        var baseUrl = lease.ApiBaseUrl;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(ClientBudget);

        var sw = Stopwatch.StartNew();
        var events = 0;
        var pings = 0;
        double? firstContent = null;
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
                else if (firstContent is null && line.StartsWith("data:", StringComparison.Ordinal)
                         && ContentDeltaTypes.Any(t => line.Contains(t, StringComparison.Ordinal)))
                {
                    // Any content delta ends the token-less phase, not just visible text.
                    // Matching text_delta alone would call a stream "token-less" while it
                    // streamed thinking or tool-call JSON the whole time — which is exactly
                    // the wire activity a token-less cap is about.
                    firstContent = at;
                    _out.WriteLine($"  {at,8:F1}s  <first content delta>");
                }
            }

            var elapsed = sw.Elapsed.TotalSeconds;
            var tailGap = elapsed - lastEventAt;
            if (tailGap > longestGap) longestGap = tailGap;

            return new Run(
                sawMessageStop ? Ending.Completed : Ending.EndedWithoutStop,
                elapsed, longestGap, firstContent, events, pings,
                sawMessageStop ? null : "clean EOF with no message_stop");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            return new Run(Ending.ClientGaveUp, sw.Elapsed.TotalSeconds, longestGap, firstContent, events, pings,
                $"probe budget {ClientBudget.TotalSeconds}s expired");
        }
        catch (HttpIOException ex) when (ex.HttpRequestError == HttpRequestError.ResponseEnded)
        {
            // The response body ended before its terminating chunk. That is NOT
            // attributable to Copilot: a reset, a proxy failure, or any intermediary
            // dropping the connection produces the identical exception — the repo's own
            // TransientUpstreamError treats HttpIOException as "upstream OR network"
            // precisely because the two cannot be told apart from this side.
            //
            // So it stays a TransportError and the run fails as INCONCLUSIVE. It is
            // recorded distinctly only so the reader can see WHICH transport fault
            // occurred; the distinction carries no evidential weight and must not be
            // used to claim the peer decided anything.
            return new Run(Ending.TransportError, sw.Elapsed.TotalSeconds, longestGap, firstContent, events, pings,
                "response body ended prematurely — actor unknown (peer, proxy, or network)");
        }
        catch (Exception ex)
        {
            return new Run(Ending.TransportError, sw.Elapsed.TotalSeconds, longestGap, firstContent, events, pings,
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
        _out.WriteLine($"  first content    : {(r.FirstContentAtSeconds is { } f ? $"{f:F1}s" : "never")}");
        _out.WriteLine($"  events           : {r.EventCount} (pings: {r.PingCount})");
        _out.WriteLine($"  tests {DisputedCapSeconds}s cap : {(r.IsApplicableTo(DisputedCapSeconds) ? "YES — token-less across the threshold" : "NO — produced content too early / ended too soon")}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
