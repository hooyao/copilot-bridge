using System.Text.Json.Nodes;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Field-diff harness for the A-invariant suite
/// (<c>docs/ir-definition-design.md</c> §7.1). Compares two request/response
/// bodies (as <see cref="JsonNode"/> trees) and classifies every leaf
/// difference as <see cref="DiffKind.Identical"/>,
/// <see cref="DiffKind.AllowedTransform"/>, or <see cref="DiffKind.Violation"/>.
/// Only <see cref="DiffKind.Violation"/> fails a test.
/// </summary>
/// <remarks>
/// <para>
/// This extends the spirit of <c>ApiComparisonTests</c>'s JsonNode differ (in
/// the Playground project, Integration-only, print-only) into a CI-safe,
/// assert-driven classifier. The allowed-transform list is an EXPLICIT, reviewed
/// allowlist tied to OUR OWN deterministic translation code — never a silent
/// tolerance. A diff that matches no allow rule is a VIOLATION (§7.1: "anything
/// not on it that differs is a failure").
/// </para>
/// <para>
/// Per the test philosophy (§7.0), the allow rules encode only our transforms
/// (model-id normalization, the provider-extensions envelope appearing/vanishing,
/// key reordering that STJ may produce), NEVER an assertion about what Copilot
/// currently accepts.
/// </para>
/// </remarks>
internal enum DiffKind { Identical, AllowedTransform, Violation }

/// <summary>One classified leaf difference between two JSON trees.</summary>
internal readonly record struct FieldDiff(string Path, DiffKind Kind, string? Left, string? Right, string Reason)
{
    public override string ToString() =>
        $"{Kind} @ {Path}: {Reason}  (left={Truncate(Left)} | right={Truncate(Right)})";

    private static string Truncate(string? s) =>
        s is null ? "<absent>" : s.Length <= 80 ? s : s[..80] + $"…[+{s.Length - 80}]";
}

/// <summary>
/// An explicit allow rule. <see cref="Matches"/> decides whether a given leaf
/// diff is an intended transform (returns true → classified AllowedTransform)
/// rather than a fidelity violation.
/// </summary>
internal sealed record AllowRule(string Description, Func<string, string?, string?, bool> Matches);

internal sealed class FieldDiffHarness
{
    private readonly IReadOnlyList<AllowRule> _allow;

    public FieldDiffHarness(IEnumerable<AllowRule> allowRules)
    {
        _allow = allowRules.ToList();
    }

    /// <summary>The default allowlist for change-1's round-trip tests.</summary>
    public static FieldDiffHarness Default() => new(DefaultAllowRules());

    /// <summary>
    /// Compare <paramref name="left"/> (the original) against
    /// <paramref name="right"/> (the round-tripped output). Returns every leaf
    /// diff with its classification. Structural equality is by path: a key
    /// present on one side and absent on the other is a diff at that path.
    /// </summary>
    public IReadOnlyList<FieldDiff> Diff(JsonNode? left, JsonNode? right)
    {
        var diffs = new List<FieldDiff>();
        Walk("$", left, right, diffs);
        return diffs;
    }

    /// <summary>Diffs that are not <see cref="DiffKind.Identical"/>.</summary>
    public IReadOnlyList<FieldDiff> Significant(JsonNode? left, JsonNode? right) =>
        Diff(left, right).Where(d => d.Kind != DiffKind.Identical).ToList();

    /// <summary>The VIOLATIONs only — empty means the round trip is faithful.</summary>
    public IReadOnlyList<FieldDiff> Violations(JsonNode? left, JsonNode? right) =>
        Diff(left, right).Where(d => d.Kind == DiffKind.Violation).ToList();

    private void Walk(string path, JsonNode? a, JsonNode? b, List<FieldDiff> sink)
    {
        // Both objects: union of keys; recurse.
        if (a is JsonObject ao && b is JsonObject bo)
        {
            foreach (var key in ao.Select(p => p.Key).Union(bo.Select(p => p.Key)))
            {
                var hasA = ao.TryGetPropertyValue(key, out var av);
                var hasB = bo.TryGetPropertyValue(key, out var bv);
                var childPath = $"{path}.{key}";
                if (hasA && hasB)
                {
                    Walk(childPath, av, bv, sink);
                }
                else
                {
                    // Key on exactly one side — a presence diff, classified.
                    sink.Add(Classify(childPath,
                        hasA ? Render(av) : null,
                        hasB ? Render(bv) : null));
                }
            }
            return;
        }

        // Both arrays: compare element-wise; length mismatch surfaces as
        // presence diffs at the trailing indices.
        if (a is JsonArray aa && b is JsonArray ba)
        {
            var n = Math.Max(aa.Count, ba.Count);
            for (var i = 0; i < n; i++)
            {
                var childPath = $"{path}[{i}]";
                var av = i < aa.Count ? aa[i] : null;
                var bv = i < ba.Count ? ba[i] : null;
                if (i < aa.Count && i < ba.Count)
                {
                    Walk(childPath, av, bv, sink);
                }
                else
                {
                    sink.Add(Classify(childPath,
                        i < aa.Count ? Render(av) : null,
                        i < ba.Count ? Render(bv) : null));
                }
            }
            return;
        }

        // Leaf (or shape mismatch object-vs-array-vs-scalar): compare rendered text.
        var ls = Render(a);
        var rs = Render(b);
        sink.Add(ls == rs
            ? new FieldDiff(path, DiffKind.Identical, ls, rs, "equal")
            : Classify(path, ls, rs));
    }

    private FieldDiff Classify(string path, string? left, string? right)
    {
        if (left == right)
            return new FieldDiff(path, DiffKind.Identical, left, right, "equal");

        foreach (var rule in _allow)
        {
            if (rule.Matches(path, left, right))
                return new FieldDiff(path, DiffKind.AllowedTransform, left, right, rule.Description);
        }
        return new FieldDiff(path, DiffKind.Violation, left, right, "unlisted difference");
    }

    private static string? Render(JsonNode? n) => n?.ToJsonString();

    /// <summary>
    /// Change-1 allow rules. EVERY entry is one of our own deterministic
    /// transforms; none asserts Copilot behavior (§7.0).
    /// </summary>
    private static IEnumerable<AllowRule> DefaultAllowRules()
    {
        // The provider-extensions envelope is additive and inert: it may appear
        // on the round-tripped side (right) when a test injects it, and is
        // absent on the original (left). Its PRESENCE is never a violation; its
        // CONTENT fidelity is asserted directly by A3/A4, not here.
        yield return new AllowRule(
            "provider_extensions envelope present on exactly one side (additive, inert)",
            (path, l, r) => path.EndsWith(".provider_extensions", StringComparison.Ordinal)
                            && (l is null || r is null));

        // STJ may render the same number differently than the captured text
        // (e.g. trailing-zero / exponent normalization). Treat numerically-equal
        // scalars as identical — a formatting transform, not a value change.
        yield return new AllowRule(
            "numerically-equal scalar reformatted by System.Text.Json",
            (_, l, r) => l is not null && r is not null
                         && double.TryParse(l, out var dl) && double.TryParse(r, out var dr)
                         && dl == dr);

        // The Anthropic `content` union (string | ContentBlock[]) is normalized
        // by ContentBlockParamListConverter to the array form on parse
        // (MessageParam.cs). A captured `"content":"text"` therefore round-trips
        // to `[{"type":"text","text":"text"}]`. This is OUR deterministic
        // structural move (a §7.1 "semantically equal" transform), not a value
        // change — assert it explicitly rather than tolerate it silently.
        yield return new AllowRule(
            "content string→[{type:text}] normalized by ContentBlockParamListConverter",
            (path, l, r) => IsContentStringToBlockArray(path, l, r));

        // KNOWN PRE-EXISTING lossy modeling (NOT introduced by this change —
        // the production hot path already drops these; verified in committed
        // upstream-req captures, keys = [type,properties,required]). `InputSchema`
        // (Tool.cs) models only type/properties/required, so other JSON-Schema
        // keywords a tool definition carries ($schema, additionalProperties, …)
        // are dropped on round trip. Copilot accepts the reduced schema (prod
        // runs this daily). Logged as a follow-up fidelity improvement in
        // docs/ir-definition-design.md §10; making InputSchema byte-faithful
        // would CHANGE the /cc upstream bytes, so it is out of scope for a
        // change whose gate is hot-path byte-equality. Classified as an allowed
        // (documented) transform so A1 stays honest about what we actually do.
        yield return new AllowRule(
            "input_schema keyword dropped by InputSchema model (pre-existing lossy modeling; see §10)",
            (path, l, r) => path.Contains(".input_schema.", StringComparison.Ordinal)
                            && r is null && l is not null);
    }

    private static bool IsContentStringToBlockArray(string path, string? l, string? r)
    {
        // path ends in ".content"; left is a JSON string, right is an array of
        // one text block carrying the same string.
        if (!path.EndsWith(".content", StringComparison.Ordinal)) return false;
        if (l is null || r is null) return false;
        JsonNode? ln, rn;
        try { ln = JsonNode.Parse(l); rn = JsonNode.Parse(r); }
        catch { return false; }
        if (ln is not JsonValue lv || !lv.TryGetValue<string>(out var s)) return false;
        if (rn is not JsonArray arr || arr.Count != 1) return false;
        var only = arr[0];
        return only?["type"]?.GetValue<string>() == "text"
               && only?["text"]?.GetValue<string>() == s;
    }
}
