using System.Buffers;
using System.Threading.Channels;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Per-process monotonic counter for bridge IO audit files. Endpoints reserve
/// a single seq for one inbound request and pass it down to all four artifact
/// log calls so the resulting files share a stamp. Combined with a UTC time
/// captured at the inbound endpoint, it forms a stable trace id
/// (<c>{yyyyMMdd-HHmmss}-{seq:D4}</c>) that names every artifact of the
/// request and prefixes the per-request INFO summary log line.
/// </summary>
internal static class BridgeIoSeq
{
    private static int _value;

    public static int Next() => Interlocked.Increment(ref _value);

    /// <summary>
    /// Build the per-request trace id used to (1) name the four audit JSON
    /// files for this request and (2) prefix every in-request log line — the
    /// enter/exit boundary, the pipeline stages, and the INFO summary — as
    /// <c>[&lt;traceId&gt;] </c> via <c>ReqTraceFormatEnricher</c>. Same string at
    /// all sites so operators can grep one and find the rest.
    /// </summary>
    public static string BuildTraceId(int seq, DateTime utcStart) =>
        $"{utcStart:yyyyMMdd-HHmmss}-{seq:D4}";
}

/// <summary>
/// ILogger extensions used by endpoints to ship inbound/upstream request and
/// response artifacts at <see cref="LogLevel.Information"/>. The events are
/// marked with EventIds 1001-1004 (see <see cref="BridgeIoEvents"/>) so the
/// custom <see cref="BridgeIoSink"/> can pick them out of the Serilog event
/// stream and route them to per-request JSON files instead of the rolling log.
/// </summary>
internal static class BridgeIoLoggerExtensions
{
    public static void LogInboundRequest(
        this ILogger logger,
        int seq,
        string traceId,
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        int bodyLength)
    {
        var payload = new BridgeIoPayload
        {
            Seq = seq,
            TraceId = traceId,
            TimestampUtc = DateTime.UtcNow,
            Kind = "inbound-req",
            Method = method,
            Target = path,
            Headers = headers,
            Body = body,
            BodyLength = bodyLength,
        };
        Emit(logger, BridgeIoEvents.InboundRequest, payload);
    }

    public static void LogInboundResponse(
        this ILogger logger,
        int seq,
        string traceId,
        int status,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        int bodyLength,
        IReadOnlyList<CapturedSseEvent>? events = null,
        string? error = null,
        long? durationMs = null)
    {
        var payload = new BridgeIoPayload
        {
            Seq = seq,
            TraceId = traceId,
            TimestampUtc = DateTime.UtcNow,
            Kind = "inbound-resp",
            Status = status,
            Headers = headers,
            Body = body,
            BodyLength = bodyLength,
            Events = events,
            Error = error,
            DurationMs = durationMs,
        };
        Emit(logger, BridgeIoEvents.InboundResponse, payload);
    }

    public static void LogUpstreamRequest(
        this ILogger logger,
        int seq,
        string traceId,
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        int bodyLength)
    {
        var payload = new BridgeIoPayload
        {
            Seq = seq,
            TraceId = traceId,
            TimestampUtc = DateTime.UtcNow,
            Kind = "upstream-req",
            Method = method,
            Target = url,
            Headers = headers,
            Body = body,
            BodyLength = bodyLength,
        };
        Emit(logger, BridgeIoEvents.UpstreamRequest, payload);
    }

    public static void LogUpstreamResponse(
        this ILogger logger,
        int seq,
        string traceId,
        int status,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        int bodyLength,
        string? error = null)
    {
        var payload = new BridgeIoPayload
        {
            Seq = seq,
            TraceId = traceId,
            TimestampUtc = DateTime.UtcNow,
            Kind = "upstream-resp",
            Status = status,
            Headers = headers,
            Body = body,
            BodyLength = bodyLength,
            Error = error,
        };
        Emit(logger, BridgeIoEvents.UpstreamResponse, payload);
    }

    private static void Emit(ILogger logger, EventId eventId, BridgeIoPayload payload)
    {
        var state = new[] { new KeyValuePair<string, object?>("Payload", payload) };
        logger.Log(
            LogLevel.Information,
            eventId,
            (IEnumerable<KeyValuePair<string, object?>>)state,
            exception: null,
            formatter: static (_, _) => "bridge-io");
    }
}
