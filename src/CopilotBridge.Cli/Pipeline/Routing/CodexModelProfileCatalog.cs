namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Hand-curated catalog of <see cref="CodexModelProfile"/>s — one per Copilot
/// <c>/responses</c> model the bridge serves. The Responses-side analog of
/// <see cref="ModelProfileCatalog"/>. Every row is sourced from the live contract
/// snapshot (<c>docs/copilot-responses-contract-snapshot.json</c>, change 2);
/// change-2's B2 drift test goes red when the snapshot moves, prompting a
/// reconcile here.
/// </summary>
/// <remarks>
/// <para>Lookup is by canonical id (post-<see cref="CopilotModelRegistry.Normalize"/>,
/// which no-ops on the Codex ids). An exact miss falls back to the <b>nearest
/// known profile</b> via <see cref="GetNearest"/> (fuzzy match,
/// <see cref="ModelNameMatcher"/>) so a Copilot Responses model newer than this
/// build borrows the closest known model's effort-clamp + custom-tool-drop rules
/// instead of passing through unclamped; only an id too dissimilar to any known
/// model is a hard, surfaced error (<see cref="UnknownModelException"/>). The
/// catalog stays the source of probed truth — fuzzy matching is a best-effort
/// bridge until a real profile is added, not a substitute for probing.</para>
/// <para>Two uniform coercions apply to EVERY model (research §2.3/§2.4), so they
/// live as catalog-level facts rather than per-row flags: strip
/// <c>service_tier</c> (Copilot 400s it) and drop the <c>image_generation</c>
/// tool (Copilot 400s it). T2 applies both unconditionally.</para>
/// </remarks>
internal sealed class CodexModelProfileCatalog
{
    private readonly Dictionary<string, CodexModelProfile> _byId;
    private readonly IReadOnlyList<string> _knownIds;

    public CodexModelProfileCatalog()
    {
        _byId = BuildDefault().ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
        _knownIds = SortedIds(_byId);
    }

    /// <summary>Test-only: build from an explicit profile set.</summary>
    internal CodexModelProfileCatalog(IEnumerable<CodexModelProfile> profiles)
    {
        _byId = profiles.ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
        _knownIds = SortedIds(_byId);
    }

    private static IReadOnlyList<string> SortedIds(Dictionary<string, CodexModelProfile> byId)
    {
        var ids = new List<string>(byId.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>Profile for <paramref name="canonicalId"/>, or null if unknown.</summary>
    public CodexModelProfile? Get(string canonicalId) =>
        _byId.TryGetValue(canonicalId, out var p) ? p : null;

    /// <summary>
    /// Best-effort fallback: the profile whose canonical id is <b>most similar</b>
    /// to <paramref name="canonicalId"/> (Jaccard via <see cref="ModelNameMatcher"/>),
    /// or null below the similarity floor. The Responses-side analog of
    /// <see cref="ModelProfileCatalog.GetNearest"/> — lets a Codex model newer than
    /// this build's catalog borrow the nearest known model's effort-clamp +
    /// custom-tool-drop rules rather than passing through unclamped (which risks a
    /// Copilot 400/500). The real model id still goes on the wire.
    /// <para><paramref name="matchedId"/> / <paramref name="score"/> report the
    /// nearest candidate <b>whether or not it cleared the floor</b>, so a
    /// below-floor caller can surface it in the error; only empty inputs leave them
    /// empty / 0.</para>
    /// </summary>
    public CodexModelProfile? GetNearest(string canonicalId, out string matchedId, out double score)
    {
        var best = ModelNameMatcher.FindBest(canonicalId, _knownIds, out score);
        matchedId = best ?? "";
        if (best is null || score < ModelNameMatcher.DefaultMinSimilarity) return null;
        return Get(best);
    }

    /// <summary>All known canonical ids, sorted (cached — <c>_byId</c> is immutable
    /// after construction). Used in the unknown-model error body and by the fuzzy
    /// matcher's candidate set.</summary>
    public IReadOnlyList<string> KnownIds => _knownIds;

    public int Count => _byId.Count;

    /// <summary>
    /// True for the two uniform coercions every Responses model needs. Named
    /// constants so T2 reads them by intent and a future per-model exception is a
    /// one-line change.
    /// </summary>
    public const bool StripsServiceTier = true;
    public const bool DropsImageGenerationTool = true;

    /// <summary>
    /// The baseline profile set, row-by-row from
    /// <c>docs/copilot-responses-contract-snapshot.json</c> (seeded 2026-06-15,
    /// Enterprise), with <c>mai-code-1-flash-picker</c> re-probed directly 2026-07
    /// (see its row) and the <c>gpt-5.6</c> codename slots probed directly 2026-07
    /// (see their rows). Three effort profiles:
    /// <list type="bullet">
    ///   <item><b>large</b> — <c>gpt-5.3-codex</c>, <c>gpt-5.4</c>,
    ///         <c>gpt-5.4-mini</c>, <c>gpt-5.5</c>: accept
    ///         <c>none/low/medium/high/xhigh</c>, reject <c>minimal</c>.</item>
    ///   <item><b>xlarge</b> — <c>gpt-5.6-luna</c>, <c>gpt-5.6-sol</c>,
    ///         <c>gpt-5.6-terra</c>: <b>large + <c>max</c></b> — the first Codex
    ///         models to accept <c>max</c> (<c>Gpt56_Effort_ReProbe</c>: max → 200,
    ///         minimal → 400). So Anthropic's top tier passes through instead of
    ///         being clamped to <c>xhigh</c>.</item>
    ///   <item><b>small</b> — <c>gpt-5-mini</c>,
    ///         <c>mai-code-1-flash-picker</c>: accept
    ///         <c>minimal/low/medium/high</c>, reject <c>none</c> AND <c>xhigh</c>
    ///         (the inverse of large at the boundaries).</item>
    /// </list>
    /// <c>mai-code-1-flash-picker</c> additionally 500s on custom tools
    /// (<c>RejectsCustomTools</c>).
    /// </summary>
    private static IEnumerable<CodexModelProfile> BuildDefault()
    {
        // ── "large" effort profile: accept none/low/medium/high/xhigh, reject minimal ──
        // DefaultEffort=xhigh: an unaccepted inbound effort (notably Anthropic's
        // 'max', which Codex has no equivalent for) falls back to xhigh — the
        // large profile's top accepted tier — with a WARNING in CoerceEffort.
        string[] large = ["none", "low", "medium", "high", "xhigh"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5.3-codex", AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4",       AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4-mini",  AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.5",       AcceptedEfforts = large, DefaultEffort = "xhigh" };

        // ── "xlarge" effort profile: accept none/low/medium/high/xhigh/MAX, reject minimal ──
        // The gpt-5.6 codename slots (luna/sol/terra) are the first Codex models to
        // accept 'max' — every effort axis RE-PROBED directly 2026-07
        // (ResponsesProbe.Gpt56_Effort_ReProbe): null/none/low/medium/high/xhigh/max
        // → 200, minimal → 400. This EXTENDS "large" with max, so Anthropic's top
        // tier (which large clamps to xhigh) now passes through verbatim on these.
        // TRAP the sync skill warns of: the 400 body for 'minimal' lists supported
        // = [none,low,medium,high,xhigh] — OMITTING max — yet max live-probes 200.
        // The advertised list lies; the probe is ground truth. So AcceptedEfforts
        // includes max even though Copilot's own error message doesn't.
        // DefaultEffort=xhigh (NOT max): the only inbound value not in the set is
        // Codex's 'minimal'; clamping an unrecognized effort to the costliest tier
        // is a footgun, so fall back to xhigh (large's top) — max is reserved for
        // an explicit request, which now passes through as-is.
        // Custom tools ACCEPTED (Gpt56_Tool_ReProbe: function / custom apply_patch /
        // web_search → 200; image_generation → 400 but that's the catalog-level
        // uniform drop, not a per-model flag) → RejectsCustomTools stays false.
        string[] xlarge = ["none", "low", "medium", "high", "xhigh", "max"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5.6-luna",  AcceptedEfforts = xlarge, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.6-sol",   AcceptedEfforts = xlarge, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.6-terra", AcceptedEfforts = xlarge, DefaultEffort = "xhigh" };

        // ── "small" effort profile: accept minimal/low/medium/high, reject none+xhigh ──
        // DefaultEffort=high: small rejects xhigh, so its fallback is 'high' (its
        // top accepted tier) — an inbound 'max'/'xhigh' lands here.
        string[] small = ["minimal", "low", "medium", "high"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5-mini", AcceptedEfforts = small, DefaultEffort = "high" };
        // mai-code-1-flash-INTERNAL was retired by Copilot (2026 reconciliation —
        // 400 "not available for integrator"); the live Responses id is
        // mai-code-1-flash-PICKER (200 — ResponsesProbe.MaiCode_LivenessProbe).
        // Effort + custom-tool contract RE-PROBED directly on -picker 2026-07
        // (ResponsesProbe.MaiCodePicker_Effort_ReProbe / _Tool_ReProbe; underlying
        // model 'mai-2-flash-code-2026-05-18'): accepts null/minimal/low/medium/high,
        // REJECTS none + xhigh (400 "Supported values are: minimal, low, medium,
        // high") → the "small" set; custom apply_patch → 500 → RejectsCustomTools.
        // function + web_search → 200. Wire-verified, no longer extrapolated.
        yield return new CodexModelProfile
        {
            CanonicalId = "mai-code-1-flash-picker",
            AcceptedEfforts = small,     // MaiCodePicker_Effort_ReProbe: none/xhigh → 400
            DefaultEffort = "high",      // small's top accepted tier (xhigh rejected)
            RejectsCustomTools = true,   // MaiCodePicker_Tool_ReProbe: custom apply_patch → 500
        };
    }
}
