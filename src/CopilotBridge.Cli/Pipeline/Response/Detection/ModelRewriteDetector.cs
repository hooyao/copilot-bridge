using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Restores the client-requested model id in the response so downstream
/// accounting (ccusage, the Claude Code session jsonl) reports the model the user
/// asked for, not the back-end variant the bridge routed to. Migrated verbatim
/// from the former <c>ResponseModelRewriteStage</c> into the detector framework:
/// streaming rewrites the first <c>message_start</c> event's <c>message.model</c>
/// (a <c>RewriteEvent</c> action); buffered rewrites the body's top-level
/// <c>model</c> (via <see cref="InspectBuffered"/>).
/// </summary>
/// <remarks>
/// Per-request instance carrying the original/resolved model ids and a
/// once-flag. Inert (returns None) when disabled, when the original is null
/// (router never ran), or when original == resolved (no rewrite happened). Errors
/// (non-JSON, missing model field) pass through untouched — never block a real
/// response on a rewrite failure.
/// </remarks>
internal sealed class ModelRewriteDetector : IResponseDetector
{
    private readonly string? _original;
    private readonly string _resolved;
    private readonly bool _active;
    private bool _rewroteStart;

    public ModelRewriteDetector(bool enabled, string? originalModel, string resolvedModel)
    {
        _original = originalModel;
        _resolved = resolvedModel;
        _active = enabled
            && !string.IsNullOrEmpty(originalModel)
            && !string.Equals(originalModel, resolvedModel, StringComparison.Ordinal);
    }

    public string Name => "ModelRewrite";

    public DetectionAction InspectEvent(in SseItem<string> evt)
    {
        if (!_active || _rewroteStart || evt.EventType != "message_start")
        {
            return DetectionAction.None;
        }

        var rewritten = TryRewriteMessageStart(evt.Data, _original!);
        if (rewritten is null)
        {
            return DetectionAction.None;
        }
        _rewroteStart = true;
        return DetectionAction.Rewrite(new SseItem<string>(rewritten, evt.EventType));
    }

    public DetectionAction InspectBuffered(byte[] body)
    {
        if (!_active || body.Length == 0)
        {
            return DetectionAction.None;
        }
        var rewritten = TryRewriteBufferedBody(body, _original!);
        if (rewritten is null)
        {
            return DetectionAction.None;
        }
        // Carry the replacement bytes as UTF-8 text on the event's Data slot; the
        // stage writes them back to BufferedBody.
        return DetectionAction.Rewrite(new SseItem<string>(Encoding.UTF8.GetString(rewritten), "buffered"));
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
}
