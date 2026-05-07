using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaImageBlockParam.source</c> union. Claude Code typically sends
/// base64; URL and file variants are kept for SDK fidelity.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Base64ImageSource), "base64")]
[JsonDerivedType(typeof(UrlImageSource), "url")]
[JsonDerivedType(typeof(FileImageSource), "file")]
internal abstract record ImageSource;

internal sealed record Base64ImageSource : ImageSource
{
    public required string Data { get; init; }
    /// <summary>One of <c>image/jpeg | image/png | image/gif | image/webp</c>.</summary>
    public required string MediaType { get; init; }
}

internal sealed record UrlImageSource : ImageSource
{
    public required string Url { get; init; }
}

internal sealed record FileImageSource : ImageSource
{
    public required string FileId { get; init; }
}
