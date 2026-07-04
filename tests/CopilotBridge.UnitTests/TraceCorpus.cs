using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Loads real Claude Code request bodies for the <see cref="TraceReplayResponsesTests"/>
/// harness from two sources, unioned:
/// <list type="number">
///   <item>the de-identified fixtures committed under <c>Fixtures/cc-request-*.json</c>
///         — always present, CI-safe, the deterministic baseline;</item>
///   <item>when the <c>BRIDGE_TRACE_DIR</c> environment variable points at a real
///         capture directory (the user's <c>request-traces</c>), a capped,
///         size-bounded sample of real <c>*-inbound-req.json</c> Claude Code
///         bodies routed at <c>/cc/v1/messages</c>.</item>
/// </list>
/// The second source lets the developer replay the exact traffic that failed
/// without committing large / potentially-sensitive captures; its absence is not
/// an error — the fixtures alone still guard the contract.
/// </summary>
internal static class TraceCorpus
{
    private const string TraceDirEnv = "BRIDGE_TRACE_DIR";
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // Bound the real-capture sample so the harness stays fast: the real corpus is
    // thousands of files, many > 500 KB. A modest cap of representative bodies is
    // enough to exercise every tool/tool_result/effort shape.
    private const int MaxRealSamples = 30;
    private const long MaxRealBodyBytes = 1_500_000;

    /// <summary>
    /// Yield <c>(name, bodyJson)</c> pairs. <c>name</c> is a stable label for the
    /// xUnit theory row; <c>bodyJson</c> is the raw Anthropic Messages request body.
    /// </summary>
    public static IEnumerable<(string Name, string BodyJson)> LoadClaudeCodeBodies()
    {
        foreach (var pair in LoadFixtureBodies())
            yield return pair;
        foreach (var pair in LoadRealCaptureBodies())
            yield return pair;
    }

    private static IEnumerable<(string, string)> LoadFixtureBodies()
    {
        if (!Directory.Exists(FixturesDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(FixturesDir, "cc-request-*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            string? body = null;
            try
            {
                var envelope = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
                // Fixtures wrap the request as { _meta, body }.
                body = envelope["body"]?.ToJsonString();
            }
            catch
            {
                // A malformed fixture is a test-asset bug, not a harness input —
                // skip it rather than fail the whole theory; the others still run.
            }
            if (body is not null)
                yield return ($"fixture:{Path.GetFileNameWithoutExtension(f)}", body);
        }
    }

    private static IEnumerable<(string, string)> LoadRealCaptureBodies()
    {
        var dir = Environment.GetEnvironmentVariable(TraceDirEnv);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) yield break;

        var yielded = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*-inbound-req.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            if (yielded >= MaxRealSamples) yield break;
            var info = new FileInfo(f);
            if (info.Length > MaxRealBodyBytes) continue;

            string? body = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(f));
                var root = doc.RootElement;
                // Only Claude Code inbound requests (target /cc/...); a captured
                // Codex request at /codex/responses is a different wire shape.
                if (!root.TryGetProperty("target", out var tgt)
                    || tgt.GetString() is not { } target
                    || !target.StartsWith("/cc/", StringComparison.Ordinal))
                    continue;
                if (!root.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object)
                    continue;
                if (!b.TryGetProperty("model", out var m) || m.GetString() is not { } model
                    || !model.StartsWith("claude-", StringComparison.Ordinal))
                    continue;
                body = b.GetRawText();
            }
            catch
            {
                // Truncated/locked capture file — skip.
            }
            if (body is not null)
            {
                yielded++;
                yield return ($"trace:{Path.GetFileNameWithoutExtension(f)}", body);
            }
        }
    }
}
