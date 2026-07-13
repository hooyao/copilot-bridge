using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Errors;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Responses;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Endpoints.Codex;

/// <summary>
/// <c>POST /codex/responses</c> — the Codex CLI's native endpoint. Reads a
/// Responses request, adapts to the shared Anthropic-shape IR (T1), runs the
/// SAME <see cref="Pipeline{MessagesRequest}"/> the <c>/cc</c> path uses (routing
/// sends gpt-* to the Codex/Responses strategy), then adapts the IR response back
/// to Responses (T4). Structurally parallel to
/// <c>ClaudeCodeMessagesEndpoint</c>, leaner: no count_tokens, no /models, no
/// Anthropic server-tool short-circuit. Path is <c>base_url + /responses</c>
/// (verified research §3.4), NOT <c>/codex/v1/...</c>.
/// </summary>
internal static class CodexResponsesEndpoint
{
    public static IEndpointRouteBuilder MapCodexResponses(this IEndpointRouteBuilder app)
    {
        app.MapPost("/codex/responses", HandleAsync);
        return app;
    }

    public static async Task HandleAsync(
        HttpContext httpCtx,
        BridgeContext<MessagesRequest> bridgeCtx,
        IPipelineRunner<MessagesRequest> runner,
        Pipeline<MessagesRequest> pipeline,
        ResponsesToIrInboundAdapter inboundAdapter,
        IrToResponsesOutboundAdapter outboundAdapter,
        RequestSummaryLogger summaryLogger,
        RequestAudit audit,
        ILogger<CodexResponsesEndpointTag> endpointLog)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);
        // Correlate EVERY log line for this request with the trace id — the
        // enter/exit boundary lines below, the pipeline stages/detectors, and
        // the summary in finally: push the RAW id onto Serilog's LogContext as
        // "ReqTrace" (ReqTraceFormatEnricher renders it as "[<id>] "). Declared
        // at the top of the handler so the scope spans the enter log, the try,
        // AND the finally — a using-declaration disposes at method exit, so a
        // scope pushed inside the try would drop before finally's exit line.
        // Mirrors /cc.
        using var _traceScope = Serilog.Context.LogContext.PushProperty("ReqTrace", traceId);

        endpointLog.LogDebug("endpoint {Path}: enter remote={Remote}",
            httpCtx.Request.Path, httpCtx.Connection.RemoteIpAddress);

        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in httpCtx.Request.Headers)
            inboundHeaders[h.Key] = h.Value.ToString();

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
        // Pipeline response snapshot; the finally reads the raw upstream-resp bytes
        // off it (finalizing the streaming capture). Null if the pipeline threw.
        BridgeResponse? pipelineResponse = null;

        var summary = new RequestSummary { Kind = "responses" };

        try
        {
            ResponsesRequest? clientBody;
            // Narrow read scope: the pooled body is read, audited, and deserialized
            // here, then disposed at the block's close — so the (large-pool) buffer is
            // returned to the manager in the parse window, not pinned across the
            // pipeline + streaming relay. `inbound` is out of scope afterwards, so a
            // read of its Memory after dispose is a compile error, not a use-after-free.
            using (var inbound = await InboundBody.ReadPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false))
            {
                inboundLen = inbound.Length;
                // Audit the raw inbound Codex request (pre-T1). RequestAudit copies the
                // view only when tracing is on, so off-trace there's no extra copy.
                audit.RecordInbound(seq, traceId, httpCtx.Request.Method,
                    httpCtx.Request.Path.Value ?? "", inboundHeaders, inbound.Memory);
                try
                {
                    clientBody = JsonSerializer.Deserialize(inbound.Memory.Span, JsonContext.Default.ResponsesRequest);
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

            summary.RequestedModel = clientBody.Model;
            summary.ResolvedModel = clientBody.Model;
            summary.InboundEffort = clientBody.Reasoning?.Effort;
            summary.OutboundEffort = clientBody.Reasoning?.Effort;

            // T1: Responses → IR (Anthropic shape).
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
            bridgeCtx.TraceId = traceId;

            await runner.RunAsync(pipeline);

            summary.ResolvedModel = bridgeCtx.Request.Body.Model;
            // Prefer the effort actually written to the wire by T2 (per-model
            // coercion is NOT reflected on the IR body), falling back to the IR
            // body's effort when the strategy didn't set it (non-Responses path).
            // This makes the summary honest: effort=max→xhigh, not a bare max.
            summary.OutboundEffort = bridgeCtx.Response.OutboundEffortCoerced
                ?? bridgeCtx.Request.Body.OutputConfig?.Effort;
            summary.TargetVendor = bridgeCtx.Target?.Vendor.ToString();
            summary.TargetEndpoint = bridgeCtx.Target?.Endpoint;

            upstreamUrl = bridgeCtx.Target?.Endpoint;
            // Audit the exact bytes the Codex strategy POSTed upstream: T2 built a
            // Responses-shaped body and stashed it on Response.UpstreamWireBody when
            // tracing is on — the same array handed to the Copilot client, never a
            // re-serialized IR. Off-trace it's null and the finally's
            // RecordUpstreamRequest no-ops. Note: this is the upstream REQUEST body
            // only — the raw upstream SSE before T3 is captured via the tee, read in
            // finally through RawUpstreamRespBytesOrNull.
            upstreamBody = bridgeCtx.Response.UpstreamWireBody ?? [];
            upstreamBodyLen = upstreamBody.Length;
            upstreamHeaders = new Dictionary<string, string>(bridgeCtx.Request.Headers, StringComparer.OrdinalIgnoreCase);
            upstreamStatus = bridgeCtx.Response.Status;
            upstreamResponseHeaders = new Dictionary<string, string>(bridgeCtx.Response.Headers, StringComparer.OrdinalIgnoreCase);
            // Snapshot the response so the finally can read the raw upstream-resp
            // bytes (the streaming capture is filled by the relay loop below
            // before finally finalizes it).
            pipelineResponse = bridgeCtx.Response;

            responseStatus = bridgeCtx.Response.Status;
            foreach (var (k, v) in bridgeCtx.Response.Headers)
                responseHeaders[k] = v;

            httpCtx.Response.StatusCode = responseStatus;
            // The wire content type back to Codex is event-stream (streaming) or
            // json (buffered). Force event-stream on the streaming path.
            summary.Streaming = bridgeCtx.Response.Mode == ResponseMode.Streaming;

            if (bridgeCtx.Response.Mode == ResponseMode.Streaming && bridgeCtx.Response.EventStream is not null)
            {
                httpCtx.Response.ContentType = "text/event-stream";
                // Only buffer per-event copies for the audit when tracing is on.
                capturedEvents = audit.NewEventList();
                // T4: IR (Anthropic) stream → Responses SSE back to Codex.
                var clientStream = outboundAdapter.AdaptStreamAsync(bridgeCtx.Response.EventStream, ct);
                await foreach (var evt in clientStream.WithCancellation(ct))
                {
                    capturedEvents?.Add(new CapturedSseEvent(evt.EventType, evt.Data, Filtered: false));
                    await WriteSseEventAsync(httpCtx.Response, evt.EventType, evt.Data, ct);
                }
            }
            else if (bridgeCtx.Response.BufferedBody is not null)
            {
                var outBody = await outboundAdapter.AdaptBufferedAsync(bridgeCtx.Response.BufferedBody, ct);
                responseBody = outBody;
                responseBodyLen = outBody.Length;
                if (responseHeaders.TryGetValue("Content-Type", out var ctype))
                    httpCtx.Response.ContentType = ctype;
                httpCtx.Response.ContentLength = outBody.Length;
                await httpCtx.Response.Body.WriteAsync(outBody, ct);
            }

            // Copy the response-side detector flags off the context AFTER the stream
            // has drained (streaming sets them mid-relay) so the summary line reports
            // them. The shared pipeline runs the same detectors on the Codex path, so
            // a runaway, a leak, or a tool-input-invalid abort can trip here too.
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
        catch (UpstreamTimeoutException ex)
        {
            // First-byte timeouts arrive before any downstream bytes. Stream-idle
            // faults arrive here only after T4 emitted response.failed and rethrew;
            // the response-start check preserves the already-sent 200.
            var phase = UpstreamTimeoutException.PhaseLabel(ex.Phase);
            summary.UpstreamTimeout = phase;
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogWarning(
                "endpoint upstream-timeout: phase={Phase} type={ExceptionType} idle={IdleSeconds:0.#}s "
                + "(tune Pipeline:UpstreamTimeout)",
                phase, ex.GetType().Name, ex.Elapsed.TotalSeconds);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status504GatewayTimeout;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync(
                    $"upstream timed out waiting for first byte after {ex.Elapsed.TotalSeconds:0.#}s",
                    CancellationToken.None);
            }
            else responseStatus = httpCtx.Response.StatusCode;
        }
        catch (UpstreamResponseFailedException ex)
        {
            // T4 already wrote the single Responses-native response.failed before
            // rethrowing this bounded fault. Only account for it here.
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogWarning(
                "endpoint upstream-response-failed: type={ExceptionType} code={FailureCode}",
                ex.GetType().Name, ex.Code);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync("upstream model backend failed", CancellationToken.None);
            }
            else responseStatus = httpCtx.Response.StatusCode;
        }
        catch (UnknownModelException ex)
        {
            endpointError = ex.Message;
            summary.Error = ex.Message;
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                httpCtx.Response.ContentType = "application/json";
                var error = new ErrorResponse { Error = new ErrorBody { Type = "invalid_request_error", Message = ex.Message } };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(error, JsonContext.Default.ErrorResponse);
                responseBody = bytes; responseBodyLen = bytes.Length;
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, CancellationToken.None);
            }
        }
        catch (CodexBadRequestException ex)
        {
            // A client-side fault in the Codex request (e.g. malformed tool
            // arguments the model echoed back, surfaced by T1) — surface as 400
            // with an Anthropic-shape error envelope, NOT a 502. Mirrors the
            // UnknownModelException branch above.
            endpointError = ex.Message;
            summary.Error = ex.Message;
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                httpCtx.Response.ContentType = "application/json";
                var error = new ErrorResponse { Error = new ErrorBody { Type = "invalid_request_error", Message = ex.Message } };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(error, JsonContext.Default.ErrorResponse);
                responseBody = bytes; responseBodyLen = bytes.Length;
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, CancellationToken.None);
            }
        }
        catch (Exception ex) when (Copilot.TransientUpstreamError.Is(ex))
        {
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogWarning("endpoint upstream-disconnect: {Type}: {Message}", ex.GetType().Name, ex.Message);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"upstream disconnected: {ex.Message}", CancellationToken.None);
            }
            else responseStatus = httpCtx.Response.StatusCode;
        }
        catch (Exception ex)
        {
            endpointError = ex.Message;
            summary.Error = $"{ex.GetType().Name}: {ex.Message}";
            endpointLog.LogError(ex, "endpoint exception: {Type}: {Message}", ex.GetType().Name, ex.Message);
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"upstream error: {ex.Message}", CancellationToken.None);
            }
            else responseStatus = httpCtx.Response.StatusCode;
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
                audit.RecordUpstreamRequest(seq, traceId, "POST", upstreamUrl, upstreamHeaders, upstreamBody, upstreamBodyLen);
                // Copilot's raw /responses wire bytes, pre-T3/T4 (buffered
                // pre-rewrite array, or the streaming tee finalized after the
                // relay loop). See BridgeResponse.RawUpstreamRespBytesOrNull —
                // reading it SEALS the streaming capture, so run once here after the
                // relay loop drained. Gated on audit.Enabled: audit-only work.
                var upstreamRespBody = pipelineResponse?.RawUpstreamRespBytesOrNull() ?? Array.Empty<byte>();
                audit.RecordUpstreamResponse(seq, traceId, upstreamStatus,
                    upstreamResponseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    upstreamRespBody, upstreamRespBody.Length, error: endpointError);
            }

            audit.RecordInboundResponse(seq, traceId, responseStatus, responseHeaders,
                responseBody, responseBodyLen, events: capturedEvents, error: endpointError, durationMs: sw.ElapsedMilliseconds);

            endpointLog.LogDebug("endpoint exit duration_ms={Ms} body-bytes={Bytes}",
                sw.ElapsedMilliseconds, inboundLen);
        }
    }

    private static async Task WriteSseEventAsync(HttpResponse downstream, string? eventType, string data, CancellationToken ct)
    {
        var sb = new StringBuilder(data.Length + 64);
        if (!string.IsNullOrEmpty(eventType))
            sb.Append("event: ").Append(eventType).Append('\n');
        foreach (var line in data.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await downstream.Body.WriteAsync(bytes, ct);
        await downstream.Body.FlushAsync(ct);
    }
}

/// <summary>Marker type for the Codex endpoint's <c>ILogger</c> category.</summary>
internal sealed class CodexResponsesEndpointTag { }
