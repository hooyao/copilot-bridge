using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// What a detector wants the framework to do with an event / body. Detectors
/// never touch the stream themselves — they return an intent and
/// <see cref="ResponseInspectionStage"/> renders it. This lets several action
/// kinds share one traversal (see docs: design.md D7).
/// </summary>
internal enum DetectionActionKind
{
    /// <summary>No opinion — pass the event through unchanged.</summary>
    None = 0,

    /// <summary>Swallow this event (do not forward). Used by the [DONE] filter.</summary>
    DropEvent,

    /// <summary>Replace this event's payload with <see cref="DetectionAction.Event"/>.</summary>
    RewriteEvent,

    /// <summary>
    /// Terminate: inject the error carried by <see cref="DetectionAction.ErrorJson"/>
    /// (stream: as an SSE <c>error</c> event then end; buffer: as the body with
    /// <see cref="DetectionAction.HttpStatus"/>). Used by the tool-leak guard.
    /// </summary>
    Abort,
}

/// <summary>A detector's intent for the current event/body. Value type; cheap.</summary>
internal readonly struct DetectionAction
{
    public DetectionActionKind Kind { get; private init; }

    /// <summary>Replacement event for <see cref="DetectionActionKind.RewriteEvent"/>.</summary>
    public SseItem<string> Event { get; private init; }

    /// <summary>Anthropic error JSON for <see cref="DetectionActionKind.Abort"/>.</summary>
    public string? ErrorJson { get; private init; }

    /// <summary>HTTP status for a buffered <see cref="DetectionActionKind.Abort"/>.</summary>
    public int HttpStatus { get; private init; }

    public static readonly DetectionAction None = new() { Kind = DetectionActionKind.None };

    public static DetectionAction Drop() => new() { Kind = DetectionActionKind.DropEvent };

    public static DetectionAction Rewrite(SseItem<string> replacement) =>
        new() { Kind = DetectionActionKind.RewriteEvent, Event = replacement };

    public static DetectionAction Abort(string errorJson, int httpStatus) =>
        new() { Kind = DetectionActionKind.Abort, ErrorJson = errorJson, HttpStatus = httpStatus };
}

/// <summary>
/// One inspection concern behind <see cref="ResponseInspectionStage"/>. Detectors
/// are instantiated <b>per request</b> (never singletons) because a streaming
/// detector may carry cross-delta state (e.g. the tool-leak automaton) that MUST
/// NOT be shared across requests.
/// </summary>
/// <remarks>
/// Anthropic streams content blocks contiguously (<c>content_block_start</c> →
/// deltas → <c>content_block_stop</c>, no interleaving), so a detector tracks its
/// own current-block state from event ordering; the framework does not maintain a
/// block map. A detector exposes a streaming entry (<see cref="InspectEvent"/>)
/// and an optional buffered entry (<see cref="InspectBuffered"/>); the stage
/// calls the one matching the response mode.
/// </remarks>
internal interface IResponseDetector
{
    string Name { get; }

    /// <summary>
    /// When true, this detector requires the whole streaming response to be
    /// buffered before delivery so it can emit a real HTTP status on a dirty
    /// response (rather than a mid-stream SSE error). The stage drains the stream,
    /// reconstructs it, runs <see cref="InspectEvent"/> over the buffered events,
    /// and delivers a real status/body. Default false (stream-preserving).
    /// </summary>
    bool RequiresBuffering => false;

    /// <summary>
    /// Streaming: inspect one fully-framed SSE event and return an action. Return
    /// <see cref="DetectionAction.None"/> to pass through. The event is already
    /// SSE-framed by <c>SseParser</c>, so multi-byte / multi-line data arrives
    /// whole; text that spans multiple <c>content_block_delta</c> events is the
    /// detector's own concern to accumulate (the tool-leak automaton does this
    /// character-by-character).
    /// </summary>
    DetectionAction InspectEvent(in SseItem<string> evt);

    /// <summary>
    /// Buffered: inspect the whole response body once. Return an
    /// <see cref="DetectionActionKind.Abort"/>, a
    /// <see cref="DetectionActionKind.RewriteEvent"/> whose <c>Event.Data</c>
    /// carries the replacement body, or None. Default no-op for streaming-only
    /// detectors.
    /// </summary>
    DetectionAction InspectBuffered(byte[] body) => DetectionAction.None;
}
