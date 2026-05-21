using System.Diagnostics;
using System.Text;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting;
using Microsoft.AspNetCore.Http;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>POST /cc/v1/messages/count_tokens</c> — thin passthrough to Copilot's
/// own count_tokens endpoint. Verified by <c>CopilotGapProbes</c>: Copilot
/// returns Anthropic-spec-compatible <c>{input_tokens:N}</c>, so no
/// translation is needed. Body is forwarded raw; no pipeline transforms.
/// </summary>
internal static class ClaudeCodeCountTokensEndpoint
{
    public static async Task HandleAsync(
        HttpContext httpCtx,
        ICopilotClient copilot,
        BridgeRequestLogger logger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var log = new BridgeRequestLog();
        LogHelpers.CaptureInbound(httpCtx, log);

        try
        {
            await using var ms = new MemoryStream();
            await httpCtx.Request.Body.CopyToAsync(ms, ct);
            var inboundBytes = ms.ToArray();
            log.InboundBody = Encoding.UTF8.GetString(inboundBytes);
            log.UpstreamBody = log.InboundBody;

            using var upstream = await copilot.PostCountTokensAsync(inboundBytes, ct);
            log.UpstreamStatus = (int)upstream.StatusCode;
            foreach (var h in upstream.Headers)
            {
                log.UpstreamResponseHeaders[h.Key] = string.Join(',', h.Value);
            }
            foreach (var h in upstream.Content.Headers)
            {
                log.UpstreamResponseHeaders[h.Key] = string.Join(',', h.Value);
            }

            var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
            log.DownstreamBody = Encoding.UTF8.GetString(bytes);

            httpCtx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } ctype)
            {
                httpCtx.Response.ContentType = ctype.ToString();
            }
            httpCtx.Response.ContentLength = bytes.Length;
            await httpCtx.Response.Body.WriteAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error = ex.Message;
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
        }
    }
}
