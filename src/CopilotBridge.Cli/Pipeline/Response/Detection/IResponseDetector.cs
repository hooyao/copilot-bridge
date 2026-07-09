using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Models.Anthropic.Request;

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
    /// <see cref="DetectionAction.HttpStatus"/>). Used by the response-leak guard.
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
/// are <b>scoped</b> DI services (one instance per request scope) because a
/// streaming detector may carry cross-delta state (e.g. the response-leak automaton)
/// that MUST NOT be shared across requests. They are injected as the set
/// <c>IEnumerable&lt;IResponseDetector&gt;</c> and the stage runs them in ascending
/// <see cref="Order"/> (assigned from registration order), so precedence does not
/// depend on the container's enumeration order.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure DI (a <see cref="DetectorOrder{TDetector}"/> + options
/// snapshots + loggers) and happens at the start of a request scope, before the
/// request body is populated. Per-request data (declared tools, model ids) and
/// streaming-state reset therefore live in <see cref="Begin"/>, which the stage
/// calls exactly once — after the context is fully populated and only for detectors
/// whose <see cref="Enabled"/> gate is on — before any <see cref="InspectEvent"/> /
/// <see cref="InspectBuffered"/> call.
/// </para>
/// <para>
/// Anthropic streams content blocks contiguously (<c>content_block_start</c> →
/// deltas → <c>content_block_stop</c>, no interleaving), so a detector tracks its
/// own current-block state from event ordering; the framework does not maintain a
/// block map. A detector exposes a streaming entry (<see cref="InspectEvent"/>)
/// and an optional buffered entry (<see cref="InspectBuffered"/>); the stage
/// calls the one matching the response mode.
/// </para>
/// </remarks>
internal interface IResponseDetector
{
    string Name { get; }

    /// <summary>
    /// Precedence within the detector set: the stage runs detectors in ascending
    /// <see cref="Order"/>, and the first non-passthrough action (by this order)
    /// wins. Assigned from registration order by
    /// <c>BridgeServiceCollectionExtensions.RegisterResponseDetector</c> (via an
    /// injected <see cref="DetectorOrder{TDetector}"/>), so execution order does not
    /// depend on the container's <c>IEnumerable&lt;T&gt;</c> resolution order.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Config gate. When false the stage neither <see cref="Begin"/>s nor runs
    /// this detector for the request — no scanning, no allocation. Backed by an
    /// <c>IOptionsSnapshot&lt;T&gt;</c> so it re-binds per request scope (a future
    /// <c>reloadOnChange:true</c> flip makes it live without touching detectors).
    /// The [DONE] filter is always on.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Per-request (re)initialization from the fully-populated context. Called
    /// once by the stage — after config gating, before any inspection — so the
    /// detector can read its request data (declared tools, model ids) from the
    /// injected <c>BridgeContext</c> and reset any streaming state. Decouples
    /// DI-construction timing (scope start, body empty) from request-data
    /// availability (response phase, body populated).
    /// </summary>
    void Begin();

    /// <summary>
    /// When true, this detector requires the whole streaming response to be
    /// buffered before delivery so it can emit a real HTTP status on a dirty
    /// response (rather than a mid-stream SSE error). The stage drains the stream,
    /// reconstructs it, runs <see cref="InspectEvent"/> over the buffered events,
    /// and delivers a real status/body. Default false (stream-preserving).
    /// </summary>
    bool RequiresBuffering => false;

    /// <summary>
    /// When true, the stage withholds each <b>scannable</b> content block's deltas
    /// (a <c>text</c> or <c>thinking</c> block) from <c>content_block_start</c> until
    /// <c>content_block_stop</c>, still feeding every event to detectors live, and
    /// relays the block only if no detector aborted during it — so a leak detected
    /// mid-block is suppressed BEFORE any of the block's bytes reach the client.
    /// Non-scannable blocks (e.g. <c>tool_use</c>) still stream live. This is a
    /// narrower, per-block cousin of <see cref="RequiresBuffering"/>: it keeps a real
    /// HTTP 200 stream (only scannable blocks pay latency, and only until each block
    /// ends) instead of buffering the whole response. Only meaningful on the
    /// stream-preserving path; a detector that also sets <see cref="RequiresBuffering"/>
    /// is already whole-response buffered and this is moot. Default false.
    /// </summary>
    bool BuffersScannableBlocks => false;

    /// <summary>
    /// Streaming: inspect one fully-framed SSE event and return an action. Return
    /// <see cref="DetectionAction.None"/> to pass through. The event is already
    /// SSE-framed by <c>SseParser</c>, so multi-byte / multi-line data arrives
    /// whole; text that spans multiple <c>content_block_delta</c> events is the
    /// detector's own concern to accumulate (the response-leak automaton does this
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
