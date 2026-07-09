using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// The Codex/Responses backend strategy. Holds T2 (IR → Responses wire request)
/// and T3 (Copilot <c>/responses</c> SSE → IR Anthropic stream). Selected by
/// <see cref="Matches"/> when the resolved target is
/// <see cref="BackendVendor.CopilotResponses"/>. Mirrors
/// <c>CopilotMessagesPassthroughStrategy</c>'s shape, but the body and the
/// stream are TRANSLATED (not passthrough) because the backend speaks Responses,
/// not Anthropic.
/// </summary>
internal sealed class CopilotResponsesStrategy : IUpstreamStrategy<MessagesRequest>
{
    private readonly ICopilotClient _copilot;
    private readonly CodexModelProfileCatalog _profiles;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly RequestAudit _audit;
    private readonly UpstreamTimeoutOptions _timeout;
    private readonly ILogger<CopilotResponsesStrategy> _log;

    public CopilotResponsesStrategy(
        ICopilotClient copilot,
        CodexModelProfileCatalog profiles,
        BridgeContext<MessagesRequest> ctx,
        RequestAudit audit,
        IOptions<UpstreamTimeoutOptions> timeout,
        ILogger<CopilotResponsesStrategy> log)
    {
        _copilot = copilot;
        _profiles = profiles;
        _ctx = ctx;
        _audit = audit;
        _timeout = timeout.Value;
        _log = log;
    }

    public string Name => "CopilotResponses(T2/T3)";

    public bool Matches(RouteTarget target) =>
        target.Vendor == BackendVendor.CopilotResponses
        && target.Endpoint == "/responses";

    public async Task ForwardAsync()
    {
        var ctx = _ctx;
        // ── T2: IR MessagesRequest → Responses wire bytes ──
        var (body, vision, coercedEffort) = ResponsesRequestBuilder.Build(ctx.Request.Body, _profiles);

        // Effort coercion happens inside T2 (per-model DefaultEffort fallback) and
        // is NOT written back to the IR body. Surface the honest wire value so the
        // endpoint logs effort=max→xhigh, and WARN when the inbound effort was not
        // accepted by the target and fell back to the model default — the operator
        // can override per location with a routing EffortMap.
        var inboundEffort = ctx.Request.Body.OutputConfig?.Effort;
        ctx.Response.OutboundEffortCoerced = coercedEffort;
        if (inboundEffort is not null && coercedEffort is not null
            && !string.Equals(inboundEffort, coercedEffort, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "strategy {Name}: effort '{Inbound}' not accepted by model '{Model}'; using default '{Default}' "
                + "(override per location with Routing.Locations[].Use.EffortMap)",
                Name, inboundEffort, ctx.Request.Body.Model, coercedEffort);
        }

        // Stash the real wire bytes so the endpoint audits what we POSTed upstream
        // (the T2 Responses body), not the IR. Gated on tracing — matching the
        // buffered raw capture below (:103) — so UpstreamWireBody means exactly
        // "captured wire bytes; non-null iff tracing on". (Contract A)
        if (_audit.Enabled) ctx.Response.UpstreamWireBody = body;

        _log.LogDebug("strategy {Name}: T2 built Responses body bytes={Bytes} vision={Vision} model={Model}",
            Name, body.Length, vision, ctx.Request.Body.Model);

        var resp = await _copilot.PostResponsesAsync(body, vision, ctx.Ct);

        ctx.Response.Status = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        foreach (var h in resp.Content.Headers)
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);

        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
        var streaming = ctx.Request.Body.Stream == true
            && resp.IsSuccessStatusCode
            && contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (streaming)
        {
            ctx.Response.Mode = ResponseMode.Streaming;
            // When tracing is on, tee the raw Copilot /responses SSE into a
            // capture so upstream-resp records what Copilot sent on the wire,
            // BEFORE T3 translation. Off ⇒ capture null ⇒ no tee, no allocation.
            var capture = _audit.NewCapture();
            ctx.Response.RawUpstreamResponseCapture = capture;
            // T3: translate Responses SSE → IR (Anthropic) SSE on the fly. The
            // response stages + the outbound T4 adapter then operate on IR shape.
            ctx.Response.EventStream = TranslateStreamAsync(resp, ctx.Request.Body.Model, capture, ctx.Response, ctx.Ct);
            _log.LogDebug("strategy {Name}: T3 streaming (content-type={ContentType})", Name, contentType);
        }
        else
        {
            ctx.Response.Mode = ResponseMode.Buffered;
            try
            {
                // Non-streaming or error: buffer the raw Responses body. On the
                // success path this is a Responses JSON object T4 will translate;
                // on the error path it's the Copilot error envelope, passed through.
                ctx.Response.BufferedBody = await resp.Content.ReadAsByteArrayAsync(ctx.Ct);
                // Stash the original reference for upstream-resp before any stage
                // could rewrite BufferedBody (mirrors the /cc passthrough path).
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

    /// <summary>
    /// T3 — Copilot <c>/responses</c> SSE → IR Anthropic stream events. A stateful
    /// state machine: the Responses event grammar
    /// (<c>response.created</c> → <c>output_item.added/done</c> →
    /// <c>output_text.delta</c> / <c>function_call_arguments.delta</c> →
    /// <c>response.completed</c>) maps to the Anthropic grammar
    /// (<c>message_start</c> → <c>content_block_start/_delta/_stop</c> →
    /// <c>message_delta</c> → <c>message_stop</c>). No <c>[DONE]</c> to handle.
    /// </summary>
    private async IAsyncEnumerable<SseItem<string>> TranslateStreamAsync(
        HttpResponseMessage resp,
        string model,
        RawResponseCapture? capture,
        BridgeResponse response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sm = new ResponsesToAnthropicStream(model, _log);
        Stream? rawStream = null;
        Exception? fault = null;
        var streamIdleSeconds = _timeout.StreamIdleTimeoutSeconds;
        try
        {
            rawStream = await resp.Content.ReadAsStreamAsync(ct);
            // Tee the raw Copilot SSE when capturing; otherwise parse directly.
            var readStream = capture is null ? rawStream : new TeeReadStream(rawStream, capture);
            var parser = SseParser.Create(readStream);
            // Iterate manually so a mid-stream upstream fault (premature EOF /
            // transient disconnect) still lets us flush a terminal — Codex's
            // parser requires one, and T4 turns a faulted terminal into
            // response.failed rather than a headless stream.
            //
            // Stream-idle inactivity budget: when enabled, drive MoveNextAsync with a
            // linked CTS re-armed before each read and disarmed once an event is in
            // hand, so only time spent WAITING on upstream counts. A fired idle timer
            // is latched as `fault` (an UpstreamTimeoutException) exactly like a real
            // mid-stream disconnect — reusing this path's existing catch-and-flush so
            // the Codex client still gets a well-formed response.failed terminal
            // rather than a headless stream or an Anthropic overloaded_error it can't
            // parse. Budget <= 0 ⇒ the original loop (parser driven by `ct` only).
            var idleCts = streamIdleSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            var readCt = idleCts?.Token ?? ct;
            var idle = TimeSpan.FromSeconds(streamIdleSeconds);
            await using var e = parser.EnumerateAsync(readCt).GetAsyncEnumerator(readCt);
            while (true)
            {
                idleCts?.CancelAfter(idle);
                bool moved;
                try { moved = await e.MoveNextAsync(); }
                catch (OperationCanceledException) when (
                    idleCts is not null && idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Our idle timer fired, not the caller's token — upstream went
                    // silent mid-stream. Latch it as the fault so the terminal flush
                    // below emits response.failed, same as a real disconnect.
                    fault = new UpstreamTimeoutException(UpstreamTimeoutPhase.StreamIdle, idle);
                    break;
                }
                catch (Exception ex) { fault = ex; break; }
                if (!moved) break;
                idleCts?.CancelAfter(Timeout.InfiniteTimeSpan); // disarm during translate/yield
                foreach (var irItem in sm.Translate(e.Current))
                    yield return irItem;
            }
            idleCts?.Dispose();
        }
        finally
        {
            if (rawStream is not null) await rawStream.DisposeAsync();
            resp.Dispose();
        }

        // Always emit a terminal (even on fault / empty stream). On a fault, latch
        // the IR error stop so T4 emits response.failed; surface the underlying
        // exception on ctx.Response so the endpoint folds it into the audit's
        // error field (otherwise a truncated upstream-resp would log as a clean
        // 200 — see BridgeResponse.UpstreamStreamFault).
        foreach (var tail in sm.FlushTerminal(failed: fault is not null))
            yield return tail;

        if (fault is not null)
        {
            response.UpstreamStreamFault = fault;
            _log.LogWarning("strategy {Name}: T3 upstream stream faulted ({Type}: {Message}); "
                + "flushed a failed terminal", Name, fault.GetType().Name, fault.Message);
        }
    }
}
