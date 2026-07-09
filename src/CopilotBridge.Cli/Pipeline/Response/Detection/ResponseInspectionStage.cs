using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// The single response stage that runs the detector framework: one SSE parse,
/// fan out to an ordered set of per-request <see cref="IResponseDetector"/>s, and
/// render whatever action they return. Replaces the former one-off
/// <c>DoneFilterStage</c> / <c>ResponseModelRewriteStage</c> (now detectors) so
/// the response stream is wrapped once, not once per concern.
/// </summary>
/// <remarks>
/// Scoped DI service. Detectors are injected as the whole set
/// <c>IEnumerable&lt;IResponseDetector&gt;</c> (each a scoped service, so streaming
/// state never leaks across requests); DI registration order is precedence order.
/// For each request the stage selects the config-enabled detectors
/// (<see cref="IResponseDetector.Enabled"/>) and initializes each from the
/// populated context (<see cref="IResponseDetector.Begin"/>) before any inspection.
/// The action semantics on the streaming path (design.md D7):
/// <list type="bullet">
/// <item>None → yield the event unchanged;</item>
/// <item>DropEvent → do not yield, record in <c>DroppedEvents</c>;</item>
/// <item>RewriteEvent → yield the replacement event;</item>
/// <item>Abort → yield one synthetic <c>error</c> event, then end the stream.</item>
/// </list>
/// The first non-None action for an event wins (detector order = registration
/// order); Abort short-circuits the remaining detectors.
/// </remarks>
internal sealed class ResponseInspectionStage : IResponseStage<MessagesRequest>
{
    private readonly IReadOnlyList<IResponseDetector> _detectors;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<ResponseInspectionStage> _log;

    public ResponseInspectionStage(
        IEnumerable<IResponseDetector> detectors,
        BridgeContext<MessagesRequest> ctx,
        ILogger<ResponseInspectionStage> log)
    {
        // Order by the explicit registration-assigned Order, not the container's
        // enumeration order — precedence is guaranteed regardless of how
        // IEnumerable<IResponseDetector> happens to resolve.
        _detectors = detectors.OrderBy(d => d.Order).ToArray();
        _ctx = ctx;
        _log = log;
    }

    public string Name => "ResponseInspection";

    public async Task ApplyAsync()
    {
        var ctx = _ctx;
        // Select the config-enabled detectors and initialize each from the (now
        // fully-populated) context, preserving Order = precedence. A disabled
        // detector is never begun and never inspects — no scanning, no allocation.
        // The always-on DONE filter keeps the active set non-empty.
        List<IResponseDetector>? active = null;
        foreach (var d in _detectors)
        {
            if (!d.Enabled)
            {
                continue;
            }
            d.Begin();
            (active ??= new List<IResponseDetector>(_detectors.Count)).Add(d);
        }
        if (active is null)
        {
            return; // defensive: the always-on DONE filter keeps this non-empty
        }
        var detectors = active;

        if (ctx.Response.Mode == ResponseMode.Streaming && ctx.Response.EventStream is not null)
        {
            var source = ctx.Response.EventStream;
            if (RequiresBuffering(detectors))
            {
                // Buffer-and-decide EAGERLY: drain the stream now, before the
                // stage returns, so Mode/Status/BufferedBody are settled before
                // the endpoint reads Mode to pick its delivery branch. This is
                // what makes a real HTTP status possible (PreserveStream=false).
                await BufferThenDeliverAsync(source, detectors, ctx, ctx.Ct);
            }
            else if (BuffersScannableBlocks(detectors))
            {
                // Per-block withhold-then-relay: keep a real HTTP 200 stream, but
                // hold each scannable block's events until the block completes so a
                // leak detected mid-block is suppressed before any of its bytes are
                // written. Non-scannable blocks still stream live.
                ctx.Response.EventStream = WrapStreamBufferingScannableBlocks(source, detectors, ctx, _log, ctx.Ct);
            }
            else
            {
                ctx.Response.EventStream = WrapStream(source, detectors, ctx, _log, ctx.Ct);
            }
        }
        else if (ctx.Response.Mode == ResponseMode.Buffered && ctx.Response.BufferedBody is { Length: > 0 } body)
        {
            ApplyBuffered(detectors, ctx, body);
        }
    }

    private static bool RequiresBuffering(IReadOnlyList<IResponseDetector> detectors)
    {
        foreach (var d in detectors)
        {
            if (d.RequiresBuffering) return true;
        }
        return false;
    }

    private static bool BuffersScannableBlocks(IReadOnlyList<IResponseDetector> detectors)
    {
        foreach (var d in detectors)
        {
            if (d.BuffersScannableBlocks) return true;
        }
        return false;
    }

    /// <summary>
    /// Eagerly buffer the whole upstream stream, then decide: on an Abort action
    /// flip the response to Buffered with a real HTTP status + error body (the
    /// endpoint then writes it as a normal body); otherwise leave Mode=Streaming
    /// and replace the stream with a replay of the collected (possibly rewritten)
    /// events. Runs BEFORE the endpoint writes any byte, so the status is still
    /// mutable — the whole point of <c>PreserveStream=false</c>.
    /// </summary>
    private async Task BufferThenDeliverAsync(
        IAsyncEnumerable<SseItem<string>> source,
        IReadOnlyList<IResponseDetector> detectors,
        BridgeContext<MessagesRequest> ctx,
        CancellationToken ct)
    {
        var buffered = new List<SseItem<string>>();

        await foreach (var evt in source.WithCancellation(ct))
        {
            var action = DetectionAction.None;
            foreach (var d in detectors)
            {
                var a = d.InspectEvent(evt);
                if (a.Kind != DetectionActionKind.None)
                {
                    // First non-None action wins (detector order = precedence).
                    action = a;
                    break;
                }
            }

            switch (action.Kind)
            {
                case DetectionActionKind.Abort:
                    _log.LogDebug("stage {Name}: buffered abort; status={Status}", Name, action.HttpStatus);
                    ctx.Response.Mode = ResponseMode.Buffered;
                    ctx.Response.Status = action.HttpStatus;
                    ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(action.ErrorJson!);
                    ctx.Response.Headers["Content-Type"] = "application/json";
                    ctx.Response.EventStream = null;
                    // The tripping detector owns its own context flag (leak vs
                    // runaway) — the stage can't distinguish them from the action.
                    return; // terminal — discard the rest of the buffered events

                case DetectionActionKind.DropEvent:
                    ctx.DroppedEvents.Add(new DroppedSseEvent(evt.EventType, evt.Data));
                    break;

                case DetectionActionKind.RewriteEvent:
                    buffered.Add(action.Event);
                    break;

                default:
                    buffered.Add(evt);
                    break;
            }
        }

        // Clean: replace the (now-drained) upstream stream with a replay of the
        // collected events so the endpoint's streaming relay delivers them.
        ctx.Response.EventStream = Replay(buffered);
    }

    private static async IAsyncEnumerable<SseItem<string>> Replay(List<SseItem<string>> events)
    {
        foreach (var e in events)
        {
            yield return e;
        }
        await Task.CompletedTask;
    }

    private static void ApplyBuffered(
        IReadOnlyList<IResponseDetector> detectors,
        BridgeContext<MessagesRequest> ctx,
        byte[] body)
    {
        foreach (var d in detectors)
        {
            var action = d.InspectBuffered(body);
            switch (action.Kind)
            {
                case DetectionActionKind.Abort:
                    ctx.Response.Status = action.HttpStatus;
                    ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(action.ErrorJson!);
                    ctx.Response.Headers["Content-Type"] = "application/json";
                    // Flag owned by the tripping detector (leak vs runaway).
                    return; // terminal — no later detector runs
                case DetectionActionKind.RewriteEvent:
                    // Buffered rewrite carries replacement bytes as the event data.
                    body = Encoding.UTF8.GetBytes(action.Event.Data);
                    ctx.Response.BufferedBody = body;
                    break;
            }
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> WrapStream(
        IAsyncEnumerable<SseItem<string>> source,
        IReadOnlyList<IResponseDetector> detectors,
        BridgeContext<MessagesRequest> ctx,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in source.WithCancellation(ct))
        {
            var action = DetectionAction.None;
            foreach (var d in detectors)
            {
                var a = d.InspectEvent(evt);
                if (a.Kind != DetectionActionKind.None)
                {
                    // First non-None action wins (detector order = precedence).
                    action = a;
                    if (a.Kind == DetectionActionKind.Abort)
                    {
                        log.LogDebug("stage {Name}: detector {Detector} aborted the stream", "ResponseInspection", d.Name);
                    }
                    break;
                }
            }

            switch (action.Kind)
            {
                case DetectionActionKind.None:
                    yield return evt;
                    break;

                case DetectionActionKind.DropEvent:
                    ctx.DroppedEvents.Add(new DroppedSseEvent(evt.EventType, evt.Data));
                    break;

                case DetectionActionKind.RewriteEvent:
                    yield return action.Event;
                    break;

                case DetectionActionKind.Abort:
                    // Inject the error event, then end the stream — the remaining
                    // upstream events are abandoned. The tripping detector already
                    // set its own context flag (leak vs runaway). Claude Code treats
                    // the injected overloaded_error as retryable, but note: once
                    // visible output has already streamed, recent Claude Code
                    // versions may preserve the partial response and show an
                    // incomplete-response notice instead of retrying the whole turn.
                    yield return new SseItem<string>(action.ErrorJson!, "error");
                    yield break;
            }
        }
    }

    /// <summary>
    /// Stream-preserving delivery with per-block withholding (opt-in via
    /// <see cref="ResponseLeakGuardOptions.BufferScannableBlocks"/>). Keeps a real
    /// HTTP 200 stream, but holds each <b>scannable</b> block's events from
    /// <c>content_block_start</c> until <c>content_block_stop</c> — feeding EVERY event
    /// to detectors live (detection is unchanged; only OUTPUT is delayed) — and relays
    /// the block only if no detector aborted during it. So a leak detected mid-block is
    /// suppressed BEFORE any of its bytes reach the client, closing the gap where
    /// <see cref="WrapStream"/> can only inject an error after the leaked bytes have
    /// already streamed. Non-scannable blocks (<c>tool_use</c>) and all message-level
    /// framing stream live, preserving time-to-first-token for content that cannot leak.
    /// </summary>
    /// <remarks>
    /// A scannable block is a <c>text</c> or <c>thinking</c> block. <c>thinking</c> is
    /// withheld regardless of <c>ScanThinking</c>: withholding a block the guard will not
    /// scan only adds latency (no correctness change — it flushes clean at block end),
    /// and keeping the stage free of per-detector scan-config coupling is worth that minor
    /// cost in this opt-in mode. The withheld-block delivery necessarily batches a block's
    /// text at block end rather than streaming it token-by-token — the inherent trade-off
    /// of airtight suppression (you cannot show text before verifying it is clean).
    /// </remarks>
    private static async IAsyncEnumerable<SseItem<string>> WrapStreamBufferingScannableBlocks(
        IAsyncEnumerable<SseItem<string>> source,
        IReadOnlyList<IResponseDetector> detectors,
        BridgeContext<MessagesRequest> ctx,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Events of the current withheld block, in arrival order, flushed at
        // content_block_stop iff the block stayed clean. Reused across blocks.
        var blockBuffer = new List<SseItem<string>>();
        var withholding = false;

        await foreach (var evt in source.WithCancellation(ct))
        {
            var action = DetectionAction.None;
            foreach (var d in detectors)
            {
                var a = d.InspectEvent(evt);
                if (a.Kind != DetectionActionKind.None)
                {
                    // First non-None action wins (detector order = precedence).
                    action = a;
                    if (a.Kind == DetectionActionKind.Abort)
                    {
                        log.LogDebug("stage {Name}: detector {Detector} aborted the stream (block-buffered)", "ResponseInspection", d.Name);
                    }
                    break;
                }
            }

            if (action.Kind == DetectionActionKind.Abort)
            {
                // Suppress: discard any withheld block (its bytes never reach the
                // client — the whole point of this mode), inject the error, end.
                blockBuffer.Clear();
                yield return new SseItem<string>(action.ErrorJson!, "error");
                yield break;
            }

            // The event this action renders: the original (None), the replacement
            // (Rewrite), or nothing (Drop — recorded for the audit).
            SseItem<string>? effective = action.Kind switch
            {
                DetectionActionKind.DropEvent => null,
                DetectionActionKind.RewriteEvent => action.Event,
                _ => evt,
            };
            if (action.Kind == DetectionActionKind.DropEvent)
            {
                ctx.DroppedEvents.Add(new DroppedSseEvent(evt.EventType, evt.Data));
            }

            if (evt.EventType == "content_block_start")
            {
                // Blocks are contiguous; a prior withheld block should have flushed at
                // its stop. Flush defensively if not, then decide this block.
                if (withholding)
                {
                    foreach (var b in blockBuffer) yield return b;
                    blockBuffer.Clear();
                    withholding = false;
                }

                if (IsScannableBlock(evt.Data))
                {
                    withholding = true;
                    if (effective is { } startEvt) blockBuffer.Add(startEvt);
                }
                else if (effective is { } liveStart)
                {
                    yield return liveStart; // non-scannable → stream live
                }
                continue;
            }

            if (withholding)
            {
                if (effective is { } e) blockBuffer.Add(e);
                if (evt.EventType == "content_block_stop")
                {
                    // Block stayed clean → relay it in full, in arrival order.
                    foreach (var b in blockBuffer) yield return b;
                    blockBuffer.Clear();
                    withholding = false;
                }
                continue;
            }

            if (effective is { } live)
            {
                yield return live; // outside any withheld block → stream live
            }
        }

        // The source completed GRACEFULLY while still withholding (a block whose
        // content_block_stop never arrived — e.g. a stream that ended early). No detector
        // aborted, so relay what was buffered: an unclosed envelope is not a leak under
        // the guard's closed-shape rule, and dropping presumed-clean content would diverge
        // from plain streaming. (A *throwing* upstream fault — TCP reset, cancellation —
        // bypasses this flush entirely; the withheld partial is discarded and the fault is
        // surfaced by the endpoint's relay loop, matching this mode's "no text before it is
        // verified clean" trade-off.)
        if (withholding)
        {
            foreach (var e in blockBuffer) yield return e;
            blockBuffer.Clear();
        }
    }

    /// <summary>True when a <c>content_block_start</c> begins a scannable block
    /// (<c>text</c> or <c>thinking</c>) whose deltas the block-buffering path withholds.
    /// A start whose data cannot be parsed is treated as non-scannable (streamed live) —
    /// failing toward lower latency, since an unparseable start is not a block we can scan
    /// anyway.</summary>
    private static bool IsScannableBlock(string startData)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(startData);
            if (!doc.RootElement.TryGetProperty("content_block", out var cb)
                || !cb.TryGetProperty("type", out var t)
                || t.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                // No string `type` → not a scannable block. The ValueKind gate also keeps
                // GetString() from throwing InvalidOperationException on a non-string
                // `type` (which would escape this JsonException-scoped catch and crash the
                // stream instead of streaming the block live).
                return false;
            }
            var type = t.GetString();
            return type == "text" || type == "thinking";
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
