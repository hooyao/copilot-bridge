using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// The single response stage that runs the detector framework: one SSE parse,
/// fan out to an ordered set of per-request <see cref="IResponseDetector"/>s, and
/// render whatever action they return. Replaces the former one-off
/// <c>DoneFilterStage</c> / <c>ResponseModelRewriteStage</c> (now detectors) so
/// the response stream is wrapped once, not once per concern.
/// </summary>
/// <remarks>
/// Detectors are built per request via <see cref="DetectorSetFactory"/> so
/// streaming state (e.g. the tool-leak automaton) never leaks across requests.
/// The action semantics on the streaming path (design.md D7):
/// <list type="bullet">
/// <item>None → yield the event unchanged;</item>
/// <item>DropEvent → do not yield, record in <c>DroppedEvents</c>;</item>
/// <item>RewriteEvent → yield the replacement event;</item>
/// <item>Abort → yield one synthetic <c>error</c> event, then end the stream.</item>
/// </list>
/// The first non-None action for an event wins (detector order = former stage
/// order); Abort short-circuits the remaining detectors.
/// </remarks>
internal sealed class ResponseInspectionStage : IResponseStage<MessagesRequest>
{
    private readonly DetectorSetFactory _factory;
    private readonly ILogger<ResponseInspectionStage> _log;

    public ResponseInspectionStage(DetectorSetFactory factory, ILogger<ResponseInspectionStage> log)
    {
        _factory = factory;
        _log = log;
    }

    public string Name => "ResponseInspection";

    public async Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var detectors = _factory.Build(ctx);
        if (detectors.Count == 0)
        {
            return; // nothing enabled → no allocation on the stream
        }

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
                    action = a;
                    if (a.Kind == DetectionActionKind.Abort) break;
                }
            }

            switch (action.Kind)
            {
                case DetectionActionKind.Abort:
                    _log.LogWarning("stage {Name}: buffered abort (tool leak); status={Status}", Name, action.HttpStatus);
                    ctx.Response.Mode = ResponseMode.Buffered;
                    ctx.Response.Status = action.HttpStatus;
                    ctx.Response.BufferedBody = Encoding.UTF8.GetBytes(action.ErrorJson!);
                    ctx.Response.Headers["Content-Type"] = "application/json";
                    ctx.Response.EventStream = null;
                    ctx.ToolLeakDetected = true;
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
                    ctx.ToolLeakDetected = true;
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
                    action = a;
                    if (a.Kind == DetectionActionKind.Abort)
                    {
                        log.LogWarning("stage {Name}: detector {Detector} aborted the stream", "ResponseInspection", d.Name);
                        break;
                    }
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
                    // upstream events are abandoned. Claude Code discards the whole
                    // attempt on this error and retries from clean history.
                    ctx.ToolLeakDetected = true;
                    yield return new SseItem<string>(action.ErrorJson!, "error");
                    yield break;
            }
        }
    }
}
