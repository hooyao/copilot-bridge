using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        IPipelineRunner<MessagesRequest> runner,
        Pipeline<MessagesRequest> pipeline,
        ResponsesToIrInboundAdapter inboundAdapter,
        IrToResponsesOutboundAdapter outboundAdapter,
        RequestSummaryLogger summaryLogger,
        ILogger<MessagesRequest> ioLogger,
        ILogger<CodexResponsesEndpointTag> endpointLog)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);

        endpointLog.LogDebug("endpoint {Path}: enter remote={Remote}",
            httpCtx.Request.Path, httpCtx.Connection.RemoteIpAddress);

        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in httpCtx.Request.Headers)
            inboundHeaders[h.Key] = h.Value.ToString();

        var (inboundBuf, inboundLen) = await ReadBodyPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false);
        var inboundBytesView = new ReadOnlyMemory<byte>(inboundBuf, 0, inboundLen);
        var inboundAuditBody = inboundBytesView.ToArray();
        ioLogger.LogInboundRequest(seq, traceId, httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "", inboundHeaders, inboundAuditBody, inboundAuditBody.Length, bodyPooled: false);

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var responseStatus = StatusCodes.Status500InternalServerError;
        byte[] responseBody = [];
        var responseBodyLen = 0;
        List<CapturedSseEvent>? capturedEvents = null;
        string? endpointError = null;

        string? upstreamUrl = null;
        Dictionary<string, string>? upstreamHeaders = null;
        byte[] upstreamBody = [];
        var upstreamBodyLen = 0;
        var upstreamStatus = 0;
        Dictionary<string, string>? upstreamResponseHeaders = null;
        byte[]? upstreamBufferedBody = null;

        var summary = new RequestSummary { Kind = "responses", TraceId = traceId };

        try
        {
            ResponsesRequest? clientBody;
            try
            {
                clientBody = JsonSerializer.Deserialize(inboundBytesView.Span, JsonContext.Default.ResponsesRequest);
            }
            catch (JsonException ex)
            {
                endpointError = $"deserialize: {ex.Message}";
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"invalid request body: {ex.Message}", ct);
                return;
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

            var bridgeCtx = new BridgeContext<MessagesRequest>
            {
                Request = new BridgeRequest<MessagesRequest>
                {
                    Method = httpCtx.Request.Method,
                    Path = httpCtx.Request.Path.Value ?? "",
                    RawBody = inboundBytesView,
                    Body = irBody,
                    Headers = new Dictionary<string, string>(inboundHeaders, StringComparer.OrdinalIgnoreCase),
                },
                Response = new BridgeResponse(),
                Ct = ct,
            };

            await runner.RunAsync(pipeline, bridgeCtx);

            summary.ResolvedModel = bridgeCtx.Request.Body.Model;
            summary.OutboundEffort = bridgeCtx.Request.Body.OutputConfig?.Effort;
            summary.TargetVendor = bridgeCtx.Target?.Vendor.ToString();
            summary.TargetEndpoint = bridgeCtx.Target?.Endpoint;

            upstreamUrl = bridgeCtx.Target?.Endpoint;
            // The upstream body the strategy actually sent is Responses-shaped
            // (T2 built it); the IR body here is for the audit's IR view. Record
            // the IR body bytes so the four-file audit shows the IR the pipeline
            // produced (the wire Responses body is internal to the strategy).
            var upBody = JsonSerializer.SerializeToUtf8Bytes(bridgeCtx.Request.Body, JsonContext.Default.MessagesRequest);
            upstreamBody = upBody;
            upstreamBodyLen = upBody.Length;
            upstreamHeaders = new Dictionary<string, string>(bridgeCtx.Request.Headers, StringComparer.OrdinalIgnoreCase);
            upstreamStatus = bridgeCtx.Response.Status;
            upstreamResponseHeaders = new Dictionary<string, string>(bridgeCtx.Response.Headers, StringComparer.OrdinalIgnoreCase);
            upstreamBufferedBody = bridgeCtx.Response.BufferedBody;

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
                capturedEvents = new List<CapturedSseEvent>();
                // T4: IR (Anthropic) stream → Responses SSE back to Codex.
                var clientStream = outboundAdapter.AdaptStreamAsync(bridgeCtx.Response.EventStream, bridgeCtx, ct);
                await foreach (var evt in clientStream.WithCancellation(ct))
                {
                    capturedEvents.Add(new CapturedSseEvent(evt.EventType, evt.Data, Filtered: false));
                    await WriteSseEventAsync(httpCtx.Response, evt.EventType, evt.Data, ct);
                }
            }
            else if (bridgeCtx.Response.BufferedBody is not null)
            {
                var outBody = await outboundAdapter.AdaptBufferedAsync(bridgeCtx.Response.BufferedBody, bridgeCtx, ct);
                responseBody = outBody;
                responseBodyLen = outBody.Length;
                if (responseHeaders.TryGetValue("Content-Type", out var ctype))
                    httpCtx.Response.ContentType = ctype;
                httpCtx.Response.ContentLength = outBody.Length;
                await httpCtx.Response.Body.WriteAsync(outBody, ct);
            }
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
            summaryLogger.Log(summary);

            if (upstreamUrl is not null && upstreamHeaders is not null)
            {
                ioLogger.LogUpstreamRequest(seq, traceId, "POST", upstreamUrl, upstreamHeaders, upstreamBody, upstreamBodyLen, bodyPooled: false);
                var upstreamRespBody = upstreamBufferedBody ?? Array.Empty<byte>();
                ioLogger.LogUpstreamResponse(seq, traceId, upstreamStatus,
                    upstreamResponseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    upstreamRespBody, upstreamRespBody.Length, bodyPooled: false, error: endpointError);
            }

            ioLogger.LogInboundResponse(seq, traceId, responseStatus, responseHeaders,
                responseBody, responseBodyLen, bodyPooled: false, events: capturedEvents, error: endpointError, durationMs: sw.ElapsedMilliseconds);

            ArrayPool<byte>.Shared.Return(inboundBuf, clearArray: false);
            endpointLog.LogDebug("endpoint exit duration_ms={Ms}", sw.ElapsedMilliseconds);
        }
    }

    private static async Task<(byte[] Buffer, int Length)> ReadBodyPooledAsync(Stream body, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var written = 0;
        try
        {
            while (true)
            {
                if (written == buf.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, written);
                    ArrayPool<byte>.Shared.Return(buf, clearArray: false);
                    buf = bigger;
                }
                var n = await body.ReadAsync(buf.AsMemory(written), ct).ConfigureAwait(false);
                if (n == 0) break;
                written += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf, clearArray: false);
            throw;
        }
        return (buf, written);
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
