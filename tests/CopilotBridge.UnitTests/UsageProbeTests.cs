using System.Text;
using CopilotBridge.Cli.Models;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins the three usage-extraction paths the bridge exercises:
/// non-streaming Anthropic body, streaming <c>message_delta</c> SSE event,
/// and <c>count_tokens</c> response. Empty bodies / malformed JSON leave
/// the snapshot intact instead of throwing — the audit pipeline already
/// captures unparseable payloads.
/// </summary>
public class UsageProbeTests
{
    [Fact]
    public void NonStreamingBody_ExtractsAllFields()
    {
        const string body = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-4.7",
          "content": [{"type":"text","text":"hi"}],
          "usage": {
            "input_tokens": 1024,
            "output_tokens": 512,
            "cache_read_input_tokens": 100,
            "cache_creation_input_tokens": 50
          }
        }
        """;
        var snap = new UsageSnapshot();
        UsageProbe.TryReadBuffered(Encoding.UTF8.GetBytes(body), snap);

        Assert.Equal(1024, snap.InputTokens);
        Assert.Equal(512, snap.OutputTokens);
        Assert.Equal(100, snap.CacheReadInputTokens);
        Assert.Equal(50, snap.CacheCreationInputTokens);
    }

    [Fact]
    public void CountTokensBody_ExtractsInputTokens()
    {
        var snap = new UsageSnapshot();
        UsageProbe.TryReadCountTokens(Encoding.UTF8.GetBytes("""{"input_tokens": 4096}"""), snap);
        Assert.Equal(4096, snap.InputTokens);
    }

    [Fact]
    public void MessageStartEvent_SeedsSnapshot()
    {
        const string data = """
        {
          "type": "message_start",
          "message": {
            "id": "msg_x",
            "model": "claude-opus-4.7",
            "usage": {
              "input_tokens": 600,
              "output_tokens": 0,
              "cache_read_input_tokens": 200
            }
          }
        }
        """;
        var snap = new UsageSnapshot();
        UsageProbe.TryUpdateFromStreamEvent("message_start", data, snap);

        Assert.Equal(600, snap.InputTokens);
        Assert.Equal(0, snap.OutputTokens);
        Assert.Equal(200, snap.CacheReadInputTokens);
    }

    [Fact]
    public void MessageDeltaEvent_OverwritesOutputTokensCumulatively()
    {
        var snap = new UsageSnapshot();
        // Seed with message_start.
        UsageProbe.TryUpdateFromStreamEvent("message_start",
            """{"type":"message_start","message":{"usage":{"input_tokens":500,"output_tokens":0}}}""",
            snap);
        // First delta.
        UsageProbe.TryUpdateFromStreamEvent("message_delta",
            """{"type":"message_delta","delta":{"stop_reason":null},"usage":{"output_tokens":10}}""",
            snap);
        Assert.Equal(10, snap.OutputTokens);

        // Second delta — Anthropic emits cumulative values, so output_tokens
        // overwrites (it does NOT sum). input_tokens not present this time
        // and should stay at the seeded value (we don't clobber on missing).
        UsageProbe.TryUpdateFromStreamEvent("message_delta",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}""",
            snap);
        Assert.Equal(42, snap.OutputTokens);
        Assert.Equal(500, snap.InputTokens);
    }

    [Fact]
    public void UnrelatedEvent_DoesNotTouchSnapshot()
    {
        var snap = new UsageSnapshot { InputTokens = 100 };
        UsageProbe.TryUpdateFromStreamEvent("content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}""",
            snap);
        Assert.Equal(100, snap.InputTokens);
        Assert.Null(snap.OutputTokens);
    }

    [Fact]
    public void EmptyBody_LeavesSnapshotUntouched()
    {
        var snap = new UsageSnapshot { InputTokens = 1 };
        UsageProbe.TryReadBuffered(Array.Empty<byte>(), snap);
        Assert.Equal(1, snap.InputTokens);
    }

    [Fact]
    public void MalformedJson_LeavesSnapshotUntouched()
    {
        var snap = new UsageSnapshot();
        UsageProbe.TryReadBuffered(Encoding.UTF8.GetBytes("not json"), snap);
        Assert.Null(snap.InputTokens);
    }

    [Fact]
    public void SnapshotDisplay_RendersAllAndCache()
    {
        var snap = new UsageSnapshot
        {
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadInputTokens = 25,
            CacheCreationInputTokens = 10,
        };
        var display = snap.Display;
        Assert.Contains("in:100", display);
        Assert.Contains("out:50", display);
        Assert.Contains("cache_read:25", display);
        Assert.Contains("cache_creation:10", display);
    }

    [Fact]
    public void SnapshotDisplay_NothingSet_RendersNonePlaceholder()
    {
        Assert.Equal("(none)", new UsageSnapshot().Display);
    }
}
