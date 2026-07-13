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
    private readonly CcToResponsesOptions _ccOptions;
    private readonly ILogger<CopilotResponsesStrategy> _log;

    public CopilotResponsesStrategy(
        ICopilotClient copilot,
        CodexModelProfileCatalog profiles,
        BridgeContext<MessagesRequest> ctx,
        RequestAudit audit,
        IOptions<UpstreamTimeoutOptions> timeout,
        ILogger<CopilotResponsesStrategy> log,
        IOptions<CcToResponsesOptions>? ccOptions = null)
    {
        _copilot = copilot;
        _profiles = profiles;
        _ctx = ctx;
        _audit = audit;
        _timeout = timeout.Value;
        _ccOptions = ccOptions?.Value ?? new CcToResponsesOptions();
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
        var filterRecursiveAgentTool =
            _ccOptions.PreventRecursiveAgentDelegation && ctx.IsClaudeCodeSubagent;
        var (body, vision, coercedEffort) = ResponsesRequestBuilder.Build(
            ctx.Request.Body, _profiles, filterRecursiveAgentTool, out var removedAgentTool);
        if (removedAgentTool)
        {
            _log.LogWarning(
                "strategy {Name}: removed Claude Code Agent tool from a sub-agent request to prevent "
                + "recursive delegation; if this removal is incorrect, set "
                + "Pipeline:CcToResponses:PreventRecursiveAgentDelegation=false and restart copilot-bridge",
                Name);
        }

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
            ctx.Response.EventStream = TranslateStreamAsync(resp, ctx.Request.Body.Model, capture, ctx.Ct);
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
                // Response stages operate on the Anthropic-shaped hub IR. A
                // successful non-streaming /responses object must therefore enter
                // buffered T3 here, before leak/runaway/tool-input detectors run.
                // Error envelopes remain Responses-shaped passthrough bodies.
                try
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var irBody = BufferedResponsesToAnthropic.TryTranslate(ctx.Response.BufferedBody)
                            ?? throw new UpstreamResponseFailedException("invalid_buffered_response");
                        ctx.Response.BufferedResponsesWireBody = ctx.Response.BufferedBody;
                        ctx.Response.InitialBufferedIrBody = irBody;
                        ctx.Response.BufferedBody = irBody;
                        ctx.Response.Headers["Content-Type"] = "application/json";
                    }
                }
                catch (UpstreamResponseFailedException ex)
                {
                    // Preserve the raw body for tracing, but never let a known
                    // failed/malformed Responses object cross either client edge.
                    // The endpoint rethrows this after it snapshots the response.
                    ctx.Response.BufferedUpstreamFault = ex;
                    ctx.Response.BufferedBody = [];
                }
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
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sm = new ResponsesToAnthropicStream(model, _log);
        Stream? rawStream = null;
        var streamIdleSeconds = _timeout.StreamIdleTimeoutSeconds;
        try
        {
            rawStream = await resp.Content.ReadAsStreamAsync(ct);
            // Tee the raw Copilot SSE when capturing; otherwise parse directly.
            var readStream = capture is null ? rawStream : new TeeReadStream(rawStream, capture);
            var parser = SseParser.Create(readStream);
            // Stream-idle inactivity budget: when enabled, each read is bounded by
            // StreamIdleReader (races the read against an independent delay — no
            // CancelAfter poison race). The read exception deliberately propagates
            // through the IR stream: only the downstream client edge knows whether
            // it must emit Anthropic event:error or Responses response.failed.
            // Budget <= 0 ⇒ the original loop (parser driven by `ct` only).
            using var readCts = streamIdleSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            var readCt = readCts?.Token ?? ct;
            var idle = TimeSpan.FromSeconds(streamIdleSeconds);
            await using var e = parser.EnumerateAsync(readCt).GetAsyncEnumerator(readCt);
            while (true)
            {
                var moved = readCts is not null
                    ? await StreamIdleReader.MoveNextAsync(e, readCts, idle, ct)
                    : await e.MoveNextAsync();
                if (!moved) break;
                foreach (var irItem in sm.Translate(e.Current))
                    yield return irItem;
                // A Responses completed/incomplete event is authoritative. Do not
                // wait for transport EOF after yielding the client terminal: a
                // keep-open connection, tail stall, or reset must not reverse an
                // already-completed model turn into an error after message_stop.
                if (sm.SawTerminal) break;
            }
        }
        finally
        {
            if (rawStream is not null) await rawStream.DisposeAsync();
            resp.Dispose();
        }

        // A clean TCP EOF is not a successful model terminal. If any Responses
        // event arrived but no response.completed/incomplete followed, propagate a
        // bounded fault exactly like a throwing disconnect so the downstream edge
        // selects Anthropic event:error/truncation or Responses response.failed.
        if (sm.SawUpstreamActivity && !sm.SawTerminal)
            throw new UpstreamResponseFailedException("incomplete_stream");

        // A genuinely empty stream retains the historical defensive terminal.
        foreach (var tail in sm.FlushTerminal())
            yield return tail;
    }
}
