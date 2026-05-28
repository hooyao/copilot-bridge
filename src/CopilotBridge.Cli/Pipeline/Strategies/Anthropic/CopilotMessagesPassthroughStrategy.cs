using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Strategies.Anthropic;

/// <summary>
/// M1's only strategy. Forwards a fully-preprocessed
/// <see cref="MessagesRequest"/> to Copilot's <c>POST /v1/messages</c>; the
/// response shape is already what the Anthropic-shape client expects, so this
/// is true byte-level passthrough — no body translation, no SSE event
/// translation. The <c>[DONE]</c> filter and any other event-shape work live
/// in the response pipeline (<see cref="Response.IResponseStage{TBody}"/>),
/// not here.
/// </summary>
internal sealed class CopilotMessagesPassthroughStrategy : IUpstreamStrategy<MessagesRequest>
{
    private readonly ICopilotClient _copilot;

    public CopilotMessagesPassthroughStrategy(ICopilotClient copilot)
    {
        _copilot = copilot;
    }

    public string Name => "CopilotMessagesPassthrough";

    public bool Matches(RouteTarget target) =>
        target.Vendor == BackendVendor.CopilotAnthropic
        && target.Endpoint == "/v1/messages";

    public async Task ForwardAsync(BridgeContext<MessagesRequest> ctx)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(ctx.Request.Body, JsonContext.Default.MessagesRequest);

        // HeadersOutboundStage emits these into ctx.Request.Headers; we read
        // them back here as the existing CopilotClient.PostMessagesAsync
        // signature still takes vision/anthropicBeta as explicit params (the
        // static 7 headers come from CopilotHeaderFactory inside the client).
        var vision = ctx.Request.Headers.ContainsKey("copilot-vision-request");
        IReadOnlyList<string>? beta = null;
        if (ctx.Request.Headers.TryGetValue("anthropic-beta", out var betaStr) && betaStr.Length > 0)
        {
            beta = betaStr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        Log.Debug($"strategy {Name}: forwarding bytes={body.Length} vision={vision} betas={(beta is null ? 0 : beta.Count)}");

        var resp = await _copilot.PostMessagesAsync(body, vision, beta, ctx.Ct);

        ctx.Response.Status = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
        {
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        }
        foreach (var h in resp.Content.Headers)
        {
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        }

        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
        var streaming = ctx.Request.Body.Stream == true
            && resp.IsSuccessStatusCode
            && contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (streaming)
        {
            ctx.Response.Mode = ResponseMode.Streaming;
            // Ownership of `resp` transfers to the iterator — disposed when
            // the consumer (the endpoint writer) finishes enumeration.
            ctx.Response.EventStream = StreamEventsAsync(resp, ctx.Ct);
            Log.Debug($"strategy {Name}: streaming (content-type={contentType})");
        }
        else
        {
            ctx.Response.Mode = ResponseMode.Buffered;
            try
            {
                ctx.Response.BufferedBody = await resp.Content.ReadAsByteArrayAsync(ctx.Ct);
                Log.Debug($"strategy {Name}: buffered status={ctx.Response.Status} bytes={ctx.Response.BufferedBody.Length}");
            }
            finally
            {
                resp.Dispose();
            }
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> StreamEventsAsync(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            stream = await resp.Content.ReadAsStreamAsync(ct);
            var parser = SseParser.Create(stream);
            await foreach (var evt in parser.EnumerateAsync(ct))
            {
                yield return evt;
            }
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
            resp.Dispose();
        }
    }
}
