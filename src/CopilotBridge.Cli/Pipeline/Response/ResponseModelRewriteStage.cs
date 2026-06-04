using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response;

/// <summary>
/// Restores the client-requested model id in the response so client-side
/// accounting (ccusage, the Claude Code session jsonl) reports the model
/// the user actually asked for, not the back-end variant the bridge routed
/// to. Without this, a request for <c>claude-opus-4-8</c> that the bridge
/// rewrote to <c>claude-opus-4.7-1m-internal</c> shows up downstream as
/// <c>claude-opus-4-7</c> because Anthropic's API spec returns the wire
/// model in <c>message.model</c>. Toggle via
/// <see cref="ResponseModelRewriteOptions.Enabled"/> in appsettings.json
/// (defaults to on).
/// </summary>
/// <remarks>
/// <para><b>No-op when irrelevant.</b> Skipped when the option is disabled,
/// when <see cref="BridgeContext{T}.OriginalRequestedModel"/> is null
/// (router never ran), or when the original equals the resolved id (no
/// rewrite happened).</para>
/// <para><b>Buffered path.</b> Parses the JSON body with
/// <see cref="JsonNode"/>, replaces the top-level <c>model</c> string, and
/// re-serializes. Errors (non-JSON body, unexpected shape) fall through
/// silently — never block a real response on a rewrite failure.</para>
/// <para><b>Streaming path.</b> Wraps the upstream SSE sequence and rewrites
/// the FIRST <c>message_start</c> event's payload (which carries the model
/// id under <c>message.model</c>). Subsequent events (<c>content_block_*</c>,
/// <c>message_delta</c>, <c>message_stop</c>) don't carry a model field and
/// pass through unchanged.</para>
/// </remarks>
internal sealed class ResponseModelRewriteStage : IResponseStage<MessagesRequest>
{
    private readonly ILogger<ResponseModelRewriteStage> _log;
    private readonly ResponseModelRewriteOptions _opts;

    public ResponseModelRewriteStage(
        ILogger<ResponseModelRewriteStage> log,
        IOptions<ResponseModelRewriteOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public string Name => "ResponseModelRewrite";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        if (!_opts.Enabled) return Task.CompletedTask;

        var original = ctx.OriginalRequestedModel;
        var resolved = ctx.Request.Body.Model;
        if (string.IsNullOrEmpty(original) || string.Equals(original, resolved, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        if (ctx.Response.Mode == ResponseMode.Buffered && ctx.Response.BufferedBody is { Length: > 0 } body)
        {
            var rewritten = TryRewriteBufferedBody(body, original);
            if (rewritten is not null)
            {
                ctx.Response.BufferedBody = rewritten;
                _log.LogDebug("stage {Name}: buffered body model '{Resolved}' → '{Original}'",
                    Name, resolved, original);
            }
            return Task.CompletedTask;
        }

        if (ctx.Response.Mode == ResponseMode.Streaming && ctx.Response.EventStream is not null)
        {
            var source = ctx.Response.EventStream;
            ctx.Response.EventStream = WrapStream(source, original, _log, ctx.Ct);
            _log.LogDebug("stage {Name}: streaming wrapper installed (will rewrite message_start)", Name);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Replace the top-level <c>model</c> property in an Anthropic response
    /// body. Returns null on parse failure or when the field is missing —
    /// callers leave the original bytes untouched in that case.
    /// </summary>
    private static byte[]? TryRewriteBufferedBody(byte[] body, string newModel)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject root || !root.ContainsKey("model"))
            {
                return null;
            }
            root["model"] = newModel;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Async wrapper that rewrites the <c>message.model</c> field of the
    /// first <c>message_start</c> event. Subsequent events flow through
    /// unchanged.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<string>> WrapStream(
        IAsyncEnumerable<SseItem<string>> source,
        string newModel,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rewroteStart = false;
        await foreach (var evt in source.WithCancellation(ct))
        {
            if (!rewroteStart && evt.EventType == "message_start")
            {
                var rewritten = TryRewriteMessageStart(evt.Data, newModel);
                if (rewritten is not null)
                {
                    rewroteStart = true;
                    log.LogDebug("stage ResponseModelRewrite: message_start model → '{Original}'", newModel);
                    yield return new SseItem<string>(rewritten, evt.EventType);
                    continue;
                }
            }
            yield return evt;
        }
    }

    private static string? TryRewriteMessageStart(string data, string newModel)
    {
        try
        {
            var node = JsonNode.Parse(data);
            if (node is not JsonObject root) return null;
            if (root["message"] is not JsonObject messageObj || !messageObj.ContainsKey("model"))
            {
                return null;
            }
            messageObj["model"] = newModel;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
