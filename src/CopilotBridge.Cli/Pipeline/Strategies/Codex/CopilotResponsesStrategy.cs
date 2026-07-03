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
    private readonly bool _tracingEnabled;
    private readonly ILogger<CopilotResponsesStrategy> _log;

    public CopilotResponsesStrategy(
        ICopilotClient copilot,
        CodexModelProfileCatalog profiles,
        BridgeContext<MessagesRequest> ctx,
        IOptions<TracingOptions> tracing,
        ILogger<CopilotResponsesStrategy> log)
    {
        _copilot = copilot;
        _profiles = profiles;
        _ctx = ctx;
        _tracingEnabled = tracing.Value.Enabled;
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
        var (body, vision) = ResponsesRequestBuilder.Build(ctx.Request.Body, _profiles);

        // Stash the real wire bytes so the endpoint audits what we POSTed upstream
        // (not the IR). Null on passthrough paths; here we always translated. (Contract A)
        ctx.Response.UpstreamWireBody = body;

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
            var capture = _tracingEnabled ? new RawResponseCapture() : null;
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
            await using var e = parser.EnumerateAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool moved;
                try { moved = await e.MoveNextAsync(); }
                catch (Exception ex) { fault = ex; break; }
                if (!moved) break;
                foreach (var irItem in sm.Translate(e.Current))
                    yield return irItem;
            }
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
