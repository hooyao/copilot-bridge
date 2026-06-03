using System.Buffers;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Payload shipped from an endpoint to <see cref="BridgeIoSink"/> via a single
/// <c>ILogger.Log</c> call. Holds the bytes the worker needs to render the
/// audit JSON for one of the four artifacts (inbound-req / inbound-resp /
/// upstream-req / upstream-resp).
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffer ownership.</b> <see cref="Body"/> is either rented from
/// <see cref="ArrayPool{T}.Shared"/> (when <see cref="BodyPooled"/> is true)
/// or directly owned by the payload. Either way, the sink worker is the
/// last consumer and returns rented buffers via <see cref="Release"/> after
/// it has finished serializing. Callers must not touch the array after
/// handing the payload to the logger.
/// </para>
/// <para>
/// <see cref="BodyLength"/> is the meaningful prefix of <see cref="Body"/>;
/// pooled arrays are typically larger than the rented size.
/// </para>
/// </remarks>
internal sealed class BridgeIoPayload
{
    /// <summary>Monotonic per-process sequence (shared with the inbound endpoint's audit seq).</summary>
    public required int Seq { get; init; }

    /// <summary>
    /// Stable per-request identifier built once at the inbound endpoint as
    /// <c>{yyyyMMdd-HHmmss}-{seq:D4}</c>. All four audit artifacts of one
    /// request carry the SAME value, and the per-request INFO summary line
    /// embeds the same string as <c>req#{TraceId}</c>. Operators grep the
    /// log for the trace id, then <c>ls *{TraceId}*</c> in the trace
    /// directory to pull the matching JSON files.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// UTC timestamp written to this specific audit artifact's JSON body —
    /// each of the four audits captures its own wall-clock moment so the
    /// operator can see how long the upstream call took, when the SSE
    /// stream finished, etc. Unrelated to file naming (see <see cref="TraceId"/>).
    /// </summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>One of: <c>inbound-req</c>, <c>inbound-resp</c>, <c>upstream-req</c>, <c>upstream-resp</c>.</summary>
    public required string Kind { get; init; }

    // HTTP shape — exactly one of {Method+Path/Url} and {Status} is populated
    // depending on whether this is a request or response artifact.

    /// <summary>Set for request artifacts; null for response artifacts.</summary>
    public string? Method { get; init; }

    /// <summary>Set for inbound-req artifacts (path) and upstream-req artifacts (absolute URL).</summary>
    public string? Target { get; init; }

    /// <summary>Set for response artifacts; null for request artifacts.</summary>
    public int? Status { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Body bytes; the meaningful prefix is <see cref="BodyLength"/>. May be empty.</summary>
    public required byte[] Body { get; init; }

    public required int BodyLength { get; init; }

    /// <summary>True if <see cref="Body"/> came from <see cref="ArrayPool{T}"/> and must be returned.</summary>
    public required bool BodyPooled { get; init; }

    /// <summary>SSE events captured on the inbound-resp side; null for other kinds or when not streaming.</summary>
    public IReadOnlyList<CapturedSseEvent>? Events { get; init; }

    /// <summary>Optional error string surfaced for either side.</summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock duration in milliseconds for the end-to-end inbound request (inbound-resp only).</summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Return the pooled body buffer (if any) to the shared pool. Safe to
    /// call multiple times — second and subsequent calls are no-ops.
    /// </summary>
    public void Release()
    {
        if (BodyPooled && Body.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(Body, clearArray: false);
        }
    }
}

/// <summary>One SSE event captured on the response side, with a flag for whether the bridge dropped it.</summary>
internal sealed record CapturedSseEvent(string? EventType, string Data, bool Filtered);
