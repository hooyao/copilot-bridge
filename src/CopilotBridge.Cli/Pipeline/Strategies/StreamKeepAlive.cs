using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Strategies;

/// <summary>
/// The downstream keepalive the bridge injects into an Anthropic or Responses stream
/// while upstream is silent, plus the means to recognize one again downstream.
/// </summary>
/// <remarks>
/// <para>Copilot sends <b>no</b> keepalive while a model is thinking — zero <c>ping</c>
/// events across 137 captured response traces, and a measured <c>claude-opus-5</c>
/// turn at <c>effort=xhigh</c> opened a thinking block then put nothing on the wire
/// for 600 s. The real Anthropic API pings through exactly that silence, so an
/// Anthropic client's idle watchdogs are calibrated for a stream that never goes
/// quiet. Injecting the keepalive the upstream omits is what lets the bridge's own
/// budgets be the only thing that ends a stalled turn.</para>
/// <para>Verified inert at the client, from the shipped <c>claude.exe</c> 2.1.220
/// bundle: the stream loop runs its watchdog reset BEFORE the event-type check and
/// then <c>continue</c>s on <c>type === "ping"</c>, so a ping re-arms both idle
/// watchdogs while touching no content block, no usage accumulation and no stall
/// statistics; the UI layer discards it outright. See <c>docs/timeout-chain.md</c>.</para>
/// <para>Verified inert in Codex 0.144.1 and current source: its 300 s default
/// <c>stream_idle_timeout</c> wraps each parsed <c>eventsource().next()</c>; a complete
/// <c>data: {"type":"ping"}</c> event completes that wait, then the Responses
/// dispatcher ignores the unknown type. A comment-only <c>: ping</c> is not equivalent:
/// the event-source parser discards comments and yields no event, so the timer keeps
/// running.</para>
/// <para>Lives here rather than on a strategy so the response edge can mark injected
/// events for the trace without depending on which strategy produced the stream.</para>
/// </remarks>
internal static class StreamKeepAlive
{
    /// <summary>
    /// The single payload instance every injected keepalive carries. Its <b>reference
    /// identity</b> is what <see cref="IsInjected"/> matches, so marking never has to
    /// sniff payload text.
    /// </summary>
    private const string PingData = "{\"type\":\"ping\"}";

    /// <summary>
    /// A synthetic <c>ping</c> data event valid for both downstream SSE consumers.
    /// Claude Code recognizes it explicitly; Codex ignores the unknown Responses type.
    /// </summary>
    public static SseItem<string> Ping() => new(PingData, "ping");

    /// <summary>
    /// True when this event is a keepalive THIS bridge invented, as opposed to one it
    /// relayed. Identity-based, so it cannot produce a false positive: a ping Copilot
    /// itself sent arrives as a freshly parsed string and stays unmarked, which is the
    /// honest answer. If a future response stage rewrote a ping's payload the mark
    /// would simply be lost (a false negative), never fabricated.
    /// </summary>
    public static bool IsInjected(SseItem<string> evt) => ReferenceEquals(evt.Data, PingData);
}
