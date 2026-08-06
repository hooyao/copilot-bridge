using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Live contract proof for Responses structured multimodal function output. This is
/// intentionally a two-turn function loop rather than an ordinary top-level vision
/// probe: the model first emits a real call, then receives a generated image through
/// <c>function_call_output.output</c> content items. A 200 alone is insufficient;
/// the final answer must identify the image's color.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class MultimodalFunctionOutputProbe
{
    private const string Model = "gpt-5.6-sol";
    private readonly ITestOutputHelper _output;

    public MultimodalFunctionOutputProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Gpt56Sol_StructuredImageFunctionOutput_IsAcceptedAndUnderstood()
    {
        const string first = """
            {
              "model":"gpt-5.6-sol",
              "instructions":"You must call inspect_image exactly once. Do not answer before the tool result.",
              "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Use inspect_image, then answer with only the dominant color."}]}],
              "tools":[{"type":"function","name":"inspect_image","description":"Returns the image to inspect.","parameters":{"type":"object","properties":{},"required":[]},"strict":false}],
              "tool_choice":{"type":"function","name":"inspect_image"},
              "stream":false
            }
            """;

        using var client = new PlaygroundClient();
        var (firstStatus, firstBody) = await client.TryPostResponsesAsync(first);
        Assert.Equal(System.Net.HttpStatusCode.OK, firstStatus);
        var call = ReadFunctionCall(firstBody);
        Assert.Equal("inspect_image", call.Name);

        var dataUrl = "data:image/png;base64,"
            + Convert.ToBase64String(PngGen.SolidRgbPng(100, 100, 255, 0, 0));
        var second = $$"""
            {
              "model":"{{Model}}",
              "instructions":"Answer with only the dominant color in the returned image.",
              "input":[
                {"type":"message","role":"user","content":[{"type":"input_text","text":"Use inspect_image, then answer with only the dominant color."}]},
                {"type":"function_call","call_id":"{{call.CallId}}","name":"{{call.Name}}","arguments":"{{call.Arguments}}"},
                {"type":"function_call_output","call_id":"{{call.CallId}}","output":[
                  {"type":"input_text","text":"The image returned by the tool follows."},
                  {"type":"input_image","image_url":"{{dataUrl}}"}
                ]}
              ],
              "stream":false
            }
            """;

        var (secondStatus, secondBody) = await client.TryPostResponsesAsync(second, vision: true);
        var answer = ReadOutputText(secondBody).Trim();
        _output.WriteLine($"first={(int)firstStatus} second={(int)secondStatus} answer={answer}");

        Assert.Equal(System.Net.HttpStatusCode.OK, secondStatus);
        Assert.Equal("red", answer, ignoreCase: true);
    }

    private static (string CallId, string Name, string Arguments) ReadFunctionCall(string body)
    {
        using var doc = JsonDocument.Parse(body);
        foreach (var item in doc.RootElement.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "function_call") continue;
            return (
                item.GetProperty("call_id").GetString()!,
                item.GetProperty("name").GetString()!,
                item.GetProperty("arguments").GetString()!);
        }
        throw new Xunit.Sdk.XunitException("First response did not contain a function_call: " + body);
    }

    private static string ReadOutputText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var parts = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("output").EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("text", out var text)) parts.Add(text.GetString() ?? "");
        }
        return string.Concat(parts);
    }
}
