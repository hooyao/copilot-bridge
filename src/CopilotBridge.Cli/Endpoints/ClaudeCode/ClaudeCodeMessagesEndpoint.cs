using System.Buffers;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Serilog;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>POST /cc/v1/messages</c> — Claude Code's native endpoint. Thin wrapper:
/// parse → adapt to IR → run pipeline → adapt response → write outbound.
/// </summary>
internal static class ClaudeCodeMessagesEndpoint
{
    public static async Task HandleAsync(
        HttpContext httpCtx,
        IPipelineRunner<MessagesRequest> runner,
        Pipeline<MessagesRequest> pipeline,
        ClaudeCodeInboundAdapter inboundAdapter,
        ClaudeCodeOutboundAdapter outboundAdapter,
        ILogger<MessagesRequest> ioLogger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();

        Log.Debug($"endpoint {httpCtx.Request.Path}: enter  remote={httpCtx.Connection.RemoteIpAddress}");

        // Capture inbound headers + body up front so the audit always lands
        // even on early-return paths (deserialize failure, unsupported tool).
        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in httpCtx.Request.Headers)
        {
            inboundHeaders[h.Key] = h.Value.ToString();
        }

        // inbound body — read into a pooled buffer so the hot path skips
        // GC for typical request sizes (CC packs the whole conversation
        // history each turn, easily 100 KB+).
        var (inboundBuf, inboundLen) = await ReadBodyPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false);
        var inboundBytesView = new ReadOnlyMemory<byte>(inboundBuf, 0, inboundLen);

        // Hand a separate (non-pooled) copy to the sink — the pooled
        // buffer is still in use by the pipeline (RawBody view) for the
        // remainder of the request and must not be returned to the pool
        // until this method's finally block.
        var inboundAuditBody = inboundBytesView.ToArray();
        ioLogger.LogInboundRequest(
            seq,
            httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "",
            inboundHeaders,
            inboundAuditBody,
            inboundAuditBody.Length,
            bodyPooled: false);

        // Per-response state used by the finally block.
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var responseStatus = StatusCodes.Status500InternalServerError;
        byte[] responseBody = [];
        var responseBodyLen = 0;
        var responseBodyPooled = false;
        List<CapturedSseEvent>? capturedEvents = null;
        string? endpointError = null;

        // Per-upstream state — populated only when the pipeline reaches the
        // strategy stage. Captured here so the finally block can flush a
        // best-effort upstream-req/upstream-resp pair even on partial
        // failure.
        string? upstreamUrl = null;
        Dictionary<string, string>? upstreamHeaders = null;
        byte[] upstreamBody = [];
        var upstreamBodyLen = 0;
        var upstreamBodyPooled = false;
        var upstreamStatus = 0;
        Dictionary<string, string>? upstreamResponseHeaders = null;

        try
        {
            MessagesRequest? clientBody;
            try
            {
                clientBody = JsonSerializer.Deserialize(inboundBytesView.Span, JsonContext.Default.MessagesRequest);
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

            // Short-circuit on Anthropic server tools Copilot doesn't support.
            // Currently: web_search_* — the upstream's "unsupported_value" error
            // is opaque to Claude Code users; this surface tells them what to
            // do (configure an MCP search server instead). See research §15.4.
            if (TryGetUnsupportedServerTool(clientBody, out var badType))
            {
                endpointError = $"unsupported server tool: {badType}";
                responseStatus = StatusCodes.Status400BadRequest;
                httpCtx.Response.StatusCode = responseStatus;
                httpCtx.Response.ContentType = "application/json";
                var msg = $"The '{badType}' server tool is not supported on the GitHub Copilot backend. Configure a custom search MCP server in your Claude Code MCP config (e.g. via --mcp-config) and disable the built-in WebSearch tool.";
                var bytes = Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"error\",\"error\":{{\"type\":\"not_supported\",\"message\":\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(msg)}\"}}}}");
                responseBody = bytes;
                responseBodyLen = bytes.Length;
                responseBodyPooled = false;
                httpCtx.Response.ContentType = "application/json";
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, ct);
                return;
            }

            var irBody = await inboundAdapter.AdaptAsync(clientBody, inboundHeaders, ct);
            var inboundBetas = ClaudeCodeInboundAdapter.ParseInboundBetas(inboundHeaders);

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
                InboundBetas = inboundBetas,
            };

            await runner.RunAsync(pipeline, bridgeCtx);

            // Snapshot what we actually sent / received upstream now that
            // the pipeline has populated bridgeCtx.
            upstreamUrl = bridgeCtx.Target?.Endpoint;
            var upBody = JsonSerializer.SerializeToUtf8Bytes(bridgeCtx.Request.Body, JsonContext.Default.MessagesRequest);
            upstreamBody = upBody;
            upstreamBodyLen = upBody.Length;
            upstreamBodyPooled = false;
            upstreamHeaders = new Dictionary<string, string>(bridgeCtx.Request.Headers, StringComparer.OrdinalIgnoreCase);
            upstreamStatus = bridgeCtx.Response.Status;
            upstreamResponseHeaders = new Dictionary<string, string>(bridgeCtx.Response.Headers, StringComparer.OrdinalIgnoreCase);

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

            if (bridgeCtx.Response.Mode == ResponseMode.Streaming && bridgeCtx.Response.EventStream is not null)
            {
                // Capture forwarded events as we relay them so the audit
                // has a complete picture (forwarded + filtered).
                capturedEvents = new List<CapturedSseEvent>();
                var clientStream = outboundAdapter.AdaptStreamAsync(bridgeCtx.Response.EventStream, bridgeCtx, ct);
                await foreach (var evt in clientStream.WithCancellation(ct))
                {
                    capturedEvents.Add(new CapturedSseEvent(evt.EventType, evt.Data, Filtered: false));
                    await WriteSseEventAsync(httpCtx.Response, evt.EventType, evt.Data, ct);
                }
                foreach (var d in bridgeCtx.DroppedEvents)
                {
                    capturedEvents.Add(new CapturedSseEvent(d.EventType, d.Data, Filtered: true));
                }
            }
            else if (bridgeCtx.Response.BufferedBody is not null)
            {
                var outBody = await outboundAdapter.AdaptBufferedAsync(bridgeCtx.Response.BufferedBody, bridgeCtx, ct);
                responseBody = outBody;
                responseBodyLen = outBody.Length;
                responseBodyPooled = false;
                httpCtx.Response.ContentLength = outBody.Length;
                await httpCtx.Response.Body.WriteAsync(outBody, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            endpointError = "cancelled by client";
            Log.Debug("endpoint cancelled by client");
            throw;
        }
        catch (UnknownModelException ex)
        {
            // The bridge refused the model because it has no profile for it
            // (after normalize + any user rule that fired). Surface as 400 +
            // Anthropic-format error body so clients display it as a normal
            // model error rather than a transport failure; the message
            // ([copilot-bridge] prefix) tells the operator what to fix.
            // ModelRouterStage already logged the full diagnostic to Serilog.
            endpointError = ex.Message;
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
                responseBodyPooled = false;
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            endpointError = ex.Message;
            Log.Debug($"endpoint exception: {ex.GetType().Name}: {ex.Message}");
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"upstream error: {ex.Message}", CancellationToken.None);
            }
        }
        finally
        {
            sw.Stop();

            // Flush upstream-req + upstream-resp audits if the pipeline got
            // far enough to materialize them. (Early-fail paths — bad JSON,
            // unsupported tool — leave both null and we skip.)
            if (upstreamUrl is not null && upstreamHeaders is not null)
            {
                ioLogger.LogUpstreamRequest(
                    seq,
                    "POST",
                    upstreamUrl,
                    upstreamHeaders,
                    upstreamBody,
                    upstreamBodyLen,
                    upstreamBodyPooled);

                ioLogger.LogUpstreamResponse(
                    seq,
                    upstreamStatus,
                    upstreamResponseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [],
                    0,
                    bodyPooled: false,
                    error: endpointError);
            }

            ioLogger.LogInboundResponse(
                seq,
                responseStatus,
                responseHeaders,
                responseBody,
                responseBodyLen,
                responseBodyPooled,
                events: capturedEvents,
                error: endpointError,
                durationMs: sw.ElapsedMilliseconds);

            // Pooled inbound buffer was kept alive for the pipeline's
            // RawBody view; safe to return now that the request is done
            // and the audit copy has already been handed off to the sink.
            ArrayPool<byte>.Shared.Return(inboundBuf, clearArray: false);

            Log.Debug($"endpoint exit  duration_ms={sw.ElapsedMilliseconds}");
        }
    }

    /// <summary>
    /// Read the inbound HTTP body into a pooled buffer. Caller (or its
    /// downstream logger) is responsible for returning the buffer to the
    /// pool via <see cref="BridgeIoPayload.Release"/>. The returned length
    /// is the meaningful prefix; the rented buffer may be larger.
    /// </summary>
    private static async Task<(byte[] Buffer, int Length)> ReadBodyPooledAsync(Stream body, CancellationToken ct)
    {
        // 64 KB rented to start; grow geometrically as the body fills it.
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

    /// <summary>
    /// Returns true and sets <paramref name="toolType"/> when the request
    /// carries a server tool the Copilot upstream rejects. Today: anything
    /// with <c>type</c> starting <c>"web_search_"</c>. New gap models can
    /// extend this guard without touching the pipeline.
    /// </summary>
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
