namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaMetadata</c>. Caller-supplied identifier hint for abuse detection.
/// </summary>
internal sealed record Metadata
{
    public string? UserId { get; init; }
}
