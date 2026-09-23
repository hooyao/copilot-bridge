namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Profiles for Copilot's live Anthropic /v1/messages models. These are
/// request-format facts from live Playground probes, not /models capability
/// metadata. Unknown ids may borrow the nearest profile, but the requested id
/// stays on the wire so Copilot still decides whether it is available.
/// </summary>
internal sealed class ModelProfileCatalog
{
    private readonly Dictionary<string, ModelProfile> _byId;
    private readonly IReadOnlyList<string> _knownIds;

    public ModelProfileCatalog()
    {
        _byId = BuildDefault().ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
        _knownIds = SortedIds(_byId);
    }

    /// <summary>Test-only: build a catalog from an explicit profile set.</summary>
    internal ModelProfileCatalog(IEnumerable<ModelProfile> profiles)
    {
        _byId = profiles.ToDictionary(p => p.CanonicalId, StringComparer.OrdinalIgnoreCase);
        _knownIds = SortedIds(_byId);
    }

    private static IReadOnlyList<string> SortedIds(Dictionary<string, ModelProfile> byId)
    {
        var ids = new List<string>(byId.Keys);
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    public ModelProfile? Get(string canonicalId) =>
        _byId.TryGetValue(canonicalId, out var profile) ? profile : null;

    public ModelProfile? GetNearest(string canonicalId, out string matchedId, out double score)
    {
        var best = ModelNameMatcher.FindBest(canonicalId, _knownIds, out score);
        matchedId = best ?? "";
        if (best is null || score < ModelNameMatcher.DefaultMinSimilarity) return null;
        return Get(best);
    }

    public IReadOnlyList<string> KnownIds => _knownIds;
    public int Count => _byId.Count;

    private static IEnumerable<ModelProfile> BuildDefault()
    {
        // 2026-09 account reconciliation: /models advertises only this Claude
        // id. RetiredCandidate_LivenessProbe returned model_not_supported for
        // haiku-4.5, sonnet-4.6/5, and opus-4.6/4.7/4.8/5; none retains a
        // catalog claim. The integrator allowlist still names several of them,
        // but a live 400 outranks that list.
        //
        // Anthropic's Opus 5.5 migration guide documents the client-facing id
        // claude-opus-5-5. Opus55_ClientIdAlias_LivenessProbe confirmed that
        // Copilot accepts it and its advertised dotted id on both Messages and
        // count_tokens. Copilot responds using claude-opus-5.5.
        //
        // Opus55_Thinking_ProbeAcceptance: adaptive/omitted 200; disabled and
        // enabled 400. Opus55_Effort_ReProbe: low..max 200, independently and
        // with adaptive. Opus55_ToolChoice_ProbeAcceptance: auto/none 200,
        // forced any/tool 400. Each rejection was reconfirmed on a captured
        // 4-tool, 3-system-block, streaming Claude Code request, retaining its
        // real anthropic-beta header (Opus55_RealClientCapture_RejectedAxisStaysRejected).
        // Opus55_MidConversationSystem_PlacementRules: U·S and U·A·U·S 200,
        // illegal placements 400. Opus55_LargePrompt_ProbeOneMillionContextSupport:
        // 677k input tokens 200, with and without context-1m beta.
        yield return new ModelProfile
        {
            CanonicalId = "claude-opus-5.5",
            AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],
            EffortOnUnsupported = EffortHandling.Strip,
            Thinking = ThinkingPolicy.AdaptiveOnly,
            // The official migration guide recommends lower effort when
            // replacing thinking:disabled. Low is live-accepted on this model.
            EffortWhenDisabledThinkingUnsupported = "low",
            AcceptsMidConversationSystem = true,
            SupportsForcedToolChoice = false,
        };
    }
}
