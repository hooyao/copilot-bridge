using System.Buffers;
using System.Net.Http.Headers;
using CopilotBridge.Cli.Copilot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Endpoints.Images;

/// <summary>Raw OpenAI Images transport; JSON and multipart bodies stay opaque.</summary>
internal static class ImagesEndpoint
{
    public static IEndpointRouteBuilder MapImages(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/images/generations", GenerateAsync);
        app.MapPost("/v1/images/edits", EditAsync);
        app.MapPost("/codex/images/generations", GenerateAsync);
        app.MapPost("/codex/images/edits", EditAsync);
        return app;
    }

    private static Task GenerateAsync(HttpContext context, IImagesClient client, ILogger<ImagesEndpointTag> log) =>
        HandleAsync(context, client, log, ImageOperation.Generate);

    private static Task EditAsync(HttpContext context, IImagesClient client, ILogger<ImagesEndpointTag> log) =>
        HandleAsync(context, client, log, ImageOperation.Edit);

    internal static async Task HandleAsync(
        HttpContext context, IImagesClient client, ILogger<ImagesEndpointTag> log, ImageOperation operation)
    {
        var ct = context.RequestAborted;
        try
        {
            HttpResponseMessage upstream;
            using (var body = await InboundBody.ReadPooledAsync(context.Request.Body, ct))
                upstream = await client.PostImageAsync(operation, body.Memory, context.Request.ContentType, ct);

            using (upstream)
            {
                context.Response.StatusCode = (int)upstream.StatusCode;
                var excluded = HopByHopHeaders(upstream.Headers);
                CopyHeaders(upstream.Headers, context.Response, excluded);
                CopyHeaders(upstream.Content.Headers, context.Response, excluded);
                await context.Response.StartAsync(ct);
                await using var source = await upstream.Content.ReadAsStreamAsync(ct);
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
                    {
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Images upstream request failed");
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.StatusCode = ex is UpstreamTimeoutException
                ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"Bridge could not reach the upstream service\",\"type\":\"upstream_error\"}}", ct);
        }
    }

    private static HashSet<string> HopByHopHeaders(HttpResponseHeaders headers)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        };
        foreach (var value in headers.Connection) excluded.Add(value);
        return excluded;
    }

    private static void CopyHeaders(HttpHeaders headers, HttpResponse destination, HashSet<string> excluded)
    {
        foreach (var header in headers)
            if (!excluded.Contains(header.Key)) destination.Headers[header.Key] = header.Value.ToArray();
    }
}

internal sealed class ImagesEndpointTag { }
