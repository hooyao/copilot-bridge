using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Reads bridge audit log files written under <c>{baseDir}/logs/</c>. Used by
/// headless tests to inspect what the bridge actually forwarded to Copilot
/// after running <c>claude.exe</c> end-to-end.
/// </summary>
internal sealed class BridgeLogReader
{
    private readonly string _logDir;
    private readonly HashSet<string> _seenAtStart;

    public BridgeLogReader(string logDir)
    {
        _logDir = logDir;
        _seenAtStart = Directory.Exists(logDir)
            ? new HashSet<string>(Directory.GetFiles(logDir, "*.json"))
            : new HashSet<string>();
    }

    /// <summary>Returns log entries written since this reader was constructed, ordered oldest-first.</summary>
    public IReadOnlyList<BridgeLogEntry> ReadNew()
    {
        if (!Directory.Exists(_logDir)) return Array.Empty<BridgeLogEntry>();
        var files = Directory.GetFiles(_logDir, "*.json")
            .Where(f => !_seenAtStart.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var entries = new List<BridgeLogEntry>(files.Count);
        foreach (var f in files)
        {
            var raw = File.ReadAllText(f);
            try
            {
                var node = JsonNode.Parse(raw)
                    ?? throw new InvalidOperationException($"Empty log file: {f}");
                entries.Add(new BridgeLogEntry(f, node.AsObject()));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Log file is not valid JSON: {f} — {ex.Message}", ex);
            }
        }
        return entries;
    }
}

internal sealed record BridgeLogEntry(string Path, JsonObject Root)
{
    public string InboundPath => Root["inbound"]?["path"]?.GetValue<string>() ?? "";
    public string InboundMethod => Root["inbound"]?["method"]?.GetValue<string>() ?? "";
    public JsonNode? InboundBody => Root["inbound"]?["body"];
    public JsonNode? UpstreamBody => Root["upstream"]?["body"];
    public int UpstreamStatus => Root["upstream"]?["status"]?.GetValue<int>() ?? 0;
    public string? UpstreamUrl => Root["upstream"]?["url"]?.GetValue<string>();
    public JsonArray? Events => Root["events"]?.AsArray();
}
