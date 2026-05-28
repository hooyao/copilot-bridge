using System.Buffers;
using System.Diagnostics;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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
        ILogger<CountTokensTag> ioLogger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();

        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in httpCtx.Request.Headers)
        {
            inboundHeaders[header.Key] = header.Value.ToString();
        }

        var (inboundBuf, inboundLen) = await ReadBodyPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false);
        // The pooled buffer is forwarded to Copilot, so we keep it alive
        // for the strategy call. Hand a discrete (non-pooled) copy to the
        // audit so the sink can release it independently.
        var inboundAuditBody = new ReadOnlyMemory<byte>(inboundBuf, 0, inboundLen).ToArray();

        ioLogger.LogInboundRequest(
            seq,
            httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "",
            inboundHeaders,
            inboundAuditBody,
            inboundAuditBody.Length,
            bodyPooled: false);

        var responseStatus = StatusCodes.Status500InternalServerError;
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] responseBody = [];
        string? error = null;
        var upstreamLogged = false;

        try
        {
            using var upstream = await copilot.PostCountTokensAsync(
                inboundBuf.AsMemory(0, inboundLen).ToArray(), ct);

            // Upstream audit: count_tokens forwards the body unchanged, so
            // we can describe the upstream URL and reuse the inbound audit
            // body as the upstream-sent body.
            var upstreamHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // ICopilotClient hides the actual URL; record what we know.
            ioLogger.LogUpstreamRequest(
                seq,
                "POST",
                "https://api.githubcopilot.com/v1/messages/count_tokens",
                upstreamHeaders,
                inboundAuditBody,
                inboundAuditBody.Length,
                bodyPooled: false);

            var upstreamRespHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in upstream.Headers)
            {
                upstreamRespHeaders[h.Key] = string.Join(',', h.Value);
            }
            foreach (var h in upstream.Content.Headers)
            {
                upstreamRespHeaders[h.Key] = string.Join(',', h.Value);
            }

            var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
            responseStatus = (int)upstream.StatusCode;
            responseBody = bytes;

            ioLogger.LogUpstreamResponse(
                seq,
                responseStatus,
                upstreamRespHeaders,
                bytes,
                bytes.Length,
                bodyPooled: false);
            upstreamLogged = true;

            httpCtx.Response.StatusCode = responseStatus;
            if (upstream.Content.Headers.ContentType is { } ctype)
            {
                httpCtx.Response.ContentType = ctype.ToString();
                responseHeaders["Content-Type"] = ctype.ToString();
            }
            httpCtx.Response.ContentLength = bytes.Length;
            await httpCtx.Response.Body.WriteAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
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
            if (!upstreamLogged)
            {
                // Strategy never returned a response (network error, auth).
                // Log a minimal upstream-resp record so seq stays balanced.
                ioLogger.LogUpstreamResponse(
                    seq,
                    0,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [],
                    0,
                    bodyPooled: false,
                    error: error);
            }

            ioLogger.LogInboundResponse(
                seq,
                responseStatus,
                responseHeaders,
                responseBody,
                responseBody.Length,
                bodyPooled: false,
                error: error,
                durationMs: sw.ElapsedMilliseconds);

            ArrayPool<byte>.Shared.Return(inboundBuf, clearArray: false);
        }
    }

    private static async Task<(byte[] Buffer, int Length)> ReadBodyPooledAsync(Stream body, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
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
}

/// <summary>Marker type used to give count_tokens its own <c>ILogger</c> category.</summary>
internal sealed class CountTokensTag { }
