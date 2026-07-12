using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies the streamed <c>/v1/messages</c> SSE shape: standard Anthropic events
/// (message_start → content_block_* → message_delta → message_stop) plus the
/// Copilot-added <c>[DONE]</c> terminator the bridge needs to filter.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class StreamingTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task Stream_EmitsAnthropicEventSequence(string model)
    {
        // No thinking — Haiku-4.5 rejects "adaptive" with HTTP 400 even though its
        // capabilities.supports.adaptive_thinking is true. Streaming with thinking is
        // covered by the dedicated thinking experiment (TBD).
        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [
              { "role": "user", "content": "List three prime numbers larger than 100, one per line." }
            ],
            "max_tokens": 256,
            "stream": true
          }
          """;

        var collected = new List<(string EventType, string Data)>();
        using (var client = new PlaygroundClient())
        {
            await foreach (var item in client.PostMessagesStreamAsync(payload))
            {
                collected.Add((item.EventType, item.Data));
            }
        }

        // Required Anthropic event types per the official spec.
        Assert.Contains(collected, e => e.EventType == "message_start");
        Assert.Contains(collected, e => e.EventType == "content_block_start");
        Assert.Contains(collected, e => e.EventType == "content_block_delta");
        Assert.Contains(collected, e => e.EventType == "content_block_stop");
        Assert.Contains(collected, e => e.EventType == "message_delta");
        Assert.Contains(collected, e => e.EventType == "message_stop");

        // Sanity: at least one text_delta payload arrived.
        Assert.Contains(collected, e =>
            e.EventType == "content_block_delta" && e.Data.Contains("\"text_delta\""));
    }

    /// <summary>
    /// Documents that Copilot terminates streams with a non-Anthropic <c>[DONE]</c>
    /// marker. The bridge MUST drop this; Anthropic clients (Claude Code's SDK) JSON.parse
    /// each <c>data:</c> field and <c>[DONE]</c> is not valid JSON. Failing here would mean
    /// Copilot stopped emitting it (good — bridge filter becomes a no-op) or our SSE parser
    /// dropped it (also fine — same outcome for the bridge).
    /// </summary>
    [Fact]
    public async Task Stream_EndsWithCopilotDoneMarker_OrCleanly()
    {
        var payload = """
          {
            "model": "claude-sonnet-4.6",
            "messages": [{ "role": "user", "content": "Reply with the single word: ok" }],
            "max_tokens": 16,
            "stream": true
          }
          """;

        var collected = new List<(string EventType, string Data)>();
        using (var client = new PlaygroundClient())
        {
            await foreach (var item in client.PostMessagesStreamAsync(payload))
            {
                collected.Add((item.EventType, item.Data));
            }
        }

        // Either a [DONE] terminator is present (current behavior we want bridge to filter)
        // or the stream ends cleanly at message_stop (future behavior — also OK).
        var hasDone = collected.Any(e => e.Data.Trim() == "[DONE]");
        var lastIsMessageStop = collected.Last().EventType == "message_stop";
        Assert.True(hasDone || lastIsMessageStop,
            "Stream should end with [DONE] (current Copilot behavior) OR cleanly at message_stop.");
    }
}
