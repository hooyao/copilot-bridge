using System.Net.ServerSentEvents;
using System.Text;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Reproduction harness for the "Invalid tool parameters" corruption seen in
/// production (trace 20260605-145016-0079): an <c>AskUserQuestion</c> tool call
/// streamed back from Copilot arrived at Claude Code with the <c>question</c>
/// field of each question object missing, so Claude Code's schema validation
/// rejected it.
///
/// The bridge's streaming path is:
///   upstream bytes → SseParser.Create(stream).EnumerateAsync() → SseItem&lt;string&gt;
///   → WriteSseEventAsync (re-serialize as data: lines) → downstream bytes
///
/// These tests exercise SseParser exactly as
/// <c>CopilotMessagesPassthroughStrategy.StreamEventsAsync</c> does, plus the
/// re-serialization <c>ClaudeCodeMessagesEndpoint.WriteSseEventAsync</c> does,
/// and assert that the <c>partial_json</c> payloads survive the round trip
/// byte-for-byte. If a payload is dropped or mangled, that's the bug.
/// </summary>
public class SseRoundTripTests
{
    // Mirror of the production writer (ClaudeCodeMessagesEndpoint.WriteSseEventAsync)
    // so the round trip matches what the client actually receives.
    private static string WriteSse(string? eventType, string data)
    {
        var sb = new StringBuilder(data.Length + 64);
        if (!string.IsNullOrEmpty(eventType))
        {
            sb.Append("event: ").Append(eventType).Append('\n');
        }
        foreach (var line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static async Task<List<SseItem<string>>> ParseAsync(byte[] upstreamBytes)
    {
        using var stream = new MemoryStream(upstreamBytes);
        var parser = SseParser.Create(stream);
        var items = new List<SseItem<string>>();
        await foreach (var evt in parser.EnumerateAsync())
        {
            items.Add(evt);
        }
        return items;
    }

    /// <summary>
    /// The canonical Anthropic tool-call stream: a sequence of
    /// <c>content_block_delta</c> events whose <c>partial_json</c> fragments
    /// concatenate to a complete tool input that includes a <c>question</c>
    /// field. After parse + re-serialize + reassemble, the field must survive.
    /// </summary>
    [Fact]
    public async Task ToolCallDeltas_QuestionFieldSurvivesRoundTrip()
    {
        // partial_json fragments that reassemble to:
        //   {"questions":[{"question":"Which?","header":"H","options":[]}]}
        string[] fragments =
        [
            "",
            "{\"questi",
            "ons\": [{\"qu",
            "estion\":\"Whi",
            "ch?\",\"he",
            "ader\":\"H\",\"opt",
            "ions\":[]}]}",
        ];

        var upstream = BuildToolCallSse(fragments);
        var items = await ParseAsync(upstream);

        // Re-serialize each event the way the endpoint does, then re-parse the
        // partial_json back out to simulate the full client round trip.
        var reassembled = RoundTripAndReassemble(items);

        Assert.Contains("\"question\"", reassembled);
        Assert.Equal(
            "{\"questions\": [{\"question\":\"Which?\",\"header\":\"H\",\"options\":[]}]}",
            reassembled);
    }

    /// <summary>
    /// Stress the boundary that the production trace hit: a fragment that ends
    /// mid-key right before <c>question</c>, and CRLF line endings from the
    /// upstream (Copilot emits <c>\r\n</c>). SseParser must not drop the event
    /// carrying the <c>question</c> payload.
    /// </summary>
    [Fact]
    public async Task ToolCallDeltas_CrlfLineEndings_NoFragmentDropped()
    {
        string[] fragments =
        [
            "{\"questions\": [{\"",
            "question\":\"A\",",
            "\"header\":\"H\"}]}",
        ];

        var upstream = BuildToolCallSse(fragments, crlf: true);
        var items = await ParseAsync(upstream);

        var deltaCount = items.Count(i => i.EventType == "content_block_delta");
        Assert.Equal(fragments.Length, deltaCount);

        var reassembled = RoundTripAndReassemble(items);
        Assert.Equal("{\"questions\": [{\"question\":\"A\",\"header\":\"H\"}]}", reassembled);
    }

    /// <summary>
    /// An empty <c>partial_json</c> fragment (the trace's frag[0] was "") must
    /// not swallow the following fragment. SSE treats a <c>data:</c> line with
    /// empty value as a blank data line, which is a known footgun.
    /// </summary>
    [Fact]
    public async Task ToolCallDeltas_EmptyFirstFragment_FollowingFragmentsIntact()
    {
        string[] fragments =
        [
            "",
            "{\"question\":\"Q\"}",
        ];

        var upstream = BuildToolCallSse(fragments);
        var items = await ParseAsync(upstream);
        var reassembled = RoundTripAndReassemble(items);

        Assert.Equal("{\"question\":\"Q\"}", reassembled);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal Anthropic SSE byte stream: one content_block_delta event
    /// per fragment, each carrying an input_json_delta with that partial_json.
    /// </summary>
    private static byte[] BuildToolCallSse(string[] fragments, bool crlf = false)
    {
        var nl = crlf ? "\r\n" : "\n";
        var sb = new StringBuilder();
        foreach (var fr in fragments)
        {
            // partial_json must be JSON-escaped inside the event data.
            var escaped = System.Text.Json.JsonSerializer.Serialize(fr);
            var data = $"{{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{escaped}}}}}";
            sb.Append("event: content_block_delta").Append(nl);
            sb.Append("data: ").Append(data).Append(nl);
            sb.Append(nl);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Take parsed SseItems, run each through the endpoint's WriteSse writer,
    /// re-parse the resulting bytes (simulating the client), and concatenate
    /// the partial_json fragments back into the tool input string.
    /// </summary>
    private static string RoundTripAndReassemble(List<SseItem<string>> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(WriteSse(item.EventType, item.Data));
        }
        var clientBytes = Encoding.UTF8.GetBytes(sb.ToString());

        using var stream = new MemoryStream(clientBytes);
        var parser = SseParser.Create(stream);
        var json = new StringBuilder();
        foreach (var evt in EnumerateSync(parser))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(evt.Data);
            if (doc.RootElement.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("partial_json", out var pj))
            {
                json.Append(pj.GetString());
            }
        }
        return json.ToString();
    }

    private static IEnumerable<SseItem<string>> EnumerateSync(SseParser<string> parser)
    {
        foreach (var evt in parser.Enumerate())
        {
            yield return evt;
        }
    }
}
