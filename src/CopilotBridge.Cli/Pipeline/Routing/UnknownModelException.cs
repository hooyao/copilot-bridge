namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Thrown by <see cref="Stages.Anthropic.ModelRouterStage"/> when, after
/// normalize + any matching user routing location, the resolved model id has
/// no entry in <see cref="ModelProfileCatalog"/>. The bridge cannot send a
/// request to a model whose wire shape it does not know — guessing produces
/// a silent 400 from Copilot that users cannot diagnose. Better to surface a
/// clear actionable error here.
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

    public UnknownModelException(
        string requestedModel,
        string resolvedModel,
        RouteLocation? appliedLocation,
        int? appliedLocationIndex,
        IReadOnlyList<string> knownProfiles)
        : base(BuildMessage(requestedModel, resolvedModel, appliedLocation, appliedLocationIndex, knownProfiles))
    {
        RequestedModel = requestedModel;
        ResolvedModel = resolvedModel;
        AppliedLocation = appliedLocation;
        AppliedLocationIndex = appliedLocationIndex;
        KnownProfiles = knownProfiles;
    }

    private static string BuildMessage(
        string requestedModel,
        string resolvedModel,
        RouteLocation? loc,
        int? locIndex,
        IReadOnlyList<string> known)
    {
        var knownList = known.Count == 0 ? "(catalog is empty)" : string.Join(", ", known);

        if (loc is null)
        {
            return $"[copilot-bridge] No profile for model '{resolvedModel}'. "
                + "The bridge keeps a per-model profile (Pipeline/Routing/ModelProfileCatalog.cs) "
                + "describing what Copilot's variant of that model accepts on the wire, and refuses "
                + "to forward requests for models it has no profile for — guessing would produce a "
                + "silent 400 from Copilot. Two ways to fix:\n"
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
            + $"'{resolvedModel}' is not in the bridge's profile catalog, so the bridge cannot "
            + "decide how to shape the request body. Fix either by:\n"
            + "  1. Changing this location's Use.Model to a known profile id.\n"
            + "  2. Adding another location (earlier in the list) that further remaps the resolved id.\n"
            + $"Known profiles: {knownList}";
    }
}
