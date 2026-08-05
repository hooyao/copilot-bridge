using System.Text.Json;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Endpoints.Codex;

/// <summary>Codex-native remote catalog at <c>GET /codex/models</c>.</summary>
internal static class CodexModelsEndpoint
{
    public static IEndpointRouteBuilder MapCodexModels(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<CodexModelCatalogOptions>>().Value.Enabled)
            return app;

        app.MapGet("/codex/models", HandleAsync);
        return app;
    }

    public static async Task HandleAsync(
        HttpContext httpCtx,
        ICodexCatalogSourceCache sourceCache,
        CodexCatalogOverlayService overlays,
        CodexCatalogProjector projector)
    {
        var values = httpCtx.Request.Query["client_version"];
        if (values.Count != 1)
        {
            await WriteErrorAsync(httpCtx, StatusCodes.Status400BadRequest,
                "client_version must appear exactly once.");
            return;
        }
        if (!CodexCatalogRequestIdentity.TryResolve(
                values[0], httpCtx.Request.Headers.UserAgent.ToString(), out var exactVersion, out var identityError))
        {
            await WriteErrorAsync(httpCtx, StatusCodes.Status400BadRequest, identityError!);
            return;
        }
        var resolution = await sourceCache.ResolveAsync(exactVersion.ToString(), httpCtx.RequestAborted);
        if (!resolution.Success)
        {
            await WriteErrorAsync(httpCtx, StatusCodes.Status400BadRequest,
                resolution.Error ?? "Exact Codex catalog source is unavailable.");
            return;
        }

        var overlay = await overlays.GetAsync(httpCtx.RequestAborted);
        var projection = projector.Project(resolution.Baseline!, overlay.Models, overlay.IsValidated);
        var response = new CodexModelsResponse { Models = projection.Models };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.CodexModelsResponse);

        httpCtx.Response.StatusCode = StatusCodes.Status200OK;
        httpCtx.Response.ContentType = "application/json";
        httpCtx.Response.Headers.ETag = projection.ETag;
        httpCtx.Response.ContentLength = bytes.Length;
        await httpCtx.Response.Body.WriteAsync(bytes, httpCtx.RequestAborted);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new CodexCatalogErrorResponse { Error = new CodexCatalogError { Message = message, Type = "invalid_request_error" } },
            JsonContext.Default.CodexCatalogErrorResponse), CancellationToken.None);
    }
}
