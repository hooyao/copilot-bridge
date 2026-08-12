using System.Diagnostics;
using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>What a <see cref="StreamIdleReader.MoveNextAsync"/> call produced.</summary>
internal enum StreamReadOutcome
{
    /// <summary>An upstream event is available on the enumerator's <c>Current</c>.</summary>
    Event = 0,

    /// <summary>The upstream stream ended.</summary>
    EndOfStream = 1,

    /// <summary>
    /// The keepalive interval elapsed with upstream still silent. The caller SHOULD
    /// emit one downstream keepalive and call back in; the upstream read is still
    /// pending and is resumed by the next call.
    /// </summary>
    KeepAliveDue = 2,
}

/// <summary>
/// Advances an SSE enumerator with a per-event <b>inactivity</b> bound, shared by
/// the <c>/cc</c> passthrough and Codex translation streaming loops, and reports
/// when a downstream keepalive is due for either client protocol because upstream
/// has gone quiet.
/// </summary>
/// <remarks>
/// <para>The idle deadline is a separate <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// raced against the pending read via <see cref="Task.WhenAny(Task[])"/> — NOT a
/// <c>CancelAfter</c> armed/disarmed on the enumerator's own token. That distinction
/// is the whole point: an arm/disarm timer on a reused CTS has a nanosecond race
/// (if it fires between a successful move and the disarm, it permanently cancels the
/// source and the next read spuriously reports an idle timeout). Racing an
/// independent delay has no such window — the timer can never poison the source.</para>
/// <para><b>Two deadlines over one read.</b> The idle budget is measured from the
/// last <b>upstream</b> event; the keepalive interval from the last <b>downstream</b>
/// activity (an upstream event or a keepalive this reader already reported). Each
/// call races the single pending read against whichever deadline is nearer. When the
/// keepalive deadline wins, the read is left <b>pending and untouched</b> and the
/// idle deadline is <b>not</b> recomputed — so no number of keepalives can postpone
/// the idle timeout. That is structural, not a convention to be remembered: a
/// keepalive is an event the bridge SENT downstream, never one it RECEIVED upstream,
/// and keepalives exist precisely to stop the CLIENT from judging upstream silence.
/// If they also fed this budget, nothing would be judging it and a hung upstream
/// would stream keepalives forever.</para>
/// <para>A move that completes synchronously (the next event is already buffered in
/// the parser — the common case for a healthy stream where one network read yields
/// several SSE events) takes a fast path: no <c>Task</c> allocation, no delay, no
/// race. Only a move that must actually wait on the network allocates the race
/// scaffolding, and that is precisely the moment where a few allocations are
/// negligible.</para>
/// <para>Cancellation semantics: a client cancel always wins and propagates as
/// <see cref="OperationCanceledException"/>; only a genuine idle deadline (with the
/// caller's token NOT cancelled) throws <see cref="UpstreamTimeoutException"/>. On an
/// idle timeout the pending read is cancelled via the read CTS and awaited so it
/// never dangles past the throw (no unobserved exception when the stream is later
/// disposed).</para>
/// <para>Instances are per-stream and single-threaded: the relay loop is the only
/// caller, and it carries the pending read between calls. Do not share one across
/// requests.</para>
/// </remarks>
internal sealed class StreamIdleReader
{
    private readonly IAsyncEnumerator<SseItem<string>> _e;
    private readonly CancellationTokenSource _readCts;
    private readonly TimeSpan _idle;
    private readonly TimeSpan _keepAlive;

    /// <summary>The read carried across a <see cref="StreamReadOutcome.KeepAliveDue"/> return; null when no read is in flight.</summary>
    private Task<bool>? _pending;

    /// <summary>Timestamp of the last UPSTREAM event — the idle budget's origin.</summary>
    private long _lastUpstreamTs;

    /// <summary>Timestamp of the last DOWNSTREAM activity (upstream event or reported keepalive) — the keepalive interval's origin.</summary>
    private long _lastDownstreamTs;

    /// <param name="e">The enumerator, whose token is sourced from <paramref name="readCts"/>.</param>
    /// <param name="readCts">The linked CTS backing the enumerator's read token; cancelled here to end a pending read on an idle timeout.</param>
    /// <param name="idle">The inactivity budget between upstream events; <see cref="TimeSpan.Zero"/> or less means unbounded.</param>
    /// <param name="keepAlive">The downstream keepalive interval; <see cref="TimeSpan.Zero"/> or less means never report <see cref="StreamReadOutcome.KeepAliveDue"/>.</param>
    public StreamIdleReader(
        IAsyncEnumerator<SseItem<string>> e,
        CancellationTokenSource readCts,
        TimeSpan idle,
        TimeSpan keepAlive)
    {
        _e = e;
        _readCts = readCts;
        _idle = idle;
        _keepAlive = keepAlive;
        _lastUpstreamTs = _lastDownstreamTs = Stopwatch.GetTimestamp();
    }

    private bool IdleEnabled => _idle > TimeSpan.Zero;

    private bool KeepAliveEnabled => _keepAlive > TimeSpan.Zero;

    /// <summary>
    /// Advances the stream, or reports that a downstream keepalive is due. Throws
    /// <see cref="UpstreamTimeoutException"/> on an idle timeout, or
    /// <see cref="OperationCanceledException"/> on a client cancel.
    /// </summary>
    /// <param name="ct">The caller (client) token; its cancellation wins over either deadline.</param>
    public async ValueTask<StreamReadOutcome> MoveNextAsync(CancellationToken ct)
    {
        if (_pending is null)
        {
            var move = _e.MoveNextAsync();
            if (move.IsCompleted)
            {
                // Fast path: the event was already buffered (or the stream ended). No
                // timer, no Task allocation. Covers the bulk of a healthy stream.
                return Arrive(await move);
            }

            _pending = move.AsTask();
        }
        else if (_pending.IsCompleted)
        {
            // The carried read landed while the caller was emitting a keepalive.
            var carried = _pending;
            _pending = null;
            return Arrive(await carried);
        }

        while (true)
        {
            if (!IdleEnabled && !KeepAliveEnabled)
            {
                // Neither deadline armed: no race, no timer — behave exactly like a bare
                // MoveNextAsync. Callers normally bypass this reader entirely in that
                // configuration; this keeps the class correct if one does not.
                var bare = _pending;
                _pending = null;
                return Arrive(await bare);
            }

            // Race the ONE pending read against whichever deadline is nearer. Both are
            // absolute, so looping here cannot postpone either. Both are also measured
            // from a SINGLE clock reading: sampling twice would let the second-computed
            // deadline look nanoseconds nearer purely because it was read later, which
            // is how an interval EQUAL to the budget could wrongly win the race.
            var now = Stopwatch.GetTimestamp();
            var wait = TimeSpan.MaxValue;
            var waitingForKeepAlive = false;
            if (IdleEnabled)
            {
                wait = _idle - Stopwatch.GetElapsedTime(_lastUpstreamTs, now);
            }
            if (KeepAliveEnabled)
            {
                var untilKeepAlive = _keepAlive - Stopwatch.GetElapsedTime(_lastDownstreamTs, now);
                // STRICTLY nearer: on a tie the idle budget wins. A keepalive interval
                // >= the budget must never displace it — the budget is the only thing
                // that ends a stalled turn, so it never loses a coin flip.
                if (untilKeepAlive < wait)
                {
                    wait = untilKeepAlive;
                    waitingForKeepAlive = true;
                }
            }
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completed = await Task.WhenAny(_pending, Task.Delay(wait, delayCts.Token));

            if (completed == _pending || ct.IsCancellationRequested)
            {
                // The move won (or the client cancelled): stop the timer task and return
                // the move's outcome. A client cancel makes the await throw OCE, which
                // propagates as the client cancel.
                delayCts.Cancel();
                var moveTask = _pending;
                _pending = null;
                return Arrive(await moveTask);
            }

            if (waitingForKeepAlive)
            {
                // Upstream is quiet but not yet over budget. Report the tick and leave the
                // read PENDING — deliberately not touching _lastUpstreamTs, so the idle
                // deadline keeps counting from the last real upstream event. Stamped from
                // "now" rather than advanced by one interval so a late loop cannot emit a
                // burst of catch-up keepalives.
                _lastDownstreamTs = Stopwatch.GetTimestamp();
                return StreamReadOutcome.KeepAliveDue;
            }

            // The idle deadline elapsed and it is not a client cancel: end the still
            // pending read (so it doesn't outlive this throw), observe its
            // cancellation, then surface the timeout.
            _readCts.Cancel();
            try { await _pending; }
            catch { /* expected: the read we just cancelled */ }
            _pending = null;
            throw new UpstreamTimeoutException(UpstreamTimeoutPhase.StreamIdle, _idle);
        }
    }

    /// <summary>Records a genuine upstream arrival — the only thing that resets the idle budget.</summary>
    private StreamReadOutcome Arrive(bool moved)
    {
        _lastUpstreamTs = _lastDownstreamTs = Stopwatch.GetTimestamp();
        return moved ? StreamReadOutcome.Event : StreamReadOutcome.EndOfStream;
    }
}
