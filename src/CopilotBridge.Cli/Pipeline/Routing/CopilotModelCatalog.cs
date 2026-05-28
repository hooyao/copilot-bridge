using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Copilot;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// In-memory snapshot of Copilot's <c>/models</c> response, indexed by canonical
/// model id. The bridge consults this to answer:
/// <list type="bullet">
///   <item><b>Does Copilot accept <c>output_config.effort</c> at all on this model?</b>
///         True iff <c>capabilities.supports.reasoning_effort</c> is non-empty.</item>
///   <item><b>Is a specific effort value accepted as-is?</b>
///         Membership check on <c>reasoning_effort</c>.</item>
///   <item><b>Is there a variant-suffixed model that locks the requested effort?</b>
///         e.g. <c>claude-opus-4.7</c> + <c>xhigh</c> → <c>claude-opus-4.7-xhigh</c>.</item>
/// </list>
/// Mutable singleton: starts empty, loaded once at startup via
/// <see cref="LoadFromAsync"/>. Reloading requires a bridge restart because
/// Copilot's model set changes slowly enough that hot-reload isn't worth the
/// complexity. The catalog returns "strip the field" for unknown models so the
/// pre-load behavior is safe — never sends a field Copilot might reject.
/// </summary>
internal sealed class CopilotModelCatalog
{
    private Dictionary<string, ModelEntry> _byId;

    /// <summary>Constructs an empty catalog. Production code calls <see cref="LoadFromAsync"/> at startup.</summary>
    public CopilotModelCatalog()
    {
        _byId = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Test-only constructor: inject specific entries to drive routing decisions deterministically.</summary>
    internal CopilotModelCatalog(IEnumerable<ModelEntry> entries)
    {
        _byId = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _byId[e.Id] = e;
    }

    /// <summary>Fetches <c>/models</c> via <paramref name="client"/> and replaces the snapshot in place.</summary>
    public async ValueTask LoadFromAsync(ICopilotClient client, CancellationToken ct = default)
    {
        var resp = await client.GetModelsAsync(ct);
        var dict = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in resp.Data)
        {
            if (!string.Equals(m.Vendor, "Anthropic", StringComparison.OrdinalIgnoreCase)) continue;

            var efforts = m.Capabilities?.Supports?.ReasoningEffort ?? Array.Empty<string>();
            var supportsV1Messages = m.SupportedEndpoints?.Contains("/v1/messages", StringComparer.OrdinalIgnoreCase) ?? false;

            dict[m.Id] = new ModelEntry(
                Id: m.Id,
                SupportedEfforts: efforts,
                AdaptiveThinking: m.Capabilities?.Supports?.AdaptiveThinking == true,
                MinThinkingBudget: m.Capabilities?.Supports?.MinThinkingBudget,
                MaxThinkingBudget: m.Capabilities?.Supports?.MaxThinkingBudget,
                SupportsV1Messages: supportsV1Messages);
        }
        _byId = dict;
    }

    public ModelEntry? Get(string canonicalId) =>
        _byId.TryGetValue(canonicalId, out var e) ? e : null;

    public IReadOnlyCollection<ModelEntry> All => _byId.Values;

    public int Count => _byId.Count;

    /// <summary>
    /// Given a base model id and a requested effort, decide how to route:
    /// pass-through (effort field accepted), variant rewrite, or strip.
    /// </summary>
    public RoutingDecision DecideEffortRouting(string canonicalId, string effort)
    {
        var info = Get(canonicalId);
        if (info is null)
        {
            // Unknown model — safest is strip.
            return new RoutingDecision(canonicalId, StripEffort: true);
        }

        if (info.SupportedEfforts.Count == 0)
        {
            // Model has no reasoning_effort capability — strip.
            return new RoutingDecision(canonicalId, StripEffort: true);
        }

        foreach (var supported in info.SupportedEfforts)
        {
            if (string.Equals(supported, effort, StringComparison.OrdinalIgnoreCase))
                return new RoutingDecision(canonicalId, StripEffort: false);
        }

        // Effort not accepted by the base model; try variant `{id}-{effort}`.
        var variantId = canonicalId + "-" + effort.ToLowerInvariant();
        if (_byId.ContainsKey(variantId))
        {
            return new RoutingDecision(variantId, StripEffort: true);
        }

        // No variant exists — strip and fall back to the base model.
        return new RoutingDecision(canonicalId, StripEffort: true);
    }
}

/// <summary>Snapshot of one Copilot model's relevant capabilities.</summary>
internal sealed record ModelEntry(
    string Id,
    IReadOnlyList<string> SupportedEfforts,
    bool AdaptiveThinking,
    int? MinThinkingBudget,
    int? MaxThinkingBudget,
    bool SupportsV1Messages);

/// <summary>Result of <see cref="CopilotModelCatalog.DecideEffortRouting"/>.</summary>
internal sealed record RoutingDecision(string Model, bool StripEffort);
