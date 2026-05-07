using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Response;

/// <summary>
/// Drops Copilot's OpenAI-style <c>event:message data:[DONE]</c> terminator
/// from the streaming response (research §8.6 / §15.2.4). The Anthropic SDK
/// JSON.parses each <c>data:</c> line and would throw on the literal string
/// <c>[DONE]</c>. Captures the dropped event into the audit log with
/// <c>filtered:true</c> so it's still visible during debugging.
/// </summary>
internal sealed class DoneFilterStage : IResponseStage<MessagesRequest>
{
    public string Name => "DoneFilter";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        if (ctx.Response.Mode != ResponseMode.Streaming || ctx.Response.EventStream is null)
        {
            return Task.CompletedTask;
        }

        var source = ctx.Response.EventStream;
        ctx.Response.EventStream = WrapAsync(source, ctx.Log, ctx.Ct);
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SseItem<string>> WrapAsync(
        IAsyncEnumerable<SseItem<string>> source,
        BridgeRequestLog log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in source.WithCancellation(ct))
        {
            if (evt.EventType == "message" && evt.Data == "[DONE]")
            {
                log.Events.Add(new SseEventCapture(evt.EventType, evt.Data, Filtered: true));
                DiagTracer.Log("stage DoneFilter: dropped event:message data:[DONE]");
                continue;
            }
            yield return evt;
        }
    }
}
