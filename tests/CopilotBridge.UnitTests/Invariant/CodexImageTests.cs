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

        var (bytes, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
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
        var (bytes, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
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
    public void ClaudeToolResult_TextImageText_PreservesStructuredOutputAndSetsVision()
    {
        const string callId = "toolu_image_contract";
        const string pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=";
        var ir = new MessagesRequest
        {
            Model = "gpt-5.6-sol",
            Messages =
            [
                new MessageParam
                {
                    Role = Role.Assistant,
                    Content =
                    [
                        new ToolUseBlockParam
                        {
                            Id = callId,
                            Name = "Read",
                            Input = Element("""{"file_path":"solid-red.png"}"""),
                        },
                    ],
                },
                new MessageParam
                {
                    Role = Role.User,
                    Content =
                    [
                        new ToolResultBlockParam
                        {
                            ToolUseId = callId,
                            Content = Element(
                                "[{\"type\":\"text\",\"text\":\"The image follows.\"},"
                                + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\""
                                + pngBase64
                                + "\"}},{\"type\":\"text\",\"text\":\"End of result.\"}]"),
                        },
                    ],
                },
            ],
            Stream = true,
        };

        var (bytes, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
        var emitted = JsonNode.Parse(bytes)!.AsObject();
        var outputItem = emitted["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "function_call_output");
        var output = Assert.IsType<JsonArray>(outputItem!["output"]);

        Assert.Equal(callId, outputItem["call_id"]!.GetValue<string>());
        Assert.Equal(["input_text", "input_image", "input_text"],
            output.Select(part => part!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("The image follows.", output[0]!["text"]!.GetValue<string>());
        Assert.Equal(
            $"data:image/png;base64,{pngBase64}",
            output[1]!["image_url"]!.GetValue<string>());
        Assert.Equal("End of result.", output[2]!["text"]!.GetValue<string>());
        Assert.True(vision, "a structured tool-result input_image must enable Copilot-Vision-Request");
    }

    [Theory]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol-next")]
    public void ClaudeToolResultImage_UnprobedExactModel_UsesStringFallback(string model)
    {
        var ir = ToolResultImageRequest(model,
            "[{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/red.png\"}}]");

        var (bytes, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
        var emitted = JsonNode.Parse(bytes)!.AsObject();
        var output = emitted["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "function_call_output")!["output"];

        Assert.Equal(
            "{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/red.png\"}}",
            output!.GetValue<string>());
        Assert.False(vision);
    }

    [Fact]
    public void ClaudeToolResultImage_UnsupportedSibling_UsesWholeArrayFallback()
    {
        const string content = "[{\"type\":\"text\",\"text\":\"before\"},"
            + "{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/red.png\"}},"
            + "{\"type\":\"document\",\"source\":{\"type\":\"text\",\"data\":\"do not drop\"}}]";
        var ir = ToolResultImageRequest("gpt-5.6-sol", content);

        var (bytes, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
        var emitted = JsonNode.Parse(bytes)!.AsObject();
        var output = emitted["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "function_call_output")!["output"]!
            .GetValue<string>();

        Assert.Equal(
            "before\n{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://example.com/red.png\"}}"
            + "\n{\"type\":\"document\",\"source\":{\"type\":\"text\",\"data\":\"do not drop\"}}",
            output);
        Assert.False(vision);
    }

    [Fact]
    public void NoImage_VisionFlagFalse()
    {
        const string textOnly = "{\"model\":\"gpt-5.3-codex\",\"instructions\":\"x\","
            + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\"}]}],"
            + "\"stream\":true,\"store\":false}";
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(textOnly));
        var (_, vision, _) = ResponsesRequestBuilder.Build(ir, Profiles);
        Assert.False(vision);
    }

    /// <summary>
    /// The SAME tool-result JSON must be treated differently based on what the IR
    /// itself says, never on who the caller thinks the client is: a Codex source marks
    /// its output opaque at T1, so T2 re-emits it verbatim; a Claude tool_result carries
    /// no such mark, so T2 is free to read its blocks. This is the layering guard — if
    /// someone reintroduces a source-client parameter on the builder, the two halves of
    /// this test can no longer both hold.
    /// </summary>
    [Fact]
    public void IdenticalJson_OpaqueWhenSourceMarkedIt_InterpretedOtherwise()
    {
        const string anthropicShapedArray =
            "[{\"type\":\"text\",\"text\":\"see image\"},"
            + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAAA\"}}]";

        // Codex source: T1 stamps the opaque marker, so the array survives verbatim.
        var codexIr = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(
            "{\"model\":\"gpt-5.6-sol\",\"input\":["
            + "{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"t\",\"arguments\":\"{}\"},"
            + "{\"type\":\"function_call_output\",\"call_id\":\"c1\",\"output\":" + anthropicShapedArray + "}"
            + "],\"stream\":true}"));
        var (codexBytes, codexVision, _) = ResponsesRequestBuilder.Build(codexIr, Profiles);
        var codexOutput = JsonNode.Parse(codexBytes)!.AsObject()["input"]!.AsArray()
            .Single(i => i!["type"]!.GetValue<string>() == "function_call_output")!["output"];
        Assert.IsNotType<JsonArray>(codexOutput);
        Assert.False(codexVision);

        // Claude source: no marker, so the same JSON is read as Anthropic blocks.
        var claudeIr = ToolResultImageRequest("gpt-5.6-sol", anthropicShapedArray);
        var (claudeBytes, claudeVision, _) = ResponsesRequestBuilder.Build(claudeIr, Profiles);
        var claudeOutput = JsonNode.Parse(claudeBytes)!.AsObject()["input"]!.AsArray()
            .Single(i => i!["type"]!.GetValue<string>() == "function_call_output")!["output"];
        Assert.Equal(["input_text", "input_image"],
            Assert.IsType<JsonArray>(claudeOutput).Select(p => p!["type"]!.GetValue<string>()).ToArray());
        Assert.True(claudeVision);
    }

    private static MessagesRequest ToolResultImageRequest(string model, string content) =>
        new()
        {
            Model = model,
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content =
                    [
                        new ToolResultBlockParam
                        {
                            ToolUseId = "toolu_image_fallback",
                            Content = Element(content),
                        },
                    ],
                },
            ],
            Stream = true,
        };

    private static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
