using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>BetaRequestDocumentBlock.source</c> union. Claude Code typically
/// sends base64 PDFs; the other forms are kept for SDK fidelity.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Base64PdfSource), "base64")]
[JsonDerivedType(typeof(PlainTextSource), "text")]
[JsonDerivedType(typeof(ContentBlockSource), "content")]
[JsonDerivedType(typeof(UrlPdfSource), "url")]
[JsonDerivedType(typeof(FileDocumentSource), "file")]
internal abstract record DocumentSource;

internal sealed record Base64PdfSource : DocumentSource
{
    public required string Data { get; init; }
    public string MediaType { get; init; } = "application/pdf";
}

internal sealed record PlainTextSource : DocumentSource
{
    public required string Data { get; init; }
    public string MediaType { get; init; } = "text/plain";
}

/// <summary>
/// Mirrors <c>BetaContentBlockSource</c>. Inner content is <c>string</c> or
/// an array of <see cref="TextBlockParam"/>/<see cref="ImageBlockParam"/> — kept
/// raw for now (Claude Code rarely uses this; preprocessing pipeline does not
/// touch it).
/// </summary>
internal sealed record ContentBlockSource : DocumentSource
{
    public JsonElement Content { get; init; }
}

internal sealed record UrlPdfSource : DocumentSource
{
    public required string Url { get; init; }
}

internal sealed record FileDocumentSource : DocumentSource
{
    public required string FileId { get; init; }
}
