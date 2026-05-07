namespace CopilotBridge.Cli.Models.Anthropic.CountTokens;

/// <summary>
/// Mirrors <c>BetaMessageTokensCount</c>. M1 placeholder: always returns
/// <c>{ input_tokens: 1 }</c> so Claude Code's <c>POST /v1/messages/count_tokens</c>
/// preflight doesn't 404. M3 will compute a real estimate.
/// </summary>
internal sealed record CountTokensResponse
{
    public required int InputTokens { get; init; }
}
