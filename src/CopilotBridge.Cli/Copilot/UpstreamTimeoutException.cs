namespace CopilotBridge.Cli.Copilot;

/// <summary>Which inactivity budget fired — see <see cref="UpstreamTimeoutException"/>.</summary>
internal enum UpstreamTimeoutPhase
{
    /// <summary>No response headers within the first-byte budget (pre-headers).</summary>
    FirstByte,

    /// <summary>Gap between SSE events exceeded the stream-idle budget (headers already sent).</summary>
    StreamIdle,
}

/// <summary>
/// Thrown by the <c>/cc</c> forward path when a <see cref="UpstreamTimeoutPhase"/>
/// inactivity budget (see <c>Hosting.Options.UpstreamTimeoutOptions</c>) elapses
/// with no upstream progress. This is a bridge-initiated abort of an unresponsive
/// Copilot, NOT a client cancellation and NOT a network fault.
/// </summary>
/// <remarks>
/// <para>Deliberately a plain <see cref="Exception"/> — NOT an
/// <see cref="OperationCanceledException"/> (so it is never confused with a client
/// cancel) and NOT in the <see cref="TransientUpstreamError"/> family (so the
/// client's transient-retry loop does not re-send it and the endpoint does not
/// mislabel it a 502). The endpoint has one dedicated <c>catch</c> that maps it:
/// <see cref="UpstreamTimeoutPhase.FirstByte"/> → a real 504; a mid-stream
/// <see cref="UpstreamTimeoutPhase.StreamIdle"/> → a retryable error event or a
/// truncation, per config.</para>
/// <para>The distinguishing test at the throw site is: the linked timeout token
/// was cancelled AND the caller's own token was not — so a genuine client cancel
/// always wins the race and propagates as its original
/// <see cref="OperationCanceledException"/>.</para>
/// </remarks>
internal sealed class UpstreamTimeoutException : Exception
{
    public UpstreamTimeoutPhase Phase { get; }

    /// <summary>How long the phase was idle before the budget fired.</summary>
    public TimeSpan Elapsed { get; }

    public UpstreamTimeoutException(UpstreamTimeoutPhase phase, TimeSpan elapsed)
        : base($"upstream {PhaseLabel(phase)} timeout after {elapsed.TotalSeconds:0.#}s of inactivity")
    {
        Phase = phase;
        Elapsed = elapsed;
    }

    /// <summary>Grep-friendly phase token used in logs and the request summary.</summary>
    public static string PhaseLabel(UpstreamTimeoutPhase phase) => phase switch
    {
        UpstreamTimeoutPhase.FirstByte => "first_byte",
        _ => "stream_idle",
    };
}
