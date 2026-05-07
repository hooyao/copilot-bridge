using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.CountTokens;
using Microsoft.AspNetCore.Http;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>POST /cc/v1/messages/count_tokens</c> — M1 placeholder so Claude Code's
/// preflight doesn't 404. Returns <c>{"input_tokens":1}</c>. M3 substitutes a
/// real estimate.
/// </summary>
internal static class ClaudeCodeCountTokensEndpoint
{
    public static async Task HandleAsync(HttpContext httpCtx, BridgeRequestLogger logger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var log = new BridgeRequestLog();
        LogHelpers.CaptureInbound(httpCtx, log);

        try
        {
            await using var ms = new MemoryStream();
            await httpCtx.Request.Body.CopyToAsync(ms, ct);
            log.InboundBody = Encoding.UTF8.GetString(ms.ToArray());

            var response = new CountTokensResponse { InputTokens = 1 };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.CountTokensResponse);
            log.DownstreamBody = Encoding.UTF8.GetString(bytes);

            httpCtx.Response.ContentType = "application/json";
            httpCtx.Response.ContentLength = bytes.Length;
            await httpCtx.Response.Body.WriteAsync(bytes, ct);
        }
        finally
        {
            sw.Stop();
            log.DurationMs = sw.ElapsedMilliseconds;
            await logger.WriteAsync(log, CancellationToken.None);
        }
    }
}
