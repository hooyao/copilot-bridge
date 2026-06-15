using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies image content blocks round-trip through Copilot's <c>/v1/messages</c>
/// when accompanied by the <c>Copilot-Vision-Request: true</c> header. We don't
/// assert on the model's interpretation of the image — only that the request
/// succeeds and produces a text response. Wire-format compatibility is what the
/// bridge needs.
/// </summary>
[Trait("Category", "Integration")]
public class VisionTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    public async Task ImageContentBlock_RoundTripsToTextResponse(string model)
    {
        // 100×100 red PNG. Anthropic's documented vision pipeline rejects very small
        // images ("could not process image"); a few-thousand-byte PNG is large enough.
        var pngBase64 = Convert.ToBase64String(PngGen.SolidRgbPng(100, 100, 255, 0, 0));

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/png",
                                ["data"] = pngBase64,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Briefly describe what you see. One sentence.",
                        },
                    },
                },
            },
            ["max_tokens"] = 64,
        };

        using var client = new PlaygroundClient();
        var responseBody = await client.PostMessagesAsync(payload.ToJsonString(), vision: true);
        var response = JsonNode.Parse(responseBody)!;

        Assert.Equal("end_turn", response["stop_reason"]?.GetValue<string>());

        var textBlock = response["content"]!.AsArray()
            .FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text");
        Assert.NotNull(textBlock);
        Assert.False(string.IsNullOrWhiteSpace(textBlock!["text"]!.GetValue<string>()));
    }
}
