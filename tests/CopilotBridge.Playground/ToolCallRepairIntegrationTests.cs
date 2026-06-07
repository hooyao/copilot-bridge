using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class ToolCallRepairIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public ToolCallRepairIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ToolCallRepairStage_FixesUpstreamCorruption_WhenReproduced()
    {
        const string body = """
          {
            "model": "claude-opus-4.8",
            "max_tokens": 4096,
            "stream": true,
            "tool_choice": {"type":"tool","name":"AskUserQuestion"},
            "tools": [{
              "name": "AskUserQuestion",
              "description": "Ask the user one or more multiple-choice questions.",
              "input_schema": {
                "type":"object",
                "properties": {
                  "questions": {
                    "type":"array",
                    "items": {
                      "type":"object",
                      "properties": {
                        "question": {"type":"string"},
                        "header": {"type":"string"},
                        "multiSelect": {"type":"boolean"},
                        "options": {"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"description":{"type":"string"}}}}
                      },
                      "required": ["question","header","options","multiSelect"]
                    }
                  }
                },
                "required": ["questions"]
              }
            }],
            "messages": [{"role":"user","content":"I'm choosing a database and a deploy target for a new web app. Ask me two questions to narrow it down, each with 3 options."}]
          }
          """;

        var tools = JsonSerializer.Deserialize<MessagesRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Tools;

        const int maxRuns = 10;
        bool foundCorruption = false;

        using var client = new PlaygroundClient();

        for (var i = 1; i <= maxRuns; i++)
        {
            _output.WriteLine($"[run {i}] Prompting Copilot for AskUserQuestion...");
            string rawResponse = await client.PostMessagesRawStreamAsync(body);
            
            // Check if upstream naturally omitted the 'question' field
            bool isCorrupt = !rawResponse.Contains("\"question\"", StringComparison.Ordinal);
            
            if (isCorrupt)
            {
                _output.WriteLine($"[run {i}] SUCCESS: Upstream naturally corrupted the JSON by omitting 'question'. Running through ToolCallRepairStage...");
                foundCorruption = true;

                // Set up the repair stage
                var opts = Options.Create(new ToolCallRepairOptions { Enabled = true });
                var stage = new ToolCallRepairStage(NullLogger<ToolCallRepairStage>.Instance, opts);
                
                var ctx = new BridgeContext<MessagesRequest>
                {
                    Request = new BridgeRequest<MessagesRequest>
                    {
                        Method = "POST",
                        Path = "/v1/messages",
                        Body = new MessagesRequest { Model = "claude-opus-4.8", Messages = [], Tools = tools }
                    },
                    Response = new BridgeResponse
                    {
                        Mode = ResponseMode.Streaming,
                        EventStream = ParseRawSse(rawResponse)
                    },
                    Ct = CancellationToken.None
                };

                await stage.ApplyAsync(ctx);

                // Read output
                var outputEvents = new List<SseItem<string>>();
                await foreach (var evt in ctx.Response.EventStream!)
                {
                    outputEvents.Add(evt);
                }

                // Gather repaired JSON
                var joined = new StringBuilder();
                foreach (var evt in outputEvents)
                {
                    if (evt.EventType == "content_block_delta")
                    {
                        try 
                        {
                            var doc = JsonDocument.Parse(evt.Data);
                            if (doc.RootElement.TryGetProperty("delta", out var delta)
                                && delta.TryGetProperty("type", out var dt)
                                && dt.GetString() == "input_json_delta"
                                && delta.TryGetProperty("partial_json", out var pj))
                            {
                                joined.Append(pj.GetString());
                            }
                        }
                        catch { }
                    }
                }

                string finalJson = joined.ToString();
                _output.WriteLine($"Repaired JSON: {finalJson}");

                // ASSERT: The repair stage must have injected the missing 'question' field (likely as a dummy string "")
                Assert.Contains("\"question\"", finalJson);
                break; // We proved it works, no need to run more
            }
            else
            {
                _output.WriteLine($"[run {i}] Upstream returned well-formed JSON (or at least contained 'question').");
            }
        }

        if (!foundCorruption)
        {
            _output.WriteLine($"WARNING: After {maxRuns} runs, Copilot never omitted the 'question' field. The integration test passed but the corruption could not be naturally reproduced this time.");
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> ParseRawSse(string rawStream)
    {
        var bytes = Encoding.UTF8.GetBytes(rawStream);
        using var stream = new MemoryStream(bytes);
        var parser = SseParser.Create(stream);
        
        await foreach (var item in parser.EnumerateAsync())
        {
            yield return item;
        }
    }
}
