using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// A Responses turn must retain per-response identity at the Anthropic edge.
/// Claude Code groups API rounds by assistant message id when deciding whether
/// reactive compaction has enough history; a process-wide constant collapses
/// every real round into one and makes prompt-too-long recovery impossible.
/// </summary>
public sealed class ResponsesMessageIdentityTests
{
    [Fact]
    public void DistinctResponses_ProduceDistinctStableAnthropicMessageIds()
    {
        var first = MessageId("response-alpha");
        var firstReplay = MessageId("response-alpha");
        var second = MessageId("response-beta");

        Assert.StartsWith("msg_", first);
        Assert.Equal(first, firstReplay);
        Assert.NotEqual(first, second);
    }

    private static string MessageId(string responseId)
    {
        var translator = new ResponsesToAnthropicStream("gpt-5.6-sol");
        var evt = new SseItem<string>(
            JsonSerializer.Serialize(new
            {
                type = "response.created",
                response = new { id = responseId, status = "in_progress" },
            }),
            "response.created");
        var messageStart = Assert.Single(translator.Translate(evt));
        using var doc = JsonDocument.Parse(messageStart.Data);
        return doc.RootElement
            .GetProperty("message")
            .GetProperty("id")
            .GetString()!;
    }
}
