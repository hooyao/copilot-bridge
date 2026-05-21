namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Sanity checks the routes config at startup. Fail-fast: any defect throws
/// before Kestrel binds the port. Better a clear startup error than a silent
/// misbehavior at request time.
/// </summary>
internal static class RoutesValidator
{
    private static readonly HashSet<string> ValidThinking = new(StringComparer.OrdinalIgnoreCase)
        { "enabled", "adaptive", "disabled" };

    public static void Validate(RoutesConfig config)
    {
        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var prefix = $"appsettings.json: Routing.Rules[{i}]";

            // Match must constrain at least one dimension.
            if (rule.Match.InboundModel is null
                && rule.Match.InboundEffort is null
                && rule.Match.InboundThinking is null)
            {
                throw new InvalidOperationException(
                    $"{prefix}: Match has no constraints. At least one of "
                    + "InboundModel / InboundEffort / InboundThinking is required.");
            }

            if (rule.Match.InboundThinking is not null
                && !ValidThinking.Contains(rule.Match.InboundThinking))
            {
                throw new InvalidOperationException(
                    $"{prefix}: Match.InboundThinking='{rule.Match.InboundThinking}' is not valid. "
                    + "Allowed: enabled, adaptive, disabled.");
            }

            if (rule.Rewrite?.Thinking?.Type is { Length: > 0 } rewriteType
                && !ValidThinking.Contains(rewriteType))
            {
                throw new InvalidOperationException(
                    $"{prefix}: Rewrite.Thinking.Type='{rewriteType}' is not valid. "
                    + "Allowed: enabled, adaptive, disabled.");
            }
        }
    }
}
