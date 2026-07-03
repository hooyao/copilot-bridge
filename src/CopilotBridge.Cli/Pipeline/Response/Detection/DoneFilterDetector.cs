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
/// <see cref="BridgeContext{TBody}.DroppedEvents"/> for the audit. Always on and
/// stateless: no config gate (forwarding <c>[DONE]</c> crashes the SDK) and
/// nothing to (re)initialize per request — it inherits the base's empty
/// <c>Begin()</c>.
/// </remarks>
internal sealed class DoneFilterDetector : AbstractOrderAwareDetector<DoneFilterDetector>
{
    public DoneFilterDetector(DetectorOrder<DoneFilterDetector> order) : base(order) { }

    public override string Name => "DoneFilter";

    /// <summary>Always on — not user-configurable. Forwarding the <c>[DONE]</c>
    /// terminator crashes the Anthropic SDK, so this detector must always run.</summary>
    public override bool Enabled => true;

    public override DetectionAction InspectEvent(in SseItem<string> evt)
    {
        if (evt.EventType == "message" && evt.Data == "[DONE]")
        {
            return DetectionAction.Drop();
        }
        return DetectionAction.None;
    }
}
