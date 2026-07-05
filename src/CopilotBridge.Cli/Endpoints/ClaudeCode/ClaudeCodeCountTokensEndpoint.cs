using System.Diagnostics;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        IAuthService auth,
        RequestSummaryLogger summaryLogger,
        RequestAudit audit)
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

        // Read the body via the shared pooled reader. count_tokens needs the bytes
        // as an independent array regardless of tracing (the summary model probe and
        // the forwarded upstream POST both consume them), so materialize ONE owned
        // copy here and reuse it for probe + POST + audit. Dispose the pooled reader
        // immediately — the copy is independent, so we release the pooled buffer back
        // to the manager right away rather than pinning it across the upstream POST.
        byte[] inboundAuditBody;
        using (var inbound = await InboundBody.ReadPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false))
        {
            inboundAuditBody = inbound.Memory.ToArray();
        }

        audit.RecordInbound(
            seq,
            traceId,
            httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "",
            inboundHeaders,
            inboundAuditBody);

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
            using var upstream = await copilot.PostCountTokensAsync(inboundAuditBody, ct);

            var upstreamHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Audit the ACTUAL upstream URL the client POSTed to. CopilotClient builds
            // it as `{baseUrl}/v1/messages/count_tokens` where baseUrl comes from the
            // resolved Copilot token (enterprise → api.enterprise.githubcopilot.com);
            // a hardcoded api.githubcopilot.com would misreport it for non-individual
            // accounts. Populated by now — PostCountTokensAsync forced the token fetch.
            var upstreamUrl = $"{auth.CopilotApiBaseUrl ?? "https://api.githubcopilot.com"}/v1/messages/count_tokens";
            audit.RecordUpstreamRequest(
                seq,
                traceId,
                "POST",
                upstreamUrl,
                upstreamHeaders,
                inboundAuditBody,
                inboundAuditBody.Length);

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

            audit.RecordUpstreamResponse(
                seq,
                traceId,
                responseStatus,
                upstreamRespHeaders,
                bytes,
                bytes.Length);
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
                audit.RecordUpstreamResponse(
                    seq,
                    traceId,
                    0,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [],
                    0,
                    error: error);
            }

            audit.RecordInboundResponse(
                seq,
                traceId,
                responseStatus,
                responseHeaders,
                responseBody,
                responseBody.Length,
                error: error,
                durationMs: sw.ElapsedMilliseconds);
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
}
