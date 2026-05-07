using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Models;
using Microsoft.AspNetCore.Http;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>GET /cc/v1/models</c> — projects Copilot's <c>/models</c> response into
/// the Anthropic shape, filtered to models advertising <c>/v1/messages</c>
/// support.
/// </summary>
internal static class ClaudeCodeModelsEndpoint
{
    private const string EpochCreatedAt = "1970-01-01T00:00:00Z";

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
            var upstream = await copilot.GetModelsAsync(ct);
            var data = new List<AnthropicModelInfo>();
            foreach (var m in upstream.Data)
            {
                if (m.SupportedEndpoints?.Contains("/v1/messages") != true) continue;
                data.Add(new AnthropicModelInfo
                {
                    Id = m.Id,
                    DisplayName = m.Name ?? m.Id,
                    CreatedAt = EpochCreatedAt,
                    MaxInputTokens = m.Capabilities?.Limits?.MaxContextWindowTokens,
                    MaxTokens = m.Capabilities?.Limits?.MaxOutputTokens,
                });
            }

            var response = new AnthropicModelsResponse { Data = data };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.AnthropicModelsResponse);
            log.DownstreamBody = Encoding.UTF8.GetString(bytes);

            httpCtx.Response.ContentType = "application/json";
            httpCtx.Response.ContentLength = bytes.Length;
            await httpCtx.Response.Body.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            log.Error = ex.Message;
            if (!httpCtx.Response.HasStarted)
            {
                httpCtx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await httpCtx.Response.WriteAsync($"failed to fetch models: {ex.Message}", CancellationToken.None);
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
