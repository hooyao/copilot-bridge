using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Image / vision path coverage (change-3 review Gap 10). The data-URL parsing in
/// T1's <c>MapImage</c> and the re-emission in T2 have real edge cases (the
/// <c>;</c>/<c>,</c> split, the non-data-URL fallback) that no offline fixture or
/// E1 prompt exercised. Also asserts the <c>vision</c> flag (which drives the
/// <c>Copilot-Vision-Request</c> header) is set when an image is present.
/// </summary>
public class CodexImageTests
{
    private static readonly CodexModelProfileCatalog Profiles = new();

    private static string ImageRequest(string imageUrl) =>
        "{\"model\":\"gpt-5.3-codex\",\"instructions\":\"x\","
        + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":["
        + "{\"type\":\"input_image\",\"image_url\":\"" + imageUrl + "\"},"
        + "{\"type\":\"input_text\",\"text\":\"what is this?\"}]}],"
        + "\"stream\":true,\"store\":false}";

    [Fact]
    public void DataUrlImage_ParsesToBase64Source_InIr()
    {
        const string dataUrl = "data:image/png;base64,iVBORw0KGgoAAAANS";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(ImageRequest(dataUrl)));

        var image = ir.Messages
            .SelectMany(m => m.Content)
            .OfType<ImageBlockParam>()
            .Single();
        var src = Assert.IsType<Base64ImageSource>(image.Source);
        Assert.Equal("image/png", src.MediaType);
        Assert.Equal("iVBORw0KGgoAAAANS", src.Data);
    }

    [Fact]
    public void DataUrlImage_RoundTripsThroughT1T2_AndSetsVision()
    {
        const string dataUrl = "data:image/jpeg;base64,/9j/4AAQSkZJRg";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(ImageRequest(dataUrl)));

        var (bytes, vision) = ResponsesRequestBuilder.Build(ir, Profiles);
        Assert.True(vision, "an input_image must set the vision flag (→ Copilot-Vision-Request)");

        var emitted = JsonNode.Parse(bytes)!.AsObject();
        var imagePart = emitted["input"]!.AsArray()
            .SelectMany(i => i!["content"]?.AsArray() ?? new JsonArray())
            .FirstOrDefault(p => p!["type"]?.GetValue<string>() == "input_image");
        Assert.NotNull(imagePart);
        // The data URL is reconstructed identically from the base64 source.
        Assert.Equal(dataUrl, imagePart!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void NonDataUrlImage_FallsBackToUrlSource_NoThrow()
    {
        const string httpUrl = "https://example.com/cat.png";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(ImageRequest(httpUrl)));

        var image = ir.Messages.SelectMany(m => m.Content).OfType<ImageBlockParam>().Single();
        var src = Assert.IsType<UrlImageSource>(image.Source);
        Assert.Equal(httpUrl, src.Url);

        // And it round-trips back to the same image_url.
        var (bytes, vision) = ResponsesRequestBuilder.Build(ir, Profiles);
        Assert.True(vision);
        var emitted = JsonNode.Parse(bytes)!.AsObject();
        var imagePart = emitted["input"]!.AsArray()
            .SelectMany(i => i!["content"]?.AsArray() ?? new JsonArray())
            .First(p => p!["type"]?.GetValue<string>() == "input_image");
        Assert.Equal(httpUrl, imagePart!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedDataUrl_NoSemicolon_FallsBackToUrlSource()
    {
        // "data:image/png,XXXX" — no ';' before the comma → the base64 split guard
        // (semi < comma) fails → defined fallback to a URL source (carried whole),
        // not a crash.
        const string malformed = "data:image/png,rawbytes";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(ImageRequest(malformed)));

        var image = ir.Messages.SelectMany(m => m.Content).OfType<ImageBlockParam>().Single();
        var src = Assert.IsType<UrlImageSource>(image.Source);
        Assert.Equal(malformed, src.Url);
    }

    [Fact]
    public void NoImage_VisionFlagFalse()
    {
        const string textOnly = "{\"model\":\"gpt-5.3-codex\",\"instructions\":\"x\","
            + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\"}]}],"
            + "\"stream\":true,\"store\":false}";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(textOnly));
        var (_, vision) = ResponsesRequestBuilder.Build(ir, Profiles);
        Assert.False(vision);
    }
}
