using System.Buffers;
using System.Diagnostics;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    public static IEndpointRouteBuilder MapCountTokens(this IEndpointRouteBuilder app)
    {
        app.MapPost("/cc/v1/messages/count_tokens", HandleAsync);
        return app;
    }

    public static async Task HandleAsync(
        HttpContext httpCtx,
        ICopilotClient copilot,
        RequestSummaryLogger summaryLogger,
        ILogger<CountTokensTag> ioLogger)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);

        // Correlate this request's log lines — the summary in particular — with
        // the trace id: push the RAW id onto Serilog's LogContext as "ReqTrace"
        // for the whole handler, so ReqTraceFormatEnricher prefixes them with
        // "[<id>] ". The summary carries no id itself, so without this scope it
        // would render with no trace id at all. Matches the messages/codex
        // endpoints (this one has no pipeline stages or enter/exit lines, but its
        // summary still needs the id).
        using var _traceScope = Serilog.Context.LogContext.PushProperty("ReqTrace", traceId);

        var inboundHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in httpCtx.Request.Headers)
        {
            inboundHeaders[header.Key] = header.Value.ToString();
        }

        var (inboundBuf, inboundLen) = await ReadBodyPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false);
        var inboundAuditBody = new ReadOnlyMemory<byte>(inboundBuf, 0, inboundLen).ToArray();

        ioLogger.LogInboundRequest(
            seq,
            traceId,
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

        // The count_tokens endpoint never goes through the pipeline, so the
        // summary line is much shorter — it just identifies the request, the
        // model the client asked for, and the resulting input_tokens count.
        var summary = new RequestSummary { Kind = "count_tokens" };
        // Inbound beta tokens — same parser as the messages endpoint uses.
        var betaSet = ClaudeCodeInboundAdapter.ParseInboundBetas(inboundHeaders);
        if (betaSet.Count > 0)
        {
            var arr = new string[betaSet.Count];
            var i = 0;
            foreach (var t in betaSet) arr[i++] = t;
            summary.InboundBetas = arr;
        }
        // Cheap model probe: look for "model":"..." in the JSON body without
        // a full deserialize (count_tokens has many fields we don't model).
        summary.RequestedModel = TryReadModelFromJson(inboundAuditBody);
        summary.ResolvedModel = summary.RequestedModel;

        try
        {
            using var upstream = await copilot.PostCountTokensAsync(
                inboundBuf.AsMemory(0, inboundLen).ToArray(), ct);

            var upstreamHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ioLogger.LogUpstreamRequest(
                seq,
                traceId,
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

            // Pull input_tokens out for the summary line.
            UsageProbe.TryReadCountTokens(bytes, summary.Usage);

            ioLogger.LogUpstreamResponse(
                seq,
                traceId,
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
            summary.StatusCode = responseStatus;
            summary.DurationMs = sw.ElapsedMilliseconds;
            summaryLogger.Log(summary);

            if (!upstreamLogged)
            {
                ioLogger.LogUpstreamResponse(
                    seq,
                    traceId,
                    0,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [],
                    0,
                    bodyPooled: false,
                    error: error);
            }

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

            ArrayPool<byte>.Shared.Return(inboundBuf, clearArray: false);
        }
    }

    /// <summary>
    /// Cheap model probe: scan the JSON body for the top-level <c>"model"</c>
    /// property without spinning up a full <see cref="System.Text.Json.JsonDocument"/>.
    /// Returns null when the body is empty or the field can't be found —
    /// not fatal, the summary just shows "?" for the model.
    /// </summary>
    private static string? TryReadModelFromJson(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("model", out var modelProp)
                && modelProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return modelProp.GetString();
            }
        }
        catch (System.Text.Json.JsonException) { /* malformed body — skip */ }
        return null;
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
