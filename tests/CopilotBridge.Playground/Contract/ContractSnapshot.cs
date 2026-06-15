using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Contract;

/// <summary>
/// Shared machinery for the per-backend Copilot contract snapshots
/// (`docs/ir-definition-design.md` §7.B). A snapshot is committed JSON recording
/// the live wire-truth one Copilot backend exhibited on a given date — which
/// effort values each model accepted/rejected, which fields/tools it rejected,
/// the observed SSE event set. The asserting probes build a facts
/// <see cref="JsonObject"/> live, then either SEED the snapshot (opt-in) or DIFF
/// against the committed one and fail on any difference (drift detection, B2).
/// </summary>
/// <remarks>
/// Unlike change-1's offline field-diff harness (which has an allow-list of our
/// own transforms), this is deliberately strict: the snapshot IS the contract,
/// so ANY contract difference is drift and fails. The only thing ignored is the
/// volatile <c>_meta</c> stamp (capture date / account), which always differs and
/// is not a contract fact.
/// </remarks>
internal static class ContractSnapshot
{
    /// <summary>
    /// Opt-in seed: set <c>BRIDGE_REGEN_CONTRACT_SNAPSHOT=1</c> to (re)write the
    /// committed snapshot from a live run, then review the git diff. Never set in
    /// CI — that would make drift detection vacuously green.
    /// </summary>
    public static bool SeedMode =>
        Environment.GetEnvironmentVariable("BRIDGE_REGEN_CONTRACT_SNAPSHOT") == "1";

    /// <summary>Absolute path to a committed snapshot under <c>docs/</c>.</summary>
    public static string PathFor(string fileName) =>
        Path.Combine(FindRepoDocsDir(), fileName);

    /// <summary>Write a facts object to the committed snapshot (pretty, stable key order via the caller).</summary>
    public static void Write(string fileName, JsonObject facts)
    {
        var path = PathFor(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, facts.ToJsonString(Pretty) + "\n");
    }

    /// <summary>Read a committed snapshot, or null if it doesn't exist yet.</summary>
    public static JsonObject? ReadOrNull(string fileName)
    {
        var path = PathFor(fileName);
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    }

    /// <summary>
    /// The seed-or-diff lifecycle every contract sweep shares:
    /// <list type="bullet">
    ///   <item>SeedMode → write the live facts as the new snapshot, return no diffs.</item>
    ///   <item>no committed snapshot yet → write it (first seed), return no diffs
    ///         but flag it so the test output says "seeded, review &amp; commit".</item>
    ///   <item>otherwise → diff live vs committed, return the (possibly empty)
    ///         list of human-readable drift lines.</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<string> Diffs, bool Seeded) SeedOrDiff(string fileName, JsonObject liveFacts)
    {
        var committed = ReadOrNull(fileName);
        if (SeedMode || committed is null)
        {
            Write(fileName, liveFacts);
            return (Array.Empty<string>(), Seeded: true);
        }
        return (Diff(committed, liveFacts), Seeded: false);
    }

    /// <summary>
    /// Structural diff of two facts trees, ignoring <c>_meta</c>. Returns one
    /// readable line per difference: a key present on one side only, or a changed
    /// leaf/array value. ANY difference is reported (no tolerance) — the caller
    /// fails the test if the list is non-empty.
    /// </summary>
    public static IReadOnlyList<string> Diff(JsonObject committed, JsonObject live)
    {
        var diffs = new List<string>();
        Walk("", Strip(committed), Strip(live), diffs);
        diffs.Sort(StringComparer.Ordinal);
        return diffs;
    }

    // Drop the volatile _meta stamp before comparing — it's not a contract fact.
    private static JsonNode Strip(JsonObject o)
    {
        var clone = JsonNode.Parse(o.ToJsonString())!.AsObject();
        clone.Remove("_meta");
        return clone;
    }

    private static void Walk(string path, JsonNode? committed, JsonNode? live, List<string> sink)
    {
        if (committed is JsonObject co && live is JsonObject lo)
        {
            foreach (var key in co.Select(p => p.Key).Union(lo.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var hasC = co.TryGetPropertyValue(key, out var cv);
                var hasL = lo.TryGetPropertyValue(key, out var lv);
                var childPath = path.Length == 0 ? key : $"{path}.{key}";
                if (hasC && hasL) Walk(childPath, cv, lv, sink);
                else if (hasC) sink.Add($"REMOVED  {childPath}  (snapshot had {Render(cv)}; live no longer has it)");
                else sink.Add($"ADDED    {childPath}  (live now has {Render(lv)}; snapshot did not)");
            }
            return;
        }

        // Arrays compared as SETS (order-independent) — acceptance lists are sets.
        if (committed is JsonArray ca && live is JsonArray la)
        {
            var cset = ca.Select(Render).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var lset = la.Select(Render).OrderBy(s => s, StringComparer.Ordinal).ToList();
            foreach (var gone in cset.Except(lset))
                sink.Add($"REMOVED  {path}[]  (snapshot listed {gone}; live does not)");
            foreach (var added in lset.Except(cset))
                sink.Add($"ADDED    {path}[]  (live lists {added}; snapshot did not)");
            return;
        }

        var cs = Render(committed);
        var ls = Render(live);
        if (cs != ls)
            sink.Add($"CHANGED  {path}  (snapshot={cs} → live={ls})");
    }

    private static string Render(JsonNode? n) => n?.ToJsonString() ?? "null";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Walk up from the test bin dir to the repo root, return its <c>docs/</c> dir.</summary>
    private static string FindRepoDocsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var docs = Path.Combine(dir.FullName, "docs");
            if (Directory.Exists(docs) && File.Exists(Path.Combine(dir.FullName, "CopilotBridge.slnx")))
                return docs;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo docs/ dir from " + AppContext.BaseDirectory);
    }
}
