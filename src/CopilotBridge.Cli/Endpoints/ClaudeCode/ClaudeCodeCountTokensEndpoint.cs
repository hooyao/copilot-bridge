using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Errors;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// Route-aware <c>POST /cc/v1/messages/count_tokens</c>. Native Anthropic
/// targets retain exact byte passthrough. Responses targets translate the
/// count input through the shared route planner and T2 builder, then calibrate
/// Copilot's raw count into target-equivalent input tokens.
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
        RequestAudit audit,
        ModelRoutePlanner routePlanner,
        BridgeContext<MessagesRequest> bridgeCtx,
        CodexModelProfileCatalog codexProfiles,
        ResponsesAdmissionEstimator estimator,
        IOptions<CcToResponsesOptions> ccOptions)
    {
        var ct = httpCtx.RequestAborted;
        var sw = Stopwatch.StartNew();
        var seq = BridgeIoSeq.Next();
        var traceId = BridgeIoSeq.BuildTraceId(seq, DateTime.UtcNow);
        using var _traceScope = Serilog.Context.LogContext.PushProperty("ReqTrace", traceId);

        var inboundHeaders = Snapshot(httpCtx.Request.Headers);
        byte[] inboundBody;
        using (var inbound = await InboundBody.ReadPooledAsync(httpCtx.Request.Body, ct).ConfigureAwait(false))
            inboundBody = inbound.Memory.ToArray();

        audit.RecordInbound(
            seq, traceId, httpCtx.Request.Method,
            httpCtx.Request.Path.Value ?? "", inboundHeaders, inboundBody);

        var responseStatus = StatusCodes.Status500InternalServerError;
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] responseBody = [];
        string? error = null;
        var upstreamLogged = false;
        var summary = new RequestSummary { Kind = "count_tokens" };
        var inboundBetas = ClaudeCodeInboundAdapter.ParseInboundBetas(inboundHeaders);
        summary.InboundBetas = inboundBetas.ToArray();

        try
        {
            string? requestedModel;
            string? requestedEffort = null;
            try
            {
                using var modelDoc = JsonDocument.Parse(inboundBody);
                var root = modelDoc.RootElement;
                requestedModel = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("model", out var model)
                    && model.ValueKind == JsonValueKind.String
                        ? model.GetString()
                        : null;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("output_config", out var outputConfig)
                    && outputConfig.ValueKind == JsonValueKind.Object
                    && outputConfig.TryGetProperty("effort", out var effort)
                    && effort.ValueKind == JsonValueKind.String)
                    requestedEffort = effort.GetString();
            }
            catch (JsonException ex)
            {
                responseHeaders["Content-Type"] = "application/json";
                responseBody = await WriteAnthropicErrorAsync(
                    httpCtx, StatusCodes.Status400BadRequest,
                    "invalid request body: " + ex.Message, ct);
                responseStatus = StatusCodes.Status400BadRequest;
                error = ex.Message;
                return;
            }

            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                responseStatus = StatusCodes.Status400BadRequest;
                error = "count_tokens body requires model and messages";
                responseHeaders["Content-Type"] = "application/json";
                responseBody = await WriteAnthropicErrorAsync(
                    httpCtx, responseStatus, error, ct);
                return;
            }

            summary.RequestedModel = requestedModel;
            summary.ResolvedModel = requestedModel;

            PopulateContext(
                bridgeCtx,
                new MessagesRequest
                {
                    Model = requestedModel,
                    Messages = [],
                    OutputConfig = requestedEffort is null
                        ? null
                        : new OutputConfig { Effort = requestedEffort },
                },
                httpCtx.Request.Method,
                httpCtx.Request.Path.Value ?? "",
                inboundHeaders,
                inboundBetas,
                traceId,
                ct);

            var target = routePlanner.Plan(bridgeCtx);
            summary.ResolvedModel = bridgeCtx.Request.Body.Model;
            summary.TargetVendor = target.Vendor.ToString();
            summary.TargetEndpoint = "/v1/messages/count_tokens";
            summary.OutboundEffort = bridgeCtx.Request.Body.OutputConfig?.Effort;

            byte[] upstreamBody;
            if (target.Vendor == BackendVendor.CopilotAnthropic)
            {
                // Preserve every unmodeled field for the working native protocol.
                upstreamBody = inboundBody;
            }
            else if (target.Vendor == BackendVendor.CopilotResponses)
            {
                if (!ResponsesCountRequest.TryParse(
                        inboundBody, out var countShape, out var shapeError))
                {
                    responseStatus = StatusCodes.Status400BadRequest;
                    error = shapeError;
                    responseHeaders["Content-Type"] = "application/json";
                    responseBody = await WriteAnthropicErrorAsync(
                        httpCtx, responseStatus, error!, ct);
                    return;
                }

                // Plan the strict count shape in a fresh context. The model-only
                // probe above intentionally preserves native passthrough, but a
                // Location may also accumulate header/beta side effects. Reusing
                // that mutable context would apply those effects twice.
                bridgeCtx = new BridgeContext<MessagesRequest>();
                PopulateContext(
                    bridgeCtx,
                    countShape!,
                    httpCtx.Request.Method,
                    httpCtx.Request.Path.Value ?? "",
                    inboundHeaders,
                    inboundBetas,
                    traceId,
                    ct);
                target = routePlanner.Plan(bridgeCtx);
                var filterAgent = ccOptions.Value.PreventRecursiveAgentDelegation
                    && bridgeCtx.IsClaudeCodeSubagent;
                upstreamBody = ResponsesRequestBuilder.Build(
                    bridgeCtx.Request.Body, codexProfiles, filterAgent).Body;
            }
            else
            {
                responseStatus = StatusCodes.Status400BadRequest;
                error = $"count_tokens does not support target backend {target.Vendor}";
                responseHeaders["Content-Type"] = "application/json";
                responseBody = await WriteAnthropicErrorAsync(
                    httpCtx, responseStatus, error, ct);
                return;
            }

            using var upstream = await copilot.PostCountTokensAsync(upstreamBody, ct);
            var upstreamUrl =
                $"{auth.CopilotApiBaseUrl ?? "https://api.githubcopilot.com"}/v1/messages/count_tokens";
            audit.RecordUpstreamRequest(
                seq, traceId, "POST", upstreamUrl,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                upstreamBody, upstreamBody.Length);

            var upstreamRespHeaders = Snapshot(upstream.Headers, upstream.Content.Headers);
            var rawResponse = await upstream.Content.ReadAsByteArrayAsync(ct);
            responseStatus = (int)upstream.StatusCode;
            audit.RecordUpstreamResponse(
                seq, traceId, responseStatus, upstreamRespHeaders,
                rawResponse, rawResponse.Length);
            upstreamLogged = true;

            responseBody = rawResponse;
            CopyContentType(upstream, httpCtx, responseHeaders);

            if (target.Vendor == BackendVendor.CopilotResponses
                && upstream.IsSuccessStatusCode)
            {
                if (!CountTokensResponseParser.TryParse(
                        rawResponse, out var rawCount, out var parseError))
                {
                    responseStatus = StatusCodes.Status502BadGateway;
                    error = parseError;
                    responseBody = SerializeAnthropicError(
                        "invalid upstream count_tokens response: " + parseError,
                        "api_error");
                    responseHeaders["Content-Type"] = "application/json";
                }
                else
                {
                    var estimate = estimator.Estimate(
                        bridgeCtx.Request.Body.Model, rawCount);
                    responseBody = JsonSerializer.SerializeToUtf8Bytes(
                        new CountTokensResponse { InputTokens = estimate.InputTokens },
                        JsonContext.Default.CountTokensResponse);
                    responseHeaders["Content-Type"] = "application/json";
                    summary.RawCountInputTokens = rawCount;
                    summary.CountCalibrationId = estimate.CalibrationId;
                    summary.CountCalibrationReserve = estimate.Reserve;
                    summary.ReturnedCountInputTokens = estimate.InputTokens;
                    summary.Usage.InputTokens = estimate.InputTokens;
                }
            }
            else
            {
                UsageProbe.TryReadCountTokens(responseBody, summary.Usage);
            }

            httpCtx.Response.StatusCode = responseStatus;
            if (responseHeaders.TryGetValue("Content-Type", out var contentType))
                httpCtx.Response.ContentType = contentType;
            httpCtx.Response.ContentLength = responseBody.Length;
            await httpCtx.Response.Body.WriteAsync(responseBody, ct);
        }
        catch (UnknownModelException ex)
        {
            responseStatus = StatusCodes.Status400BadRequest;
            error = ex.Message;
            responseBody = SerializeAnthropicError(ex.Message);
            responseHeaders["Content-Type"] = "application/json";
            await WriteBytesAsync(httpCtx, responseStatus, responseBody, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            responseStatus = StatusCodes.Status502BadGateway;
            error = ex.Message;
            responseBody = SerializeAnthropicError(
                "upstream error: " + ex.Message, "api_error");
            responseHeaders["Content-Type"] = "application/json";
            if (!httpCtx.Response.HasStarted)
                await WriteBytesAsync(httpCtx, responseStatus, responseBody, CancellationToken.None);
        }
        finally
        {
            sw.Stop();
            summary.StatusCode = responseStatus;
            summary.DurationMs = sw.ElapsedMilliseconds;
            summary.Error = error;
            summaryLogger.Log(summary);

            if (!upstreamLogged)
            {
                audit.RecordUpstreamResponse(
                    seq, traceId, 0,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [], 0, error: error);
            }

            audit.RecordInboundResponse(
                seq, traceId, responseStatus, responseHeaders,
                responseBody, responseBody.Length,
                error: error, durationMs: sw.ElapsedMilliseconds);
        }
    }

    private static Dictionary<string, string> Snapshot(
        IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers) result[h.Key] = h.Value.ToString();
        return result;
    }

    private static void PopulateContext(
        BridgeContext<MessagesRequest> context,
        MessagesRequest body,
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlySet<string> inboundBetas,
        string traceId,
        CancellationToken ct)
    {
        context.Request = new BridgeRequest<MessagesRequest>
        {
            Method = method,
            Path = path,
            Body = body,
            Headers = new Dictionary<string, string>(
                headers, StringComparer.OrdinalIgnoreCase),
        };
        context.Response = new BridgeResponse();
        context.Ct = ct;
        context.TraceId = traceId;
        context.InboundBetas = inboundBetas;
        context.IsClaudeCodeSubagent =
            ClaudeCodeMessagesEndpoint.IsClaudeCodeSubagent(headers);
    }

    private static Dictionary<string, string> Snapshot(
        HttpResponseHeaders headers,
        HttpContentHeaders contentHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers) result[h.Key] = string.Join(',', h.Value);
        foreach (var h in contentHeaders) result[h.Key] = string.Join(',', h.Value);
        return result;
    }

    private static void CopyContentType(
        HttpResponseMessage upstream,
        HttpContext httpCtx,
        IDictionary<string, string> responseHeaders)
    {
        if (upstream.Content.Headers.ContentType is not { } contentType) return;
        var value = contentType.ToString();
        httpCtx.Response.ContentType = value;
        responseHeaders["Content-Type"] = value;
    }

    private static byte[] SerializeAnthropicError(
        string message,
        string type = "invalid_request_error") =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ErrorResponse
            {
                Error = new ErrorBody { Type = type, Message = message },
            },
            JsonContext.Default.ErrorResponse);

    private static async Task<byte[]> WriteAnthropicErrorAsync(
        HttpContext context,
        int status,
        string message,
        CancellationToken ct)
    {
        var body = SerializeAnthropicError(message);
        await WriteBytesAsync(context, status, body, ct);
        return body;
    }

    private static async Task WriteBytesAsync(
        HttpContext context,
        int status,
        byte[] body,
        CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, ct);
    }
}
