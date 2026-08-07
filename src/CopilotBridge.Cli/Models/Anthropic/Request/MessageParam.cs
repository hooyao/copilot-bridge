using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Converters;
using CopilotBridge.Cli.Models.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaMessageParam</c>. <c>content</c> on the wire is a string-or-array
/// union; <see cref="ContentBlockParamListConverter"/> normalizes both shapes to
/// the array form for internal use.
/// </summary>
internal sealed record MessageParam
{
    /// <summary><c>"user"</c> or <c>"assistant"</c>.</summary>
    public required string Role { get; init; }

    [JsonConverter(typeof(ContentBlockParamListConverter))]
    public required IReadOnlyList<ContentBlockParam> Content { get; init; }

    /// <summary>
    /// Message-level provider-native data that the Anthropic-shaped IR cannot type.
    /// A source pushes only its own namespace; a destination pulls only the namespace
    /// it owns. Null for ordinary Claude Code messages, so the hot path is unchanged.
    /// </summary>
    public ProviderExtensions? ProviderExtensions { get; init; }
}

/// <summary>Role string constants.</summary>
internal static class Role
{
    public const string User = "user";
    public const string Assistant = "assistant";
}
