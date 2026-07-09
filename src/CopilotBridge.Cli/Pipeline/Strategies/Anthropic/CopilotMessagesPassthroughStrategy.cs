using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Strategies.Anthropic;

/// <summary>
/// M1's only strategy. Forwards a fully-preprocessed
/// <see cref="MessagesRequest"/> to Copilot's <c>POST /v1/messages</c>; the
/// response shape is already what the Anthropic-shape client expects, so this
/// is true byte-level passthrough — no body translation, no SSE event
/// translation. The <c>[DONE]</c> filter and any other event-shape work live
/// in the response pipeline (<see cref="Response.IResponseStage{TBody}"/>),
/// not here.
/// </summary>
internal sealed class CopilotMessagesPassthroughStrategy : IUpstreamStrategy<MessagesRequest>
{
    private readonly ICopilotClient _copilot;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly RequestAudit _audit;
    private readonly UpstreamTimeoutOptions _timeout;
    private readonly ILogger<CopilotMessagesPassthroughStrategy> _log;

    public CopilotMessagesPassthroughStrategy(
        ICopilotClient copilot,
        BridgeContext<MessagesRequest> ctx,
        RequestAudit audit,
        IOptions<UpstreamTimeoutOptions> timeout,
        ILogger<CopilotMessagesPassthroughStrategy> log)
    {
        _copilot = copilot;
        _ctx = ctx;
        _audit = audit;
        _timeout = timeout.Value;
        _log = log;
    }

    public string Name => "CopilotMessagesPassthrough";

    public bool Matches(RouteTarget target) =>
        target.Vendor == BackendVendor.CopilotAnthropic
        && target.Endpoint == "/v1/messages";

    public async Task ForwardAsync()
    {
        var ctx = _ctx;
        var body = JsonSerializer.SerializeToUtf8Bytes(ctx.Request.Body, JsonContext.Default.MessagesRequest);

        // Stash the exact bytes we POST so the endpoint audits the real wire body
        // instead of re-serializing the IR a second time (P1). On passthrough the
        // IR IS the Anthropic wire body, so this is the same array we hand the
        // client below — no extra serialize. Gated: null off-trace, so the endpoint
        // writes nothing and UpstreamWireBody means exactly "captured wire bytes".
        if (_audit.Enabled) ctx.Response.UpstreamWireBody = body;

        // HeadersOutboundStage emits these into ctx.Request.Headers; we read
        // them back here as the existing CopilotClient.PostMessagesAsync
        // signature still takes vision/anthropicBeta as explicit params (the
        // static 7 headers come from CopilotHeaderFactory inside the client).
        var vision = ctx.Request.Headers.ContainsKey("copilot-vision-request");
        IReadOnlyList<string>? beta = null;
        if (ctx.Request.Headers.TryGetValue("anthropic-beta", out var betaStr) && betaStr.Length > 0)
        {
            beta = betaStr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        _log.LogDebug(
            "strategy {Name}: forwarding bytes={Bytes} vision={Vision} betas={BetaCount} hdrOverrides={OverrideCount}",
            Name, body.Length, vision, beta?.Count ?? 0, ctx.CopilotHeaderOverrides.Count);

        // Only thread overrides through when there's actually something to override
        // — keeps the common-case HTTP build identical to pre-routing-rewrite.
        var overrides = ctx.CopilotHeaderOverrides.Count > 0
            ? (IReadOnlyDictionary<string, string?>)ctx.CopilotHeaderOverrides
            : null;
        var resp = await _copilot.PostMessagesAsync(body, vision, beta, overrides, ctx.Ct);

        ctx.Response.Status = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
        {
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        }
        foreach (var h in resp.Content.Headers)
        {
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        }

        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
        var streaming = ctx.Request.Body.Stream == true
            && resp.IsSuccessStatusCode
            && contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (streaming)
        {
            ctx.Response.Mode = ResponseMode.Streaming;
            // When tracing is on, tee the raw upstream stream into a capture so
            // the upstream-resp audit gets Copilot's exact SSE bytes (pre-stage).
            // Off ⇒ capture is null ⇒ StreamEventsAsync is byte-identical passthrough.
            var capture = _audit.NewCapture();
            ctx.Response.RawUpstreamResponseCapture = capture;
            // Ownership of `resp` transfers to the iterator — disposed when
            // the consumer (the endpoint writer) finishes enumeration.
            ctx.Response.EventStream = StreamEventsAsync(resp, capture, _timeout.StreamIdleTimeoutSeconds, ctx.Ct);
            _log.LogDebug("strategy {Name}: streaming (content-type={ContentType})", Name, contentType);
        }
        else
        {
            ctx.Response.Mode = ResponseMode.Buffered;
            try
            {
                ctx.Response.BufferedBody = await resp.Content.ReadAsByteArrayAsync(ctx.Ct);
                // Stash the original reference BEFORE any response stage can
                // rewrite BufferedBody (ResponseModelRewriteStage reassigns it
                // to a new array), so upstream-resp shows Copilot's wire bytes.
                if (_audit.Enabled) ctx.Response.RawUpstreamResponseBody = ctx.Response.BufferedBody;
                _log.LogDebug("strategy {Name}: buffered status={Status} bytes={Bytes}",
                    Name, ctx.Response.Status, ctx.Response.BufferedBody.Length);
            }
            finally
            {
                resp.Dispose();
            }
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamEventsAsync(
        HttpResponseMessage resp,
        RawResponseCapture? capture,
        int streamIdleSeconds,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Stream? rawStream = null;
        try
        {
            rawStream = await resp.Content.ReadAsStreamAsync(ct);
            // Tee only when capturing; otherwise hand the raw stream straight to
            // the parser (no wrapper, no allocation, byte-identical passthrough).
            var readStream = capture is null ? rawStream : new TeeReadStream(rawStream, capture);
            var parser = SseParser.Create(readStream);

            if (streamIdleSeconds <= 0)
            {
                // Disabled: the original path — byte-identical passthrough, no timer,
                // no linked CTS. The `await foreach` drives the parser with `ct`.
                await foreach (var evt in parser.EnumerateAsync(ct))
                {
                    yield return evt;
                }
                yield break;
            }

            // Stream-idle inactivity budget. Each event's wait on upstream is bounded
            // by StreamIdleReader, which races the read against an independent delay
            // (NOT a CancelAfter armed/disarmed on the enumerator token — that has a
            // nanosecond poison race). readCts backs the enumerator's read token so
            // the reader can end a pending read on an idle timeout. The yield sits
            // OUTSIDE any try/catch (C# forbids yielding inside a try with a catch).
            var idle = TimeSpan.FromSeconds(streamIdleSeconds);
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var enumerator = parser.EnumerateAsync(readCts.Token).GetAsyncEnumerator(readCts.Token);
            while (true)
            {
                if (!await StreamIdleReader.MoveNextAsync(enumerator, readCts, idle, ct))
                {
                    yield break;
                }
                yield return enumerator.Current;
            }
        }
        finally
        {
            // Dispose the raw network stream — the tee deliberately doesn't own it.
            if (rawStream is not null) await rawStream.DisposeAsync();
            resp.Dispose();
        }
    }
}
