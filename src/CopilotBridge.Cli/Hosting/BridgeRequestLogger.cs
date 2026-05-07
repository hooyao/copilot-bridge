using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Writes per-request bridge logs to <c>logs/&lt;utc&gt;-&lt;seq&gt;.json</c>.
/// Used by the dev session to inspect Claude Code's actual wire format and
/// diagnose protocol mismatches between Claude Code and Copilot.
/// </summary>
internal sealed class BridgeRequestLogger
{
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "anthropic-auth-token",
    };

    private readonly string _logDir;
    private int _seq;

    public BridgeRequestLogger()
    {
        _logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

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
