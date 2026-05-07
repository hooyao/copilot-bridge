using Microsoft.AspNetCore.Http;

namespace CopilotBridge.Cli.Hosting;

internal static class LogHelpers
{
    public static void CaptureInbound(HttpContext ctx, BridgeRequestLog log)
    {
        log.Method ??= ctx.Request.Method;
        log.Path ??= ctx.Request.Path.Value;
        foreach (var header in ctx.Request.Headers)
        {
            log.InboundHeaders[header.Key] = header.Value.ToString();
        }
    }
}
