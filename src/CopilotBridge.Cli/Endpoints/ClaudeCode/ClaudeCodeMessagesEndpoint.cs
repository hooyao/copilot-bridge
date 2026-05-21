using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.AspNetCore.Http;

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
        BridgeRequestLogger logger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var log = new BridgeRequestLog();
        LogHelpers.CaptureInbound(httpCtx, log);

        Log.Debug($"endpoint {httpCtx.Request.Path}: enter  remote={httpCtx.Connection.RemoteIpAddress}");

        try
        {
            await using var bodyMs = new MemoryStream();
            await httpCtx.Request.Body.CopyToAsync(bodyMs, ct);
            var inboundBytes = bodyMs.ToArray();
            log.InboundBody = Encoding.UTF8.GetString(inboundBytes);

            MessagesRequest? clientBody;
            try
            {
                clientBody = JsonSerializer.Deserialize(inboundBytes, JsonContext.Default.MessagesRequest);
            }
            catch (JsonException ex)
            {
                log.Error = $"deserialize: {ex.Message}";
                httpCtx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpCtx.Response.WriteAsync($"invalid request body: {ex.Message}", ct);
                return;
            }
            if (clientBody is null)
            {
                log.Error = "deserialize: null";
                httpCtx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Short-circuit on Anthropic server tools Copilot doesn't support.
            // Currently: web_search_* — the upstream's "unsupported_value" error
            // is opaque to Claude Code users; this surface tells them what to
            // do (configure an MCP search server instead). See research §15.4.
            if (TryGetUnsupportedServerTool(clientBody, out var badType))
            {
                log.Error = $"unsupported server tool: {badType}";
                httpCtx.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpCtx.Response.ContentType = "application/json";
                var msg = $"The '{badType}' server tool is not supported on the GitHub Copilot backend. Configure a custom search MCP server in your Claude Code MCP config (e.g. via --mcp-config) and disable the built-in WebSearch tool.";
                // JsonEncodedText handles escaping AOT-safely (no reflection).
                var bytes = Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"error\",\"error\":{{\"type\":\"not_supported\",\"message\":\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(msg)}\"}}}}");
                log.DownstreamBody = Encoding.UTF8.GetString(bytes);
                httpCtx.Response.ContentLength = bytes.Length;
                await httpCtx.Response.Body.WriteAsync(bytes, ct);
                return;
            }

            // Build a frozen view of inbound headers for the adapter; the
            // pipeline gets its own mutable copy to transform.
            var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in httpCtx.Request.Headers)
            {
                inboundHeaders[h.Key] = h.Value.ToString();
            }

            var irBody = await inboundAdapter.AdaptAsync(clientBody, inboundHeaders, ct);

            var bridgeCtx = new BridgeContext<MessagesRequest>
            {
                Request = new BridgeRequest<MessagesRequest>
                {
                    Method = httpCtx.Request.Method,
                    Path = httpCtx.Request.Path.Value ?? "",
                    RawBody = inboundBytes,
                    Body = irBody,
                    Headers = new Dictionary<string, string>(inboundHeaders, StringComparer.OrdinalIgnoreCase),
                },
                Response = new BridgeResponse(),
                Log = log,
                Ct = ct,
            };

            await runner.RunAsync(pipeline, bridgeCtx);

            // Audit-log capture of what we ended up sending upstream
            // (post-stage transforms).
            log.UpstreamUrl = bridgeCtx.Target?.Endpoint;
            var upstreamBody = JsonSerializer.SerializeToUtf8Bytes(bridgeCtx.Request.Body, JsonContext.Default.MessagesRequest);
            log.UpstreamBody = Encoding.UTF8.GetString(upstreamBody);
            foreach (var (k, v) in bridgeCtx.Request.Headers)
            {
                log.UpstreamHeaders[k] = v;
            }
            log.UpstreamStatus = bridgeCtx.Response.Status;
            foreach (var (k, v) in bridgeCtx.Response.Headers)
            {
                log.UpstreamResponseHeaders[k] = v;
            }

            httpCtx.Response.StatusCode = bridgeCtx.Response.Status;
            if (bridgeCtx.Response.Headers.TryGetValue("Content-Type", out var ctype))
            {
                httpCtx.Response.ContentType = ctype;
            }

            if (bridgeCtx.Response.Mode == ResponseMode.Streaming && bridgeCtx.Response.EventStream is not null)
            {
                var clientStream = outboundAdapter.AdaptStreamAsync(bridgeCtx.Response.EventStream, bridgeCtx, ct);
                await foreach (var evt in clientStream.WithCancellation(ct))
                {
                    log.Events.Add(new SseEventCapture(evt.EventType, evt.Data, Filtered: false));
                    await WriteSseEventAsync(httpCtx.Response, evt.EventType, evt.Data, ct);
                }
            }
            else if (bridgeCtx.Response.BufferedBody is not null)
            {
                var outBody = await outboundAdapter.AdaptBufferedAsync(bridgeCtx.Response.BufferedBody, bridgeCtx, ct);
                log.DownstreamBody = Encoding.UTF8.GetString(outBody);
                httpCtx.Response.ContentLength = outBody.Length;
                await httpCtx.Response.Body.WriteAsync(outBody, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Error = "cancelled by client";
            Log.Debug("endpoint cancelled by client");
            throw;
        }
        catch (Exception ex)
        {
            log.Error = ex.Message;
            Log.Debug($"endpoint exception: {ex.GetType().Name}: {ex.Message}");
            if (!httpCtx.Response.HasStarted)
            {
                httpCtx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await httpCtx.Response.WriteAsync($"upstream error: {ex.Message}", CancellationToken.None);
            }
        }
        finally
        {
            sw.Stop();
            log.DurationMs = sw.ElapsedMilliseconds;
            await logger.WriteAsync(log, CancellationToken.None);
            Log.Debug($"endpoint exit  duration_ms={log.DurationMs}");
        }
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
