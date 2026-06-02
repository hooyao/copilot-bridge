using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Response;

/// <summary>
/// Drops Copilot's OpenAI-style <c>event:message data:[DONE]</c> terminator
/// from the streaming response (research §8.6 / §15.2.4). The Anthropic SDK
/// JSON.parses each <c>data:</c> line and would throw on the literal string
/// <c>[DONE]</c>. Pushes the dropped event into
/// <see cref="BridgeContext{TBody}.DroppedEvents"/> so it still appears in
/// the inbound-resp audit with a <c>filtered:true</c> flag.
/// </summary>
internal sealed class DoneFilterStage : IResponseStage<MessagesRequest>
{
    private readonly ILogger<DoneFilterStage> _log;

    public DoneFilterStage(ILogger<DoneFilterStage> log)
    {
        _log = log;
    }

    public string Name => "DoneFilter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        if (ctx.Response.Mode != ResponseMode.Streaming || ctx.Response.EventStream is null)
        {
            return Task.CompletedTask;
        }

        var source = ctx.Response.EventStream;
        ctx.Response.EventStream = WrapAsync(source, ctx.DroppedEvents, _log, ctx.Ct);
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SseItem<string>> WrapAsync(
        IAsyncEnumerable<SseItem<string>> source,
        List<DroppedSseEvent> droppedEvents,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in source.WithCancellation(ct))
        {
            if (evt.EventType == "message" && evt.Data == "[DONE]")
            {
                droppedEvents.Add(new DroppedSseEvent(evt.EventType, evt.Data));
                log.LogDebug("stage DoneFilter: dropped event:message data:[DONE]");
                continue;
            }
            yield return evt;
        }
    }
}
