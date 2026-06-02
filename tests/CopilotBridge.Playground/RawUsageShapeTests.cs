using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Probes the raw shape of the <c>usage</c> object Copilot returns from
/// <c>/v1/messages</c> — both the non-streaming response body and the streaming
/// <c>message_start</c> / <c>message_delta</c> events. Section §4.2 of
/// <c>docs/copilot-api-research.md</c> previously inferred the non-streaming
/// shape from streaming captures; this test verifies it empirically and dumps
/// every key under <c>usage</c> so the docs can stay accurate.
///
/// Run with <c>dotnet test --filter RawUsageShape --logger "console;verbosity=detailed"</c>
/// to see the captured payloads.
/// </summary>
[Trait("Category", "Integration")]
public class RawUsageShapeTests
{
    private const string MinimalPayload = """
      {
        "model": "claude-sonnet-4.6",
        "messages": [{ "role": "user", "content": "Reply with the single word: ok" }],
        "max_tokens": 16
      }
      """;

    private const string MinimalStreamPayload = """
      {
        "model": "claude-sonnet-4.6",
        "messages": [{ "role": "user", "content": "Reply with the single word: ok" }],
        "max_tokens": 16,
        "stream": true
      }
      """;

    private readonly ITestOutputHelper _output;

    public RawUsageShapeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task NonStreaming_DumpsUsageObject()
    {
        using var client = new PlaygroundClient();
        var body = await client.PostMessagesAsync(MinimalPayload);

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("usage", out var usage),
            "Non-streaming response must contain a top-level 'usage' object.");

        var rendered = JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true });
        _output.WriteLine("usage (non-streaming):");
        _output.WriteLine(rendered);

        // Minimum-viable invariants — anything beyond these is documented in the
        // research doc rather than asserted, because Copilot adds/removes
        // extension fields over time.
        Assert.True(usage.TryGetProperty("input_tokens", out var inp) && inp.GetInt32() > 0,
            "usage.input_tokens must be present and positive.");
        Assert.True(usage.TryGetProperty("output_tokens", out var outp) && outp.GetInt32() > 0,
            "usage.output_tokens must be present and positive.");

        // For human review: list every key under usage so doc updates can be
        // checked against a single dump rather than scrolling JSON.
        _output.WriteLine("");
        _output.WriteLine("usage keys (non-streaming):");
        foreach (var prop in usage.EnumerateObject())
        {
            _output.WriteLine($"  - {prop.Name}: {prop.Value.ValueKind}");
        }
    }

    [Fact]
    public async Task Streaming_DumpsUsageOnMessageStartAndMessageDelta()
    {
        var startUsage = (string?)null;
        var deltaUsage = (string?)null;

        using (var client = new PlaygroundClient())
        {
            await foreach (var item in client.PostMessagesStreamAsync(MinimalStreamPayload))
            {
                if (item.EventType == "message_start" && startUsage is null)
                {
                    startUsage = ExtractUsageBlock(item.Data);
                }
                else if (item.EventType == "message_delta" && deltaUsage is null)
                {
                    deltaUsage = ExtractUsageBlock(item.Data);
                }
            }
        }

        Assert.NotNull(startUsage);
        Assert.NotNull(deltaUsage);

        _output.WriteLine("usage (message_start):");
        _output.WriteLine(startUsage);
        _output.WriteLine("");
        _output.WriteLine("usage (message_delta):");
        _output.WriteLine(deltaUsage);
    }

    private static string? ExtractUsageBlock(string sseData)
    {
        using var doc = JsonDocument.Parse(sseData);
        // message_start nests usage under .message.usage; message_delta puts it at .usage.
        if (doc.RootElement.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("usage", out var nestedUsage))
        {
            return JsonSerializer.Serialize(nestedUsage, new JsonSerializerOptions { WriteIndented = true });
        }
        if (doc.RootElement.TryGetProperty("usage", out var flatUsage))
        {
            return JsonSerializer.Serialize(flatUsage, new JsonSerializerOptions { WriteIndented = true });
        }
        return null;
    }
}
