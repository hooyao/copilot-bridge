using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// EventIds that mark a log event as a bridge IO artifact (vs ordinary
/// diagnostic logging). <see cref="BridgeIoSink"/> filters on these to
/// decide which events to write to disk; everything else is ignored and
/// flows through the normal sinks (console, rolling file).
/// </summary>
internal static class BridgeIoEvents
{
    public static readonly EventId InboundRequest   = new(1001, "BridgeIo.InboundRequest");
    public static readonly EventId InboundResponse  = new(1002, "BridgeIo.InboundResponse");
    public static readonly EventId UpstreamRequest  = new(1003, "BridgeIo.UpstreamRequest");
    public static readonly EventId UpstreamResponse = new(1004, "BridgeIo.UpstreamResponse");

    public static bool IsBridgeIo(int eventId) => eventId is >= 1001 and <= 1004;

    public static string Suffix(int eventId) => eventId switch
    {
        1001 => "inbound-req",
        1002 => "inbound-resp",
        1003 => "upstream-req",
        1004 => "upstream-resp",
        _    => throw new ArgumentOutOfRangeException(nameof(eventId)),
    };
}
