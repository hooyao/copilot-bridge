namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Thrown by <see cref="Stages.Anthropic.ModelRouterStage"/> when, after
/// normalize + any matching user routing location, the resolved model id has no
/// exact entry in <see cref="ModelProfileCatalog"/> <b>and</b> is too dissimilar
/// to any known model for the best-effort fuzzy fallback to borrow a profile
/// (see <see cref="ModelNameMatcher"/>). A model that DOES fuzzy-match is
/// forwarded (with a WARN log), not thrown — this exception is now only the
/// below-floor / unrecognized-vendor case, where guessing a wire shape would be
/// unsafe. Better to surface a clear actionable error than a silent Copilot 400.
/// </summary>
/// <remarks>
/// Caught by the endpoint (<c>ClaudeCodeMessagesEndpoint</c>), which converts
/// it into HTTP 400 + an Anthropic-format error body containing the same
/// diagnostics. Whichever the client looks at (the wire response or the
/// bridge's own log), the message tells them what to fix.
/// </remarks>
internal sealed class UnknownModelException : Exception
{
    /// <summary>The inbound model id as the client sent it (pre-normalize).</summary>
    public string RequestedModel { get; }

    /// <summary>The model id after normalize + any user location that fired — the one looked up in the catalog.</summary>
    public string ResolvedModel { get; }

    /// <summary>
    /// The user routing location that produced <see cref="ResolvedModel"/>
    /// from <see cref="RequestedModel"/>, or null if no location matched (the
    /// client asked for a model the bridge has no profile for and no rule
    /// remapped it). When non-null, this points at the location whose
    /// <c>Use.Model</c> target is missing from the catalog — the operator
    /// fixes the location, not the client.
    /// </summary>
    public RouteLocation? AppliedLocation { get; }

    /// <summary>Index of <see cref="AppliedLocation"/> in <c>Routing.Locations</c> (for the error message).</summary>
    public int? AppliedLocationIndex { get; }

    /// <summary>All canonical ids the catalog does know — listed in the error message so users see their options.</summary>
    public IReadOnlyList<string> KnownProfiles { get; }

    /// <summary>
    /// The closest known model id the fuzzy matcher found, if any — present only
    /// when a best-effort match was attempted and the best candidate scored
    /// <b>below the similarity floor</b> (so it was rejected). Null when no
    /// candidate came close at all. Surfaced in the message so a below-floor 400
    /// explains itself ("nearest was 'X' at 0.21, below the 0.30 floor").
    /// </summary>
    public string? BestCandidate { get; }

    /// <summary>The Jaccard score of <see cref="BestCandidate"/> (0 when none).</summary>
    public double BestScore { get; }

    public UnknownModelException(
        string requestedModel,
        string resolvedModel,
        RouteLocation? appliedLocation,
        int? appliedLocationIndex,
        IReadOnlyList<string> knownProfiles,
        string? bestCandidate = null,
        double bestScore = 0.0)
        : base(BuildMessage(requestedModel, resolvedModel, appliedLocation, appliedLocationIndex, knownProfiles, bestCandidate, bestScore))
    {
        RequestedModel = requestedModel;
        ResolvedModel = resolvedModel;
        AppliedLocation = appliedLocation;
        AppliedLocationIndex = appliedLocationIndex;
        KnownProfiles = knownProfiles;
        BestCandidate = bestCandidate;
        BestScore = bestScore;
    }

    private static string BuildMessage(
        string requestedModel,
        string resolvedModel,
        RouteLocation? loc,
        int? locIndex,
        IReadOnlyList<string> known,
        string? bestCandidate,
        double bestScore)
    {
        var knownList = known.Count == 0 ? "(catalog is empty)" : string.Join(", ", known);

        // When a fuzzy match was attempted but the best candidate fell below the
        // floor, say so — it tells the operator whether this is "close but rejected"
        // (probably a real new model just under the bar → add a remap) vs "nothing
        // close" (probably a typo).
        var nearNote = bestCandidate is null
            ? ""
            : $"\nNearest known model was '{bestCandidate}' (similarity {bestScore:F2}), below the "
              + $"{ModelNameMatcher.DefaultMinSimilarity:F2} fuzzy-match floor — so the bridge did "
              + "not borrow its contract automatically.";

        if (loc is null)
        {
            return $"[copilot-bridge] No profile for model '{resolvedModel}'. "
                + "The bridge keeps a per-model profile (Pipeline/Routing/ModelProfileCatalog.cs) "
                + "describing what Copilot's variant of that model accepts on the wire. It best-effort "
                + "fuzzy-matches an unknown id to the nearest known model, but this one was too "
                + "dissimilar to match safely." + nearNote + " Two ways to fix:\n"
                + $"  1. If '{resolvedModel}' exists on Copilot but the bridge's catalog is out of "
                + "date, add a routing location in appsettings.json that remaps it to a known model:\n"
                + $"       {{ \"When\": {{ \"Model\": \"{resolvedModel}\" }}, "
                + "\"Use\": { \"Model\": \"<a known profile id>\" } }\n"
                + "     and please open an issue so we can add a real profile.\n"
                + $"  2. If '{resolvedModel}' is a typo, fix the client's model name.\n"
                + $"Known profiles: {knownList}";
        }

        var location = locIndex is { } i ? $"Routing.Locations[{i}]" : "Routing.Locations[?]";
        var note = loc.Note ?? "(no Note)";
        return $"[copilot-bridge] No profile for model '{resolvedModel}', which was selected by a "
            + $"user routing location (appsettings.json {location}: {note}). The rewrite target "
            + $"'{resolvedModel}' is not in the bridge's profile catalog and was too dissimilar to "
            + "any known model to fuzzy-match safely." + nearNote + " Fix either by:\n"
            + "  1. Changing this location's Use.Model to a known profile id.\n"
            + "  2. Adding another location (earlier in the list) that further remaps the resolved id.\n"
            + $"Known profiles: {knownList}";
    }
}
