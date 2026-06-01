using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// JSON-native match tree for routing rules. Two composite operators
/// (<see cref="AllOf"/>, <see cref="AnyOf"/>) plus three leaves (<see cref="Model"/>,
/// <see cref="Effort"/>, <see cref="Header"/>). The shape is designed to
/// be readable in <c>appsettings.json</c> without a custom DSL — every
/// node is a plain POCO bound by <c>Microsoft.Extensions.Configuration</c>.
/// </summary>
/// <remarks>
/// <para><b>Sugar (the 80% case):</b> top-level <see cref="Model"/> /
/// <see cref="Effort"/> / <see cref="Header"/> are implicitly AND-ed
/// together — you don't have to wrap a single-condition match in
/// <see cref="AllOf"/>. So:
/// <code>
/// "When": { "Model": "claude-opus-4.7", "Header": {"Name":"anthropic-beta", "Contains":"context-1m-2025-08-07"} }
/// </code>
/// is short for an <see cref="AllOf"/> of two leaves.</para>
/// <para><b>Composites:</b> use <see cref="AllOf"/> / <see cref="AnyOf"/>
/// arrays when you need nesting:
/// <code>
/// "When": { "AllOf": [
///   { "Model": "claude-opus-4.7" },
///   { "AnyOf": [
///       { "Header": { "Name": "anthropic-beta", "Contains": "context-1m-2025-08-07" } },
///       { "Header": { "Name": "anthropic-beta", "Contains": "context-1m-beta-*" } }
///   ]}
/// ]}
/// </code></para>
/// <para><b>No <c>Not</c> operator</b> — negation is almost always a
/// readability/correctness footgun. Write two locations (one for each
/// branch) instead.</para>
/// <para><b>Empty match = matches everything.</b> Useful as a final
/// catch-all entry; rejected at the top of <see cref="RoutesConfig.Locations"/>
/// because it would shadow everything below.</para>
/// </remarks>
internal sealed class MatchExpression
{
    /// <summary>All children must match. AND.</summary>
    public List<MatchExpression>? AllOf { get; set; }

    /// <summary>At least one child must match. OR.</summary>
    public List<MatchExpression>? AnyOf { get; set; }

    /// <summary>Exact (case-insensitive) match on the canonical model id.</summary>
    public string? Model { get; set; }

    /// <summary>Exact (case-insensitive) match on <c>output_config.effort</c>.</summary>
    public string? Effort { get; set; }

    /// <summary>Match on a single inbound header. <c>Equals</c> or <c>Contains</c> is required.</summary>
    public HeaderMatch? Header { get; set; }

    /// <summary>Evaluate this node against the current request.</summary>
    public bool Matches(BridgeContext<MessagesRequest> ctx)
    {
        // Composites short-circuit. An empty composite is vacuously true (AllOf)
        // or vacuously false (AnyOf); operators authoring an empty AnyOf almost
        // certainly meant the catch-all "no constraint" — but validator rejects
        // empty composites at startup, so we don't have to be clever here.
        if (AllOf is { Count: > 0 } and var all)
        {
            foreach (var child in all)
            {
                if (!child.Matches(ctx)) return false;
            }
        }

        if (AnyOf is { Count: > 0 } and var any)
        {
            var ok = false;
            foreach (var child in any)
            {
                if (child.Matches(ctx)) { ok = true; break; }
            }
            if (!ok) return false;
        }

        if (Model is { Length: > 0 } expectedModel
            && !string.Equals(expectedModel, ctx.Request.Body.Model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Effort is { Length: > 0 } expectedEffort
            && !string.Equals(expectedEffort, ctx.Request.Body.OutputConfig?.Effort, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Header is not null && !HeaderMatches(Header, ctx))
        {
            return false;
        }

        return true;
    }

    private static bool HeaderMatches(HeaderMatch h, BridgeContext<MessagesRequest> ctx)
    {
        // anthropic-beta is special: the inbound representation is the parsed
        // token set in ctx.InboundBetas, not the raw header string. Routing
        // semantically asks "does the client carry this beta?", not "does
        // the raw header equal this string", so we match against the parsed
        // set for `anthropic-beta` and against ctx.Request.Headers otherwise.
        if (string.Equals(h.Name, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
        {
            if (h.Eq is { Length: > 0 } eqToken)
                return ctx.InboundBetas.Contains(eqToken);
            if (h.Contains is { Length: > 0 } containsToken)
                return ContainsToken(ctx.InboundBetas, containsToken);
            return false;
        }

        if (!ctx.Request.Headers.TryGetValue(h.Name, out var value)) return false;
        if (h.Eq is { Length: > 0 } eq)
            return string.Equals(eq, value, StringComparison.OrdinalIgnoreCase);
        if (h.Contains is { Length: > 0 } sub)
            return value.Contains(sub, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>Token match against a parsed comma-set. Supports trailing <c>*</c> wildcard.</summary>
    private static bool ContainsToken(IReadOnlyCollection<string> tokens, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            foreach (var t in tokens)
            {
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        foreach (var t in tokens)
        {
            if (string.Equals(t, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>One header condition. Exactly one of <see cref="Eq"/> / <see cref="Contains"/>.</summary>
internal sealed class HeaderMatch
{
    /// <summary>HTTP header name (case-insensitive lookup).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Full-value equality (case-insensitive). For <c>anthropic-beta</c> this
    /// means "the inbound beta set contains this exact token" — the bridge
    /// doesn't compare the raw comma-joined string, since token order isn't
    /// part of the protocol. (Named <c>Eq</c> rather than <c>Equals</c> to
    /// avoid colliding with <see cref="object.Equals(object?)"/>; both the
    /// C# field and the JSON key use <c>Eq</c>.)
    /// </summary>
    public string? Eq { get; set; }

    /// <summary>
    /// Substring (or token-containment for <c>anthropic-beta</c>) match,
    /// case-insensitive. For <c>anthropic-beta</c>, the value is one token
    /// and may end with <c>*</c> for prefix matching
    /// (e.g. <c>context-1m-*</c>).
    /// </summary>
    public string? Contains { get; set; }
}
