using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Paired live calibration for Responses admission accounting. Every row builds
/// canonical T2 exactly once and sends that identical byte array to both the
/// Anthropic-named count endpoint and the Responses usage oracle. The SHA-256
/// printed beside each row rejects accidental cross-body comparisons.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class ResponsesCountCalibrationProbe
{
    private readonly ITestOutputHelper _output;

    public ResponsesCountCalibrationProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Gpt56Sol_ExactBodyCalibrationCorpus()
    {
        var cases = new[]
        {
            new CalibrationCase("minimal", Minimal()),
            new CalibrationCase("long-history", LongHistory()),
            new CalibrationCase("tool-heavy", ToolHeavy()),
            new CalibrationCase("production-shape-near-boundary", ProductionShape()),
        };

        using var client = new PlaygroundClient();
        foreach (var item in cases)
        {
            var built = ResponsesRequestBuilder.Build(
                item.Request, new CodexModelProfileCatalog()).Body;
            var body = Encoding.UTF8.GetString(built);
            var sha256 = Convert.ToHexString(SHA256.HashData(built)).ToLowerInvariant();

            var (countStatus, countBody) =
                await client.TryPostCountTokensAsync(body);
            var (responseStatus, responseBody) =
                await client.TryPostResponsesAsync(body);

            var rawCount = ReadInt(countBody, "input_tokens");
            var usage = ReadInt(responseBody, "usage", "input_tokens");
            var delta = usage - rawCount;
            var ratio = rawCount == 0 ? 0 : (double)usage / rawCount;

            _output.WriteLine(
                $"{item.Id}: bytes={built.Length} sha256={sha256} "
                + $"count={(int)countStatus}/{rawCount} "
                + $"responses={(int)responseStatus}/{usage} "
                + $"delta={delta} ratio={ratio:F9}");

            Assert.Equal(System.Net.HttpStatusCode.OK, countStatus);
            Assert.Equal(System.Net.HttpStatusCode.OK, responseStatus);
            Assert.True(rawCount >= 0);
            Assert.True(usage >= 0);
            var estimate = new ResponsesAdmissionEstimator(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponsesAdmissionEstimator>.Instance)
                .Estimate(item.Request.Model, rawCount);
            Assert.True(
                estimate.InputTokens >= usage,
                $"{item.Id} estimate {estimate.InputTokens} under-counted paired usage {usage} "
                + $"for T2 SHA-256 {sha256}");
            if (item.Id == "minimal")
                Assert.InRange(estimate.InputTokens, usage, 128);
        }
    }

    private static int ReadInt(string json, params string[] path)
    {
        using var doc = JsonDocument.Parse(json);
        var current = doc.RootElement;
        foreach (var name in path) current = current.GetProperty(name);
        return current.GetInt32();
    }

    private static MessagesRequest Minimal() => Request(
        [Message(Role.User, new TextBlockParam { Text = "Reply with CALIBRATION_OK only." })]);

    private static MessagesRequest LongHistory()
    {
        var messages = new List<MessageParam>(400);
        for (var i = 0; i < 400; i++)
        {
            var role = i % 2 == 0 ? Role.User : Role.Assistant;
            messages.Add(Message(
                role,
                new TextBlockParam
                {
                    Text = $"Synthetic history row {i:D4}: " + new string((char)('a' + i % 26), 1_000),
                }));
        }
        messages.Add(Message(Role.User,
            new TextBlockParam { Text = "Reply with CALIBRATION_OK only." }));
        return Request(messages);
    }

    private static MessagesRequest ToolHeavy() => Request(
        [Message(Role.User, new TextBlockParam { Text = "Do not call tools. Reply CALIBRATION_OK." })],
        Tools(58));

    private static MessagesRequest ProductionShape()
    {
        const int rounds = 840;
        const int pairCount = 911;
        var messages = new List<MessageParam>(1 + rounds * 2)
        {
            Message(Role.User, new TextBlockParam
            {
                Text = "Sanitized production-shape calibration. Do not call tools; reply CALIBRATION_OK.",
            }),
        };

        var pair = 0;
        for (var round = 0; round < rounds; round++)
        {
            var inRound = round < pairCount - rounds ? 2 : 1;
            var calls = new List<ContentBlockParam>(inRound);
            var results = new List<ContentBlockParam>(inRound);
            for (var j = 0; j < inRound; j++)
            {
                var id = $"toolu_synthetic_{pair:D4}";
                calls.Add(new ToolUseBlockParam
                {
                    Id = id,
                    Name = $"SyntheticTool{pair % 58:D2}",
                    Input = Element($$"""{"index":{{pair}},"kind":"sanitized"}"""),
                });
                results.Add(new ToolResultBlockParam
                {
                    ToolUseId = id,
                    Content = Element(JsonSerializer.Serialize(
                        $"sanitized-result-{pair:D4}-" + new string((char)('a' + pair % 26), 2_700))),
                });
                pair++;
            }
            messages.Add(Message(Role.Assistant, calls.ToArray()));
            messages.Add(Message(Role.User, results.ToArray()));
        }

        Assert.Equal(1_681, messages.Count);
        Assert.Equal(911, pair);
        return Request(messages, Tools(58));
    }

    private static MessagesRequest Request(
        IReadOnlyList<MessageParam> messages,
        IReadOnlyList<Tool>? tools = null) =>
        new()
        {
            Model = "gpt-5.6-sol",
            Messages = messages,
            Tools = tools,
        };

    private static MessageParam Message(
        string role, params ContentBlockParam[] content) =>
        new() { Role = role, Content = content };

    private static IReadOnlyList<Tool> Tools(int count)
    {
        var properties = Element(
            "{\"value\":{\"type\":\"string\"},\"index\":{\"type\":\"integer\"}}");
        return Enumerable.Range(0, count).Select(i => new Tool
        {
            Name = $"SyntheticTool{i:D2}",
            Description = $"Deterministic sanitized calibration tool {i:D2}.",
            InputSchema = new InputSchema
            {
                Type = "object",
                Properties = properties,
                Required = ["value"],
            },
        }).ToArray();
    }

    private static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed record CalibrationCase(string Id, MessagesRequest Request);
}
