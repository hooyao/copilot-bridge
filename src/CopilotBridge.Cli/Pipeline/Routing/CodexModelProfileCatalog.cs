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
/// which no-ops on the Codex ids). A miss is a hard error
/// (<see cref="UnknownModelException"/>) — the bridge never guesses a Codex
/// model's effort contract.</para>
/// <para>Two uniform coercions apply to EVERY model (research §2.3/§2.4), so they
/// live as catalog-level facts rather than per-row flags: strip
/// <c>service_tier</c> (Copilot 400s it) and drop the <c>image_generation</c>
/// tool (Copilot 400s it). T2 applies both unconditionally.</para>
/// </remarks>
internal sealed class CodexModelProfileCatalog
{
    private readonly Dictionary<string, CodexModelProfile> _byId;

    public CodexModelProfileCatalog()
    {
        _byId = BuildDefault().ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Test-only: build from an explicit profile set.</summary>
    internal CodexModelProfileCatalog(IEnumerable<CodexModelProfile> profiles)
    {
        _byId = profiles.ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
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
    /// </summary>
    public CodexModelProfile? GetNearest(string canonicalId, out string matchedId, out double score)
    {
        matchedId = "";
        var nearest = ModelNameMatcher.FindNearest(canonicalId, KnownIds, out score);
        if (nearest is null) return null;
        matchedId = nearest;
        return Get(nearest);
    }

    /// <summary>All known canonical ids, sorted — used in the unknown-model error body.</summary>
    public IReadOnlyList<string> KnownIds
    {
        get
        {
            var ids = new List<string>(_byId.Keys);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

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
    /// Enterprise). Two effort profiles:
    /// <list type="bullet">
    ///   <item><b>large</b> — <c>gpt-5.3-codex</c>, <c>gpt-5.4</c>,
    ///         <c>gpt-5.4-mini</c>, <c>gpt-5.5</c>: accept
    ///         <c>none/low/medium/high/xhigh</c>, reject <c>minimal</c>.</item>
    ///   <item><b>small</b> — <c>gpt-5-mini</c>,
    ///         <c>mai-code-1-flash-picker</c>: accept
    ///         <c>minimal/low/medium/high</c>, reject <c>none</c> AND <c>xhigh</c>
    ///         (the inverse of large at the boundaries).</item>
    /// </list>
    /// <c>mai-code-1-flash-picker</c> additionally 500s on custom tools.
    /// </summary>
    private static IEnumerable<CodexModelProfile> BuildDefault()
    {
        // ── "large" effort profile: accept none/low/medium/high/xhigh, reject minimal ──
        string[] large = ["none", "low", "medium", "high", "xhigh"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5.3-codex", AcceptedEfforts = large };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4",       AcceptedEfforts = large };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.4-mini",  AcceptedEfforts = large };
        yield return new CodexModelProfile { CanonicalId = "gpt-5.5",       AcceptedEfforts = large };

        // ── "small" effort profile: accept minimal/low/medium/high, reject none+xhigh ──
        string[] small = ["minimal", "low", "medium", "high"];
        yield return new CodexModelProfile { CanonicalId = "gpt-5-mini", AcceptedEfforts = small };
        // PLAYGROUND-PENDING: mai-code-1-flash-INTERNAL was retired by Copilot (2026
        // reconciliation — 400 "not available for integrator"); the live Responses id
        // is mai-code-1-flash-PICKER (200 — ResponsesProbe.MaiCode_LivenessProbe).
        // The effort profile + custom-tool rejection below are extrapolated from the
        // retired -internal sibling (same underlying mai-code-1-flash model, different
        // routing suffix) and NOT yet re-probed on -picker. Re-run the Responses
        // effort/tool matrix against -picker and reconcile before trusting these.
        yield return new CodexModelProfile
        {
            CanonicalId = "mai-code-1-flash-picker",
            AcceptedEfforts = small,
            RejectsCustomTools = true,   // snapshot: custom_apply_patch in tools_rejected (500)
        };
    }
}
