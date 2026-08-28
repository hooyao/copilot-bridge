namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Hand-curated catalog of <see cref="CodexModelProfile"/>s — one per Copilot
/// <c>/responses</c> model the bridge serves. The Responses-side analog of
/// <see cref="ModelProfileCatalog"/>. Every row is sourced from the live contract
/// snapshot (<c>docs/copilot-responses-contract-snapshot.json</c>, change 2);
/// the B2/B3 contract sweep goes red when the snapshot or catalog moves,
/// prompting a reconcile here.
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
/// <para>Three uniform coercions apply to EVERY model (research §2.3/§2.4), so
/// they live as catalog-level facts rather than per-row flags: strip
/// <c>service_tier</c>, strip <c>store:true</c>, and drop the
/// <c>image_generation</c> tool (Copilot 400s each shape). T2 applies all three.</para>
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
    /// True for the three uniform coercions every Responses model needs. Named
    /// constants so T2 reads them by intent and a future per-model exception is a
    /// one-line change.
    /// </summary>
    public const bool StripsServiceTier = true;
    public const bool StripsStoreTrue = true;
    public const bool DropsImageGenerationTool = true;

    /// <summary>
    /// The baseline profile set, row-by-row from
    /// <c>docs/copilot-responses-contract-snapshot.json</c> (seeded 2026-06-15,
    /// Enterprise), with <c>mai-code-1-flash-picker</c> re-probed directly 2026-07
    /// (see its row) and the <c>gpt-5.6</c> codename slots probed directly 2026-07/08
    /// (see their rows). Three effort profiles:
    /// <list type="bullet">
    ///   <item><b>large</b> — <c>gpt-5.3-codex</c>, <c>gpt-5.4</c>,
    ///         <c>gpt-5.4-mini</c>, <c>gpt-5.5</c>: accept
    ///         <c>none/low/medium/high/xhigh</c>, reject <c>minimal</c>.</item>
    ///   <item><b>xlarge</b> — <c>gpt-5.6-luna</c>, <c>gpt-5.6-sol</c>,
    ///         <c>gpt-5.6-sol-fast</c>, <c>gpt-5.6-terra</c>:
    ///         <b>large + <c>max</c></b> — the first Codex
    ///         models to accept <c>max</c> (<c>Gpt56_Effort_ReProbe</c>: max → 200,
    ///         minimal → 400). So Anthropic's top tier passes through instead of
    ///         being clamped to <c>xhigh</c>.</item>
    ///   <item><b>small</b> — <c>gpt-5-mini</c>,
    ///         <c>mai-code-1-flash-picker</c>: accept
    ///         <c>minimal/low/medium/high</c>, reject <c>none</c> AND <c>xhigh</c>
    ///         (the inverse of large at the boundaries).</item>
    /// </list>
    /// No current model rejects custom tools; the flag remains available for a
    /// future model-specific backend constraint.
    /// </summary>
    private static IEnumerable<CodexModelProfile> BuildDefault()
    {
        // ── "large" effort profile: accept none/low/medium/high/xhigh, reject minimal ──
        // DefaultEffort=xhigh: an unaccepted inbound effort falls back to xhigh —
        // the large profile's top accepted tier — with a WARNING in CoerceEffort.
        // For THIS profile Anthropic's 'max' has no equivalent (large tops out at
        // xhigh), so 'max' lands on the fallback; the "xlarge" profile below DOES
        // accept 'max', so there it passes through instead.
        string[] large = ["none", "low", "medium", "high", "xhigh"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5.3-codex", AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4",       AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4-mini",  AcceptedEfforts = large, DefaultEffort = "xhigh" };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.5",       AcceptedEfforts = large, DefaultEffort = "xhigh" };

        // ── "xlarge" effort profile: accept none/low/medium/high/xhigh/max, reject minimal ──
        // The gpt-5.6 codename slots (luna/sol/sol-fast/terra) are the first Codex models to
        // accept 'max' — every effort axis RE-PROBED directly 2026-07/08
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
        // A direct two-turn function-output probe (2026-08-06) sent a generated red
        // PNG as output:[{type:input_text},{type:input_image}] and sol answered
        // exactly "red" (200). This proves a capability that ordinary top-level
        // vision probes do not. Sibling rows remain false until individually probed.
        yield return new CodexModelProfile
        {
            CanonicalId = "gpt-5.6-sol",
            AcceptedEfforts = xlarge,
            DefaultEffort = "xhigh",
            SupportsMultimodalFunctionOutput = true,
        };
        // Added 2026-08-28 from direct live evidence:
        // Gpt56_Effort_ReProbe accepts none/low/medium/high/xhigh/max and rejects
        // minimal; Gpt56_Tool_ReProbe accepts function/custom/web_search and
        // rejects only the catalog-wide image_generation tool;
        // Gpt56SolFamily_StructuredImageFunctionOutput_IsAcceptedAndUnderstood
        // completes the exact two-turn structured image-output loop as "red".
        yield return new CodexModelProfile
        {
            CanonicalId = "gpt-5.6-sol-fast",
            AcceptedEfforts = xlarge,
            DefaultEffort = "xhigh",
            SupportsMultimodalFunctionOutput = true,
        };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.6-terra", AcceptedEfforts = xlarge, DefaultEffort = "xhigh" };

        // ── "small" effort profile: accept minimal/low/medium/high, reject none+xhigh ──
        // DefaultEffort=high: small rejects xhigh, so its fallback is 'high' (its
        // top accepted tier) — an inbound 'max'/'xhigh' lands here.
        string[] small = ["minimal", "low", "medium", "high"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5-mini", AcceptedEfforts = small, DefaultEffort = "high" };
        // mai-code-1-flash-INTERNAL was retired by Copilot (2026 reconciliation —
        // 400 "not available for integrator"); the live Responses id is
        // mai-code-1-flash-PICKER (200 — ResponsesProbe.MaiCode_LivenessProbe).
        // Effort + custom-tool contract RE-PROBED directly on -picker 2026-07/08
        // (ResponsesProbe.MaiCodePicker_Effort_ReProbe / _Tool_ReProbe; underlying
        // model 'mai-2-flash-code-2026-05-18'): accepts null/minimal/low/medium/high,
        // REJECTS none + xhigh (400 "Supported values are: minimal, low, medium,
        // high") → the "small" set. Custom apply_patch was 500 in 2026-07 but
        // re-probed 200 on 2026-08-28 (MaiCodePicker_Tool_ReProbe), so no custom
        // tool rewrite remains. function + web_search → 200.
        yield return new CodexModelProfile
        {
            CanonicalId = "mai-code-1-flash-picker",
            AcceptedEfforts = small,     // MaiCodePicker_Effort_ReProbe: none/xhigh → 400
            DefaultEffort = "high",      // small's top accepted tier (xhigh rejected)
        };
    }
}
