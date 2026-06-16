using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Pipeline;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Tests for the raw-upstream-response tee (<see cref="TeeReadStream"/> +
/// <see cref="RawResponseCapture"/>). The tee sits between Copilot's network
/// stream and <c>SseParser</c> so the <c>upstream-resp</c> audit records the
/// EXACT bytes Copilot put on the wire, pre-stage. Two invariants matter:
/// <list type="number">
/// <item>the capture is byte-for-byte equal to the inner stream's content;</item>
/// <item>the parsed <c>SseItem</c>s are identical to parsing the stream
/// directly — the tee must not perturb what flows downstream to Claude Code.</item>
/// </list>
/// Mirrors the parse harness in <see cref="SseRoundTripTests"/>.
/// </summary>
public class RawResponseCaptureTests
{
    // A realistic multi-event Anthropic SSE body: message_start → a couple of
    // text deltas (multi-byte UTF-8 included) → message_stop → [DONE].
    private static byte[] SampleSse()
    {
        var sb = new StringBuilder();
        sb.Append("event: message_start\n");
        sb.Append("data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4.8\"}}\n\n");
        sb.Append("event: content_block_delta\n");
        sb.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"现在\"}}\n\n");
        sb.Append("event: content_block_delta\n");
        sb.Append("data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"回到 D2\"}}\n\n");
        sb.Append("event: message_stop\n");
        sb.Append("data: {\"type\":\"message_stop\"}\n\n");
        sb.Append("event: message\n");
        sb.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<List<(string? Type, string Data)>> ParseAsync(Stream stream)
    {
        var parser = SseParser.Create(stream);
        var items = new List<(string?, string)>();
        await foreach (var evt in parser.EnumerateAsync())
        {
            items.Add((evt.EventType, evt.Data));
        }
        return items;
    }

    /// <summary>
    /// Driving <c>SseParser</c> through the tee captures the raw bytes verbatim
    /// AND yields the same events as parsing the bytes directly.
    /// </summary>
    [Fact]
    public async Task Tee_CapturesRawBytes_AndYieldsIdenticalEvents()
    {
        var raw = SampleSse();

        // Baseline: parse the bytes directly (no tee).
        var direct = await ParseAsync(new MemoryStream(raw));

        // Through the tee.
        var capture = new RawResponseCapture();
        using var inner = new MemoryStream(raw);
        await using var tee = new TeeReadStream(inner, capture);
        var teed = await ParseAsync(tee);

        // 1) Raw bytes captured exactly.
        Assert.Equal(raw, capture.ToArray());
        Assert.Equal(raw.Length, capture.Length);

        // 2) Events identical to a direct parse — the tee is observe-only.
        Assert.Equal(direct, teed);
    }

    /// <summary>
    /// A pathologically small read buffer forces many partial reads; the
    /// capture must still reassemble the whole body with no dropped or
    /// duplicated bytes.
    /// </summary>
    [Fact]
    public async Task Tee_TinyReads_CaptureRemainsExact()
    {
        var raw = SampleSse();
        var capture = new RawResponseCapture();
        using var inner = new MemoryStream(raw);
        await using var tee = new TeeReadStream(inner, capture);

        // Read 1 byte at a time through the tee directly (not via SseParser),
        // exercising the partial-read path explicitly.
        var sink = new byte[raw.Length];
        var total = 0;
        var one = new byte[1];
        int n;
        while ((n = await tee.ReadAsync(one.AsMemory(0, 1))) > 0)
        {
            sink[total] = one[0];
            total += n;
        }

        Assert.Equal(raw.Length, total);
        Assert.Equal(raw, sink);
        Assert.Equal(raw, capture.ToArray());
    }

    /// <summary>
    /// The tee must NOT dispose its inner stream — the SSE iterator owns the raw
    /// network stream and disposes it in its own finally; a double dispose would
    /// be a bug. After disposing the tee, the inner stream is still usable.
    /// </summary>
    [Fact]
    public async Task Tee_Dispose_DoesNotDisposeInner()
    {
        var raw = SampleSse();
        var inner = new MemoryStream(raw);
        var capture = new RawResponseCapture();

        var tee = new TeeReadStream(inner, capture);
        await tee.DisposeAsync();

        // Inner is still readable (not disposed by the tee).
        inner.Position = 0;
        var buf = new byte[raw.Length];
        var read = await inner.ReadAsync(buf.AsMemory(0, raw.Length));
        Assert.Equal(raw.Length, read);
        inner.Dispose();
    }

    /// <summary>An empty upstream stream yields an empty capture, not a throw.</summary>
    [Fact]
    public async Task Tee_EmptyStream_EmptyCapture()
    {
        var capture = new RawResponseCapture();
        using var inner = new MemoryStream(Array.Empty<byte>());
        await using var tee = new TeeReadStream(inner, capture);
        var items = await ParseAsync(tee);

        Assert.Empty(items);
        Assert.Equal(0, capture.Length);
        Assert.Empty(capture.ToArray());
    }
}
