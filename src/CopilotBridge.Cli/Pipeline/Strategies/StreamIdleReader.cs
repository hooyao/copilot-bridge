using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>
/// Advances an SSE enumerator with a per-event <b>inactivity</b> bound, shared by
/// the <c>/cc</c> passthrough and Codex translation streaming loops.
/// </summary>
/// <remarks>
/// <para>The idle deadline is a separate <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// raced against the pending read via <see cref="Task.WhenAny(Task[])"/> — NOT a
/// <c>CancelAfter</c> armed/disarmed on the enumerator's own token. That distinction
/// is the whole point: an arm/disarm timer on a reused CTS has a nanosecond race
/// (if it fires between a successful move and the disarm, it permanently cancels the
/// source and the next read spuriously reports an idle timeout). Racing an
/// independent delay has no such window — the timer can never poison the source.</para>
/// <para>A move that completes synchronously (the next event is already buffered in
/// the parser — the common case for a healthy stream where one network read yields
/// several SSE events) takes a fast path: no <c>Task</c> allocation, no delay, no
/// race. Only a move that must actually wait on the network allocates the race
/// scaffolding, and that is precisely the moment where a few allocations are
/// negligible.</para>
/// <para>Cancellation semantics: a client cancel (<paramref name="ct"/>) always
/// wins and propagates as <see cref="OperationCanceledException"/>; only a genuine
/// idle deadline (with <paramref name="ct"/> NOT cancelled) throws
/// <see cref="UpstreamTimeoutException"/>. On an idle timeout the pending read is
/// cancelled via <paramref name="readCts"/> and awaited so it never dangles past
/// the throw (no unobserved exception when the stream is later disposed).</para>
/// </remarks>
internal static class StreamIdleReader
{
    /// <summary>
    /// Returns true with the next event available on <paramref name="e"/>, false at
    /// end of stream. Throws <see cref="UpstreamTimeoutException"/> on an idle
    /// timeout, or <see cref="OperationCanceledException"/> on a client cancel.
    /// </summary>
    /// <param name="e">The enumerator, whose token is sourced from <paramref name="readCts"/>.</param>
    /// <param name="readCts">The linked CTS backing the enumerator's read token; cancelled here to end a pending read on an idle timeout.</param>
    /// <param name="idle">The inactivity budget for the next event.</param>
    /// <param name="ct">The caller (client) token; its cancellation wins over the idle deadline.</param>
    public static async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<SseItem<string>> e,
        CancellationTokenSource readCts,
        TimeSpan idle,
        CancellationToken ct)
    {
        var move = e.MoveNextAsync();
        if (move.IsCompleted)
        {
            // Fast path: the event was already buffered (or the stream ended). No
            // timer, no Task allocation. Covers the bulk of a healthy stream.
            return await move;
        }

        var moveTask = move.AsTask();
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(moveTask, Task.Delay(idle, delayCts.Token));

        if (completed != moveTask && !ct.IsCancellationRequested)
        {
            // The idle deadline elapsed and it is not a client cancel: end the still
            // pending read (so it doesn't outlive this throw), observe its
            // cancellation, then surface the timeout.
            readCts.Cancel();
            try { await moveTask; }
            catch { /* expected: the read we just cancelled */ }
            throw new UpstreamTimeoutException(UpstreamTimeoutPhase.StreamIdle, idle);
        }

        // The move won (or the client cancelled): stop the timer task and return the
        // move's outcome. A client cancel makes `await moveTask` throw OCE, which
        // propagates as the client cancel.
        delayCts.Cancel();
        return await moveTask;
    }
}
