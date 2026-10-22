using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>Captured desktop-style incremental inventory at the real HTTP edge.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCollaborationReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public async Task CapturedCollaborationInventory_ProducesAPlaintextSpawnCallAtNativeEdge(bool? pairedNamedOutput, bool bufferedModelRewrite)
    {
        var source = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY to a valid v4 success-test source.");
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "plaintext-collaboration.json")))!;
        var body = fixture["body"]!.DeepClone();
        if (bufferedModelRewrite)
        {
            body["stream"] = false;
            body["model"] = "gpt-5.6-sol";
        }
        JsonObject? namedOutput = null;
        if (pairedNamedOutput is { } paired)
        {
            var input = body["input"]!.AsArray();
            var position = 1;
            if (paired)
                input.Insert(position++, JsonNode.Parse("""{"type":"function_call","namespace":"collaboration","name":"spawn_agent","call_id":"historical_call","arguments":"{\"task_name\":\"previous_worker\",\"message\":\"Prior diagnostic task\"}"}"""));
            namedOutput = new JsonObject
            {
                ["type"] = "function_call_output", ["name"] = "spawn_agent", ["namespace"] = "collaboration",
                ["output"] = "Historical diagnostic task completed.",
            };
            if (paired) namedOutput["call_id"] = "historical_call";
            input.Insert(position, namedOutput);
        }
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            bufferedModelRewrite ? ServeScenario.Gpt56ToAstra : ServeScenario.Passthrough,
            CredentialSourceDirectory: source,
            CredentialStagingMode: CredentialStagingMode.CustomOAuthVersionFour,
            UseCustomOAuthApp: true,
            PlaintextCollaboration: true));
        var reader = new BridgeLogReader(bridge.TraceDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await http.PostAsync(bridge.BaseUrl + "/codex/responses",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode[] calls;
        if (bufferedModelRewrite)
        {
            var responseBody = JsonNode.Parse(raw)!;
            Assert.Equal("completed", responseBody["status"]!.GetValue<string>());
            Assert.Equal("gpt-5.6-sol", responseBody["model"]!.GetValue<string>());
            calls = responseBody["output"]!.AsArray()
                .Where(item => item?["type"]?.GetValue<string>() == "function_call").Select(item => item!).ToArray();
        }
        else
        {
            Assert.Contains("response.completed", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("response.failed", raw, StringComparison.Ordinal);
            calls = raw.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => JsonNode.Parse(line[6..])!)
                .Where(evt => evt["type"]?.GetValue<string>() == "response.output_item.done")
                .Select(evt => evt["item"]!)
                .Where(item => item["type"]?.GetValue<string>() == "function_call")
                .ToArray();
        }
        var call = Assert.Single(calls);
        Assert.Equal("collaboration", call["namespace"]!.GetValue<string>());
        Assert.Equal("spawn_agent", call["name"]!.GetValue<string>());
        Assert.Empty(call["encrypted_function_args"]!.AsArray());
        Assert.Equal("Reply CAPTURED_PLAINTEXT_TASK_OK.",
            JsonNode.Parse(call["arguments"]!.GetValue<string>())!["message"]!.GetValue<string>());
        if (namedOutput is not null || bufferedModelRewrite)
        {
            BridgeLogEntry? entry = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((entry?.UpstreamBody is null || bufferedModelRewrite && entry.UpstreamResp?["body"] is null)
                && DateTime.UtcNow < deadline)
            {
                entry = reader.ReadNew().SingleOrDefault(value => value.UpstreamBody is not null);
                if (entry?.UpstreamBody is null || bufferedModelRewrite && entry.UpstreamResp?["body"] is null)
                    await Task.Delay(100);
            }
            Assert.NotNull(entry?.UpstreamBody);
            if (namedOutput is not null)
            {
                var projected = entry!.UpstreamBody!["input"]!.AsArray()
                    .Single(item => item?["type"]?.GetValue<string>() == "function_call_output")!;
                Assert.Equal("bridge_plaintext_collaboration", projected["namespace"]!.GetValue<string>());
                Assert.Equal("spawn_agent", projected["name"]!.GetValue<string>());
                Assert.Equal(pairedNamedOutput!.Value, projected.AsObject().ContainsKey("call_id"));
                Assert.True(JsonNode.DeepEquals(namedOutput["output"], projected["output"]));
            }
            if (bufferedModelRewrite)
            {
                Assert.Equal("gpt-6-astra", entry!.UpstreamBody!["model"]!.GetValue<string>());
                var native = entry.UpstreamResp!["body"]!["output"]!.AsArray()
                    .Single(item => item?["call_id"]?.GetValue<string>() == call["call_id"]!.GetValue<string>())!;
                Assert.Equal(native["id"]!.GetValue<string>(), call["id"]!.GetValue<string>());
            }
        }
        output.WriteLine($"[replay] Real additional_tools inventory -> plaintext spawn, complete task preserved; trace={bridge.TraceDir}");
    }
}
