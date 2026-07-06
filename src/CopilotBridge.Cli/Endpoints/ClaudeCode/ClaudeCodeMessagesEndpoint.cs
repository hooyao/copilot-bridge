using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Errors;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>POST /cc/v1/messages</c> — Claude Code's native endpoint. Thin wrapper:
/// parse → adapt to IR → run pipeline → adapt response → write outbound.
/// Self-registers via <see cref="MapMessages"/> so callers don't have to
/// know the URL template.
/// </summary>
internal static class ClaudeCodeMessagesEndpoint
{
    /// <summary>Mount the route on a route builder. Production calls this from
    /// <see cref="Hosting.ServeCommand"/>.</summary>
    public static IEndpointRouteBuilder MapMessages(this IEndpointRouteBuilder app)
    {
        app.MapPost("/cc/v1/messages", HandleAsync);
        return app;
    }

    public static async Task HandleAsync(
        HttpContext httpCtx,
        BridgeContext<MessagesRequest> bridgeCtx,
        IPipelineRunner<MessagesRequest> runner,
        Pipeline<MessagesRequest> pipeline,
        ClaudeCodeInboundAdapter inboundAdapter,
        ClaudeCodeOutboundAdapter outboundAdapter,
        ModelProfileCatalog profiles,
        RequestSummaryLogger summaryLogger,
        RequestAudit audit,
        ILogger<ClaudeCodeMessagesEndpointTag> endpointLog)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        // One trace id pins the four audit files for this request and the
        // INFO summary line together — see BridgeIoSeq.BuildTraceId.
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);

        // Correlate EVERY log line for this request with the trace id — the
        // enter/exit boundary lines below, the pipeline stages/detectors/relay
        // loop, and the summary in finally: push the RAW id onto Serilog's
        // LogContext as "ReqTrace" (ReqTraceFormatEnricher renders it as
        // "[<id>] "). Declared at the top of the handler so the scope spans the
        // enter log, the try, AND the finally — a using-declaration disposes at
        // method exit, so a scope pushed inside the try would drop before
        // finally's exit line. Non-request lines carry nothing (no scope).
        using var _traceScope = Serilog.Context.LogContext.PushProperty("ReqTrace", traceId);

        endpointLog.LogDebug("endpoint {Path}: enter  remote={Remote}",
            httpCtx.Request.Path, httpCtx.Connection.RemoteIpAddress);

        // Capture inbound headers + body up front so the audit always lands
        // even on early-return paths (deserialize failure, unsupported tool).
        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in httpCtx.Request.Headers)
        {
            inboundHeaders[h.Key] = h.Value.ToString();
        }

        // The inbound body is read + audited + deserialized in a narrow `using`
        // block inside the try below, so its pooled buffer is released as soon as
        // the IR is built — not pinned across the pipeline + streaming relay. The
        // inbound size is captured there into `inboundLen` for the exit line.
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var responseStatus = StatusCodes.Status500InternalServerError;
        byte[] responseBody = [];
        var responseBodyLen = 0;
        List<CapturedSseEvent>? capturedEvents = null;
        string? endpointError = null;
        // Inbound body size, captured in the narrow read block below; reported on the
        // exit line in the finally (the raw bytes themselves do not outlive that block).
        var inboundLen = 0;

        string? upstreamUrl = null;
        Dictionary<string, string>? upstreamHeaders = null;
        byte[] upstreamBody = [];
        var upstreamBodyLen = 0;
        var upstreamStatus = 0;
        Dictionary<string, string>? upstreamResponseHeaders = null;
        // The pipeline response, snapshotted post-RunAsync so the finally can read
        // the raw upstream-resp bytes (BridgeResponse.RawUpstreamRespBytesOrNull,
        // which finalizes the streaming capture). Null if the pipeline threw before
        // producing a response.
        BridgeResponse? pipelineResponse = null;

        // Per-request summary, populated as the pipeline progresses. Always
        // emitted in finally regardless of error path.
        var summary = new RequestSummary { Kind = "messages" };
        var usageSnapshot = summary.Usage;
        var inboundBetaSet = ClaudeCodeInboundAdapter.ParseInboundBetas(inboundHeaders);
        summary.InboundBetas = inboundBetaSet.ToArray();

        try
        {
            MessagesRequest? clientBody;
            // Narrow read scope: read → audit → deserialize, then dispose at the
            // block's close, so the (large-pool) buffer returns to the manager in the
            // parse window, not pinned across the pipeline + streaming relay. `inbound`
            // is out of scope afterwards, so reading its Memory after dispose is a
            // compile error, not a use-after-free. Inside the outer try so the finally
            // still emits the summary + inbound-resp audit on the deserialize-400 path.
            using (var inbound = await InboundBody.ReadPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false))
            {
                inboundLen = inbound.Length;
                // Audit the raw inbound bytes (pre-T1) — RequestAudit copies the view
                // to an array only when tracing is on, so off-trace there's no copy.
                audit.RecordInbound(
                    seq,
                    traceId,
                    httpCtx.Request.Method,
                    httpCtx.Request.Path.Value ?? "",
                    inboundHeaders,
                    inbound.Memory);
                try
                {
                    clientBody = JsonSerializer.Deserialize(inbound.Memory.Span, JsonContext.Default.MessagesRequest);
                }
                catch (JsonException ex)
                {
                    endpointError = $"deserialize: {ex.Message}";
                    responseStatus = StatusCodes.Status400BadRequest;
                    httpCtx.Response.StatusCode = responseStatus;
                    await httpCtx.Response.WriteAsync($"invalid request body: {ex.Message}", ct);
                    return;
                }
            }

            if (clientBody is null)
            {
                endpointError = "deserialize: null";
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                return;
            }

            // Snapshot the pre-pipeline state for the INFO summary log.
            // Pre-seed ResolvedModel with the requested name so that if the
            // pipeline throws before the post-pipeline block runs (e.g.
            // ModelRouterStage fails), the summary still names which model
            // the client asked for rather than rendering "?".
            summary.RequestedModel = clientBody.Model;
            summary.ResolvedModel = clientBody.Model;
            summary.InboundEffort = clientBody.OutputConfig?.Effort;
            summary.OutboundEffort = clientBody.OutputConfig?.Effort;
            summary.MaxTokens = clientBody.MaxTokens;

            // Short-circuit on Anthropic server tools Copilot doesn't support.
            if (TryGetUnsupportedServerTool(clientBody, out var badType))
            {
                endpointError = $"unsupported server tool: {badType}";
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                httpCtx.Response.ContentType = "application/json";
                var msg =
                    $"The '{badType}' server tool is not supported on the GitHub Copilot backend. Configure a custom search MCP server in your Claude Code MCP config (e.g. via --mcp-config) and disable the built-in WebSearch tool.";
                var bytes = Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"error\",\"error\":{{\"type\":\"not_supported\",\"message\":\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(msg)}\"}}}}");
                responseBody = bytes;
                responseBodyLen = bytes.Length;
                httpCtx.Response.ContentType = "application/json";
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, ct);
                return;
            }

            var irBody = await inboundAdapter.AdaptAsync(clientBody, inboundHeaders, ct);


            // Populate the injected scoped context (created empty by DI; the same
            // instance the pipeline components resolved). The pipeline reads it in
            // RunAsync, strictly after this fill.
            bridgeCtx.Request = new BridgeRequest<MessagesRequest>
            {
                Method = httpCtx.Request.Method,
                Path = httpCtx.Request.Path.Value ?? "",
                Body = irBody,
                Headers = new Dictionary<string, string>(inboundHeaders, StringComparer.OrdinalIgnoreCase),
            };
            bridgeCtx.Response = new BridgeResponse();
            bridgeCtx.Ct = ct;
            bridgeCtx.InboundBetas = inboundBetaSet;
            bridgeCtx.TraceId = traceId;

            await runner.RunAsync(pipeline);

            // Post-pipeline summary fields.
            summary.ResolvedModel = bridgeCtx.Request.Body.Model;
            // gpt-5.5 (and other Codex models) reached via /cc route to the Codex
            // strategy, which coerces effort in T2 without writing it back to the IR
            // body — so prefer the honest wire value it stashed (effort=max→xhigh),
            // falling back to the IR body's effort on the Anthropic passthrough path.
            summary.OutboundEffort = bridgeCtx.Response.OutboundEffortCoerced
                ?? bridgeCtx.Request.Body.OutputConfig?.Effort;
            // Re-lookup the profile for the summary. Use GetNearest so a
            // fuzzy-matched (un-profiled but forwarded) model reports the borrowed
            // profile id here rather than blank — matches what the router actually
            // used to shape the body.
            summary.CanonicalProfileId =
                (profiles.Get(bridgeCtx.Request.Body.Model)
                 ?? profiles.GetNearest(bridgeCtx.Request.Body.Model, out _, out _))?.CanonicalId;
            summary.TargetVendor = bridgeCtx.Target?.Vendor.ToString();
            summary.TargetEndpoint = bridgeCtx.Target?.Endpoint;
            summary.OutboundBetas = ParseOutboundBetas(bridgeCtx.Request.Headers);

            upstreamUrl = bridgeCtx.Target?.Endpoint;
            // Audit the exact bytes POSTed upstream. Both strategies stash them on
            // Response.UpstreamWireBody when tracing is on (the passthrough Anthropic
            // body OR the Codex T2 Responses body) — the same array they handed the
            // Copilot client, never a re-serialized IR. Off-trace UpstreamWireBody is
            // null and the finally's RecordUpstreamRequest no-ops, so nothing is
            // serialized for an audit that isn't written. Routing identity is read
            // from bridgeCtx.Target.Vendor (above), never inferred from this field.
            upstreamBody = bridgeCtx.Response.UpstreamWireBody ?? [];
            upstreamBodyLen = upstreamBody.Length;
            upstreamHeaders =
                new Dictionary<string, string>(bridgeCtx.Request.Headers, StringComparer.OrdinalIgnoreCase);
            upstreamStatus = bridgeCtx.Response.Status;
            upstreamResponseHeaders =
                new Dictionary<string, string>(bridgeCtx.Response.Headers, StringComparer.OrdinalIgnoreCase);
            // Snapshot the response so the finally can read the raw upstream-resp
            // bytes. For streaming, the capture inside it is filled by the relay
            // loop below BEFORE the finally finalizes it; for buffered, the raw
            // pre-rewrite reference is already set.
            pipelineResponse = bridgeCtx.Response;

            responseStatus = bridgeCtx.Response.Status;
            foreach (var (k, v) in bridgeCtx.Response.Headers)
            {
                responseHeaders[k] = v;
            }

            httpCtx.Response.StatusCode = responseStatus;
            if (bridgeCtx.Response.Headers.TryGetValue("Content-Type", out var ctype))
            {
                httpCtx.Response.ContentType = ctype;
            }

            summary.Streaming = bridgeCtx.Response.Mode == ResponseMode.Streaming;

            if (bridgeCtx.Response.Mode == ResponseMode.Streaming && bridgeCtx.Response.EventStream is not null)
            {
                // Only buffer per-event copies for the inbound-resp audit when
                // tracing is on — off-trace this stays null and we never grow a
                // list (the SSE body can be large; this is the hot path).
                capturedEvents = audit.NewEventList();
                var clientStream = outboundAdapter.AdaptStreamAsync(bridgeCtx.Response.EventStream, ct);
                await foreach (var evt in clientStream.WithCancellation(ct))
                {
                    capturedEvents?.Add(new CapturedSseEvent(evt.EventType, evt.Data, Filtered: false));
                    // Sniff usage from message_start / message_delta as they
                    // pass through — Anthropic emits cumulative values so the
                    // probe simply overwrites the snapshot on each event.
                    UsageProbe.TryUpdateFromStreamEvent(evt.EventType, evt.Data, usageSnapshot);
                    await WriteSseEventAsync(httpCtx.Response, evt.EventType, evt.Data, ct);
                }

                if (capturedEvents is not null)
                {
                    foreach (var d in bridgeCtx.DroppedEvents)
                    {
                        capturedEvents.Add(new CapturedSseEvent(d.EventType, d.Data, Filtered: true));
                    }
                }
            }
            else if (bridgeCtx.Response.BufferedBody is not null)
            {
                var outBody = await outboundAdapter.AdaptBufferedAsync(bridgeCtx.Response.BufferedBody, ct);
                responseBody = outBody;
                responseBodyLen = outBody.Length;
                // Non-streaming Anthropic body — parse usage out of it.
                UsageProbe.TryReadBuffered(outBody, usageSnapshot);
                httpCtx.Response.ContentLength = outBody.Length;
                await httpCtx.Response.Body.WriteAsync(outBody, ct);
            }

            // Copy the response-side detector flags off the context AFTER the stream
            // has drained (streaming sets them mid-relay) so the summary line reports
            // them: response_leak (protocol leak), runaway (volume circuit-breaker),
            // and tool_input_invalid (malformed/schema-invalid tool arguments).
            summary.ResponseLeakDetected = bridgeCtx.ResponseLeakDetected;
            summary.RunawayDetected = bridgeCtx.RunawayDetected;
            summary.ToolInputInvalidDetected = bridgeCtx.ToolInputInvalidDetected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            endpointError = "cancelled by client";
            summary.Error = "cancelled by client";
            endpointLog.LogDebug("endpoint cancelled by client");
            throw;
        }
        catch (UnknownModelException ex)
        {
            // The bridge refused the model because it has no profile for it
            // (after normalize + any user rule that fired). Surface as 400 +
            // Anthropic-format error body so clients display it as a normal
            // model error rather than a transport failure; the message
            // ([copilot-bridge] prefix) tells the operator what to fix.
            endpointError = ex.Message;
            summary.Error = ex.Message;
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                httpCtx.Response.ContentType = "application/json";
                var error = new ErrorResponse
                {
                    Error = new ErrorBody { Type = "invalid_request_error", Message = ex.Message },
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(error, JsonContext.Default.ErrorResponse);
                responseBody = bytes;
                responseBodyLen = bytes.Length;
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, CancellationToken.None);
            }
        }
        catch (Exception ex) when (IsTransientUpstreamError(ex))
        {
            // Upstream cut the connection or the read failed at the TCP level
            // — happens occasionally with Copilot, especially on long thinking
            // requests or when the upstream gateway times out a slow stream.
            // NOT a bridge bug; render as a single Warning line (no stack)
            // so the operator sees the signal without being misled into
            // hunting a regression.
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogWarning("endpoint upstream-disconnect: {Type}: {Message}", ex.GetType().Name,
                ex.Message);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"upstream disconnected: {ex.Message}", CancellationToken.None);
            }
            else
            {
                // Already sent response headers (mid-stream disconnect); we
                // cannot rewrite the status, so reflect what the wire sees:
                // a 200 stream that was cut short. The Warning + summary
                // error field tell the operator why.
                responseStatus = httpCtx.Response.StatusCode;
            }
        }
        catch (Exception ex)
        {
            // Genuinely unexpected — keep the stack trace, it's the only
            // diagnostic we have.
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogError(ex, "endpoint exception: {Type}: {Message}", ex.GetType().Name, ex.Message);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"upstream error: {ex.Message}", CancellationToken.None);
            }
            else
            {
                // Same as the transient branch: headers already sent, we
                // can only record the wire-visible status so the summary
                // line doesn't claim a misleading 500 from the initial
                // default value.
                responseStatus = httpCtx.Response.StatusCode;
            }
        }
        finally
        {
            sw.Stop();
            summary.StatusCode = responseStatus;
            summary.DurationMs = sw.ElapsedMilliseconds;
            // Poisoned-context count is set by PoisonedContextScanStage during the
            // request pipeline (before the strategy), so copy it here in finally —
            // it survives even when a later stage or the strategy throws. 0 when the
            // scan stage is disabled or the pipeline never reached it.
            summary.PoisonedToolResults = bridgeCtx.PoisonedToolResults;
            summaryLogger.Log(summary);

            if (audit.Enabled && upstreamUrl is not null && upstreamHeaders is not null)
            {
                audit.RecordUpstreamRequest(
                    seq,
                    traceId,
                    "POST",
                    upstreamUrl,
                    upstreamHeaders,
                    upstreamBody,
                    upstreamBodyLen);

                // Surface exactly what Copilot put on the wire, pre-stage: the
                // buffered pre-rewrite array, or the streaming tee (finalized now
                // that the relay loop above has drained the stream). See
                // BridgeResponse.RawUpstreamRespBytesOrNull — reading it SEALS the
                // streaming capture, so it must run once, here, after the relay
                // loop drained. Gated on audit.Enabled: off-trace there is no
                // capture to seal and this whole block is audit-only work.
                var upstreamRespBody = pipelineResponse?.RawUpstreamRespBytesOrNull() ?? Array.Empty<byte>();
                audit.RecordUpstreamResponse(
                    seq,
                    traceId,
                    upstreamStatus,
                    upstreamResponseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    upstreamRespBody,
                    upstreamRespBody.Length,
                    error: endpointError);
            }

            audit.RecordInboundResponse(
                seq,
                traceId,
                responseStatus,
                responseHeaders,
                responseBody,
                responseBodyLen,
                events: capturedEvents,
                error: endpointError,
                durationMs: sw.ElapsedMilliseconds);

            endpointLog.LogDebug("endpoint exit  duration_ms={Ms}  body-bytes={Bytes}",
                sw.ElapsedMilliseconds, inboundLen);
        }
    }

    private static IReadOnlyList<string> ParseOutboundBetas(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("anthropic-beta", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var part in raw.Split(','))
        {
            var token = part.Trim();
            if (token.Length > 0) list.Add(token);
        }
        return list;
    }

    /// <summary>
    /// Recognises upstream-side transport problems that aren't bridge bugs:
    /// Copilot cutting the TCP connection mid-response, TLS handshake
    /// failures (Copilot dropping the socket during the cert exchange),
    /// gateway timeouts, IO errors on read. Caller logs these at Warning
    /// (no stack) instead of Error (with stack) so a transient hiccup
    /// doesn't look like a regression in the bridge's own code. Delegates to
    /// the shared <see cref="Copilot.TransientUpstreamError"/> classifier
    /// (the same one CopilotClient uses to decide whether to retry).
    /// </summary>
    private static bool IsTransientUpstreamError(Exception ex) =>
        Copilot.TransientUpstreamError.Is(ex);

    private static bool TryGetUnsupportedServerTool(MessagesRequest body, out string? toolType)
    {
        toolType = null;
        if (body.Tools is null) return false;
        foreach (var t in body.Tools)
        {
            if (t.Type is { Length: > 0 } typ && typ.StartsWith("web_search_", StringComparison.OrdinalIgnoreCase))
            {
                toolType = typ;
                return true;
            }
        }
        return false;
    }

    private static async Task WriteSseEventAsync(
        HttpResponse downstream,
        string? eventType,
        string data,
        CancellationToken ct)
    {
        var sb = new StringBuilder(data.Length + 64);
        if (!string.IsNullOrEmpty(eventType))
        {
            sb.Append("event: ").Append(eventType).Append('\n');
        }
        foreach (var line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await downstream.Body.WriteAsync(bytes, ct);
        await downstream.Body.FlushAsync(ct);
    }
}

/// <summary>Marker type used to give the messages endpoint its own <c>ILogger</c> category.</summary>
internal sealed class ClaudeCodeMessagesEndpointTag { }
