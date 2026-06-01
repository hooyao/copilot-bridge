using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Reads per-request audit JSON files written under <c>{baseDir}/request-traces/</c>.
/// The sink emits FOUR files per request — one each for <c>inbound-req</c>,
/// <c>inbound-resp</c>, <c>upstream-req</c>, <c>upstream-resp</c> — sharing the
/// same <c>seq</c>. This reader groups them back into one
/// <see cref="BridgeLogEntry"/> so tests can assert on the full request
/// without juggling four file paths.
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

    /// <summary>
    /// Returns one entry per request seen since this reader was constructed,
    /// ordered by sequence number. Phases that haven't been written yet leave
    /// the corresponding property null (e.g. a request that 400'd before
    /// reaching upstream has no upstream-req / upstream-resp).
    /// </summary>
    public IReadOnlyList<BridgeLogEntry> ReadNew()
    {
        if (!Directory.Exists(_logDir)) return Array.Empty<BridgeLogEntry>();
        var files = Directory.GetFiles(_logDir, "*.json")
            .Where(f => !_seenAtStart.Contains(f))
            .ToList();

        var bySeq = new Dictionary<int, BridgeLogEntry>();
        foreach (var f in files)
        {
            var raw = File.ReadAllText(f);
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(raw)?.AsObject();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Log file is not valid JSON: {f} — {ex.Message}", ex);
            }
            if (root is null) continue;

            var seq = root["seq"]?.GetValue<int>() ?? -1;
            var kind = root["kind"]?.GetValue<string>() ?? "";
            if (seq < 0 || string.IsNullOrEmpty(kind)) continue;

            if (!bySeq.TryGetValue(seq, out var entry))
            {
                entry = new BridgeLogEntry { Seq = seq };
                bySeq[seq] = entry;
            }
            switch (kind)
            {
                case "inbound-req":   entry.InboundReq   = root; break;
                case "inbound-resp":  entry.InboundResp  = root; break;
                case "upstream-req":  entry.UpstreamReq  = root; break;
                case "upstream-resp": entry.UpstreamResp = root; break;
            }
        }

        return bySeq.Values.OrderBy(e => e.Seq).ToList();
    }
}

/// <summary>
/// A full request's audit, reassembled from its four per-phase JSON files.
/// Properties return null when the corresponding phase wasn't recorded
/// (e.g. a 400 emitted before the pipeline reached upstream).
/// </summary>
internal sealed class BridgeLogEntry
{
    public int Seq { get; init; }

    public JsonObject? InboundReq { get; set; }
    public JsonObject? InboundResp { get; set; }
    public JsonObject? UpstreamReq { get; set; }
    public JsonObject? UpstreamResp { get; set; }

    public string InboundMethod => InboundReq?["method"]?.GetValue<string>() ?? "";
    public string InboundPath   => InboundReq?["target"]?.GetValue<string>() ?? "";
    public JsonNode? InboundBody => InboundReq?["body"];

    public int InboundStatus  => InboundResp?["status"]?.GetValue<int>() ?? 0;
    public JsonNode? InboundResponseBody => InboundResp?["body"];
    public JsonArray? Events => InboundResp?["events"]?.AsArray();

    public string? UpstreamUrl  => UpstreamReq?["url"]?.GetValue<string>();
    public JsonNode? UpstreamBody => UpstreamReq?["body"];

    public int UpstreamStatus => UpstreamResp?["status"]?.GetValue<int>() ?? 0;
}
