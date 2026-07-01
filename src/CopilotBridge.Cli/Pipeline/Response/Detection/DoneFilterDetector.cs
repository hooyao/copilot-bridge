using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Drops Copilot's OpenAI-style <c>event:message data:[DONE]</c> terminator from
/// the streaming response — the Anthropic SDK JSON.parses each <c>data:</c> line
/// and would throw on the literal <c>[DONE]</c>. Migrated verbatim from the former
/// <c>DoneFilterStage</c> into the detector framework as a <c>DropEvent</c> action.
/// </summary>
/// <remarks>
/// <c>[DONE]</c> is one SSE event's complete data, never split across events
/// (SSE frames on the blank line), so this is a stateless whole-event string
/// compare. The framework records the drop in
/// <see cref="BridgeContext{TBody}.DroppedEvents"/> for the audit.
/// </remarks>
internal sealed class DoneFilterDetector : IResponseDetector
{
    public string Name => "DoneFilter";

    public DetectionAction InspectEvent(in SseItem<string> evt)
    {
        if (evt.EventType == "message" && evt.Data == "[DONE]")
        {
            return DetectionAction.Drop();
        }
        return DetectionAction.None;
    }
}
