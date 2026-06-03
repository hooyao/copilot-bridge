using System.Diagnostics;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// <c>GET /cc/v1/models</c> — projects Copilot's <c>/models</c> response into
/// the Anthropic shape, filtered to models advertising <c>/v1/messages</c>
/// support.
/// </summary>
internal static class ClaudeCodeModelsEndpoint
{
    private const string EpochCreatedAt = "1970-01-01T00:00:00Z";

    public static IEndpointRouteBuilder MapModels(this IEndpointRouteBuilder app)
    {
        app.MapGet("/cc/v1/models", HandleAsync);
        return app;
    }

    public static async Task HandleAsync(
        HttpContext httpCtx,
        ICopilotClient copilot,
        ILogger<ModelsTag> ioLogger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);

        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in httpCtx.Request.Headers)
        {
            inboundHeaders[header.Key] = header.Value.ToString();
        }
        ioLogger.LogInboundRequest(
            seq,
            traceId,
            httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "",
            inboundHeaders,
            [],
            0,
            bodyPooled: false);

        var responseStatus = StatusCodes.Status200OK;
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] responseBody = [];
        string? error = null;

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
            responseBody = JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.AnthropicModelsResponse);
            responseHeaders["Content-Type"] = "application/json";

            httpCtx.Response.ContentType = "application/json";
            httpCtx.Response.ContentLength = responseBody.Length;
            await httpCtx.Response.Body.WriteAsync(responseBody, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (!httpCtx.Response.HasStarted)
            {
                responseStatus = StatusCodes.Status502BadGateway;
                httpCtx.Response.StatusCode = responseStatus;
                await httpCtx.Response.WriteAsync($"failed to fetch models: {ex.Message}", CancellationToken.None);
            }
        }
        finally
        {
            sw.Stop();
            ioLogger.LogInboundResponse(
                seq,
                traceId,
                responseStatus,
                responseHeaders,
                responseBody,
                responseBody.Length,
                bodyPooled: false,
                error: error,
                durationMs: sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>Marker type used to give the models endpoint its own <c>ILogger</c> category.</summary>
internal sealed class ModelsTag { }
