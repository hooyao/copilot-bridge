using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// The Codex/Responses backend strategy. Holds T2 (IR → Responses wire request)
/// and T3 (Copilot <c>/responses</c> SSE → IR Anthropic stream). Selected by
/// <see cref="Matches"/> when the resolved target is
/// <see cref="BackendVendor.CopilotResponses"/>. Mirrors
/// <c>CopilotMessagesPassthroughStrategy</c>'s shape, but the body and the
/// stream are TRANSLATED (not passthrough) because the backend speaks Responses,
/// not Anthropic.
/// </summary>
internal sealed class CopilotResponsesStrategy : IUpstreamStrategy<MessagesRequest>
{
    private readonly ICopilotClient _copilot;
    private readonly CodexModelProfileCatalog _profiles;
    private readonly ILogger<CopilotResponsesStrategy> _log;

    public CopilotResponsesStrategy(
        ICopilotClient copilot,
        CodexModelProfileCatalog profiles,
        ILogger<CopilotResponsesStrategy> log)
    {
        _copilot = copilot;
        _profiles = profiles;
        _log = log;
    }

    public string Name => "CopilotResponses(T2/T3)";

    public bool Matches(RouteTarget target) =>
        target.Vendor == BackendVendor.CopilotResponses
        && target.Endpoint == "/responses";

    public async Task ForwardAsync(BridgeContext<MessagesRequest> ctx)
    {
        // ── T2: IR MessagesRequest → Responses wire bytes ──
        var (body, vision) = ResponsesRequestBuilder.Build(ctx.Request.Body, _profiles);

        _log.LogDebug("strategy {Name}: T2 built Responses body bytes={Bytes} vision={Vision} model={Model}",
            Name, body.Length, vision, ctx.Request.Body.Model);

        var resp = await _copilot.PostResponsesAsync(body, vision, ctx.Ct);

        ctx.Response.Status = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);
        foreach (var h in resp.Content.Headers)
            ctx.Response.Headers[h.Key] = string.Join(',', h.Value);

        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
        var streaming = ctx.Request.Body.Stream == true
            && resp.IsSuccessStatusCode
            && contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (streaming)
        {
            ctx.Response.Mode = ResponseMode.Streaming;
            // T3: translate Responses SSE → IR (Anthropic) SSE on the fly. The
            // response stages + the outbound T4 adapter then operate on IR shape.
            ctx.Response.EventStream = TranslateStreamAsync(resp, ctx.Request.Body.Model, ctx.Ct);
            _log.LogDebug("strategy {Name}: T3 streaming (content-type={ContentType})", Name, contentType);
        }
        else
        {
            ctx.Response.Mode = ResponseMode.Buffered;
            try
            {
                // Non-streaming or error: buffer the raw Responses body. On the
                // success path this is a Responses JSON object T4 will translate;
                // on the error path it's the Copilot error envelope, passed through.
                ctx.Response.BufferedBody = await resp.Content.ReadAsByteArrayAsync(ctx.Ct);
                _log.LogDebug("strategy {Name}: buffered status={Status} bytes={Bytes}",
                    Name, ctx.Response.Status, ctx.Response.BufferedBody.Length);
            }
            finally
            {
                resp.Dispose();
            }
        }
    }

    /// <summary>
    /// T3 — Copilot <c>/responses</c> SSE → IR Anthropic stream events. A stateful
    /// state machine: the Responses event grammar
    /// (<c>response.created</c> → <c>output_item.added/done</c> →
    /// <c>output_text.delta</c> / <c>function_call_arguments.delta</c> →
    /// <c>response.completed</c>) maps to the Anthropic grammar
    /// (<c>message_start</c> → <c>content_block_start/_delta/_stop</c> →
    /// <c>message_delta</c> → <c>message_stop</c>). No <c>[DONE]</c> to handle.
    /// </summary>
    private async IAsyncEnumerable<SseItem<string>> TranslateStreamAsync(
        HttpResponseMessage resp,
        string model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            stream = await resp.Content.ReadAsStreamAsync(ct);
            var parser = SseParser.Create(stream);
            var sm = new ResponsesToAnthropicStream(model);
            await foreach (var evt in parser.EnumerateAsync(ct))
            {
                foreach (var irItem in sm.Translate(evt))
                    yield return irItem;
            }
            foreach (var tail in sm.Flush())
                yield return tail;
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
            resp.Dispose();
        }
    }
}
