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
    private readonly bool _tracingEnabled;
    private readonly ILogger<CopilotMessagesPassthroughStrategy> _log;

    public CopilotMessagesPassthroughStrategy(
        ICopilotClient copilot,
        BridgeContext<MessagesRequest> ctx,
        IOptions<TracingOptions> tracing,
        ILogger<CopilotMessagesPassthroughStrategy> log)
    {
        _copilot = copilot;
        _ctx = ctx;
        _tracingEnabled = tracing.Value.Enabled;
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
            var capture = _tracingEnabled ? new RawResponseCapture() : null;
            ctx.Response.RawUpstreamResponseCapture = capture;
            // Ownership of `resp` transfers to the iterator — disposed when
            // the consumer (the endpoint writer) finishes enumeration.
            ctx.Response.EventStream = StreamEventsAsync(resp, capture, ctx.Ct);
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
                if (_tracingEnabled) ctx.Response.RawUpstreamResponseBody = ctx.Response.BufferedBody;
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
            await foreach (var evt in parser.EnumerateAsync(ct))
            {
                yield return evt;
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
