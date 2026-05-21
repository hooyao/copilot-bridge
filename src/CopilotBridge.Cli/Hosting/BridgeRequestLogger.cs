using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Writes per-request bridge logs to <c>logs/&lt;utc&gt;-&lt;seq&gt;.json</c>.
/// Used by the dev session to inspect Claude Code's actual wire format and
/// diagnose protocol mismatches between Claude Code and Copilot.
/// </summary>
/// <remarks>
/// When <c>BRIDGE_VERBOSE_LOG</c> environment variable is set (any non-empty
/// value), each request's headers/body/upstream/events also dump to stderr
/// in a human-readable form for live debugging. The full record is always
/// in the JSON file regardless; stderr is just a faster surface for active
/// debugging sessions. Bodies over <c>VerboseBodyMaxChars</c> are truncated
/// in the stderr output (full text remains in the JSON file).
/// </remarks>
internal sealed class BridgeRequestLogger
{
    private const string VerboseEnvVar = "BRIDGE_VERBOSE_LOG";
    private const int VerboseBodyMaxChars = 4000;

    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "anthropic-auth-token",
    };

    private static readonly bool _verboseEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(VerboseEnvVar));

    private readonly string _logDir;
    private int _seq;

    public BridgeRequestLogger()
    {
        _logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    public bool VerboseEnabled => _verboseEnabled;

    public async Task WriteAsync(BridgeRequestLog log, CancellationToken ct)
    {
        var seq = Interlocked.Increment(ref _seq);
        var stamp = log.StartedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(_logDir, $"{stamp}-{seq:D4}.json");

        var node = ToJsonNode(log);
        await File.WriteAllTextAsync(
            path,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);

        if (_verboseEnabled)
        {
            DumpToStderr(log, path);
        }
    }

    private static void DumpToStderr(BridgeRequestLog log, string auditPath)
    {
        var sb = new StringBuilder(8192);
        var divider = new string('=', 70);
        sb.AppendLine().AppendLine(divider);

        sb.Append("[").Append(log.StartedUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append("Z] ").Append(log.Method).Append(' ').Append(log.Path)
          .Append("  →  ").Append(log.UpstreamStatus)
          .Append("  (").Append(log.DurationMs).AppendLine(" ms)");

        AppendHeaders(sb, "inbound headers", log.InboundHeaders);
        AppendBody(sb, "inbound body", log.InboundBody);

        if (log.UpstreamUrl is not null)
        {
            sb.Append("upstream URL: ").AppendLine(log.UpstreamUrl);
            AppendHeaders(sb, "upstream headers (sent)", log.UpstreamHeaders);
            AppendBody(sb, "upstream body (sent)", log.UpstreamBody);
        }

        if (log.UpstreamResponseHeaders.Count > 0)
        {
            AppendHeaders(sb, "upstream response headers", log.UpstreamResponseHeaders);
        }

        if (log.DownstreamBody is not null)
        {
            AppendBody(sb, "downstream body (sent to client)", log.DownstreamBody);
        }

        if (log.Events.Count > 0)
        {
            var filtered = 0;
            foreach (var e in log.Events) if (e.Filtered) filtered++;
            sb.Append("SSE events: ").Append(log.Events.Count)
              .Append(" forwarded, ").Append(filtered).AppendLine(" filtered");
        }

        if (log.Error is not null)
        {
            sb.Append("error: ").AppendLine(log.Error);
        }

        sb.Append("audit log: ").AppendLine(auditPath);
        sb.AppendLine(divider);

        Console.Error.Write(sb.ToString());
    }

    private static void AppendHeaders(StringBuilder sb, string label, IReadOnlyDictionary<string, string> headers)
    {
        sb.Append(label).Append(" (").Append(headers.Count).AppendLine("):");
        foreach (var (key, value) in headers)
        {
            sb.Append("  ").Append(key).Append(": ")
              .AppendLine(RedactedHeaders.Contains(key) ? "<redacted>" : value);
        }
    }

    private static void AppendBody(StringBuilder sb, string label, string? body)
    {
        if (body is null)
        {
            sb.Append(label).AppendLine(": <none>");
            return;
        }
        sb.Append(label).Append(" (").Append(body.Length).AppendLine(" chars):");
        if (body.Length <= VerboseBodyMaxChars)
        {
            sb.AppendLine(body);
        }
        else
        {
            sb.AppendLine(body[..VerboseBodyMaxChars]);
            sb.Append("...[truncated, ").Append(body.Length - VerboseBodyMaxChars)
              .AppendLine(" more chars in audit JSON]");
        }
    }

    private static JsonObject ToJsonNode(BridgeRequestLog log)
    {
        var inbound = new JsonObject
        {
            ["method"] = log.Method,
            ["path"] = log.Path,
            ["headers"] = HeadersNode(log.InboundHeaders),
            ["body"] = ParseOrString(log.InboundBody),
        };

        var upstream = new JsonObject
        {
            ["url"] = log.UpstreamUrl,
            ["headers"] = HeadersNode(log.UpstreamHeaders),
            ["body"] = ParseOrString(log.UpstreamBody),
            ["status"] = log.UpstreamStatus,
            ["response_headers"] = HeadersNode(log.UpstreamResponseHeaders),
        };

        var root = new JsonObject
        {
            ["timestamp"] = log.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["duration_ms"] = log.DurationMs,
            ["inbound"] = inbound,
            ["upstream"] = upstream,
        };

        if (log.Events.Count > 0)
        {
            var events = new JsonArray();
            foreach (var e in log.Events)
            {
                var item = new JsonObject
                {
                    ["event"] = e.EventType,
                    ["data"] = ParseOrString(e.Data),
                };
                if (e.Filtered) item["filtered"] = true;
                events.Add((JsonNode?)item);
            }
            root["events"] = events;
        }

        if (log.DownstreamBody is not null)
        {
            root["downstream_body"] = ParseOrString(log.DownstreamBody);
        }

        if (log.Error is not null)
        {
            root["error"] = log.Error;
        }

        return root;
    }

    private static JsonObject HeadersNode(IReadOnlyDictionary<string, string> headers)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in headers)
        {
            obj[key] = RedactedHeaders.Contains(key) ? "<redacted>" : value;
        }
        return obj;
    }

    /// <summary>
    /// If <paramref name="raw"/> parses as JSON, embeds it inline (preserving
    /// structure for grep/jq); otherwise stores it as a string literal.
    /// </summary>
    private static JsonNode? ParseOrString(string? raw)
    {
        if (raw is null) return null;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
