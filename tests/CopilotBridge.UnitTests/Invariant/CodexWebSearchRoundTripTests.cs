using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the hosted <b>web_search_call</b> round-trip through the Codex
/// T3→T4 double-translation. Copilot's <c>/responses</c> runs web search SERVER-SIDE
/// and reports it back as <c>web_search_call</c> output items plus
/// <c>response.web_search_call.in_progress/.searching/.completed</c> lifecycle events.
/// Codex renders these to show the user a search happened. Before this change T3's
/// closed allow-list SWALLOWED the lifecycle events and mis-emitted the item as an
/// empty text block, so codex never saw the search (invisible + uncited).
/// </summary>
/// <remarks>
/// <para><b>Contract source (NOT guessed):</b> the fixture
/// <c>Fixtures/responses-sse-websearch.txt</c> is a REAL Copilot <c>/responses</c>
/// stream captured from a live gpt-5.5 run (`codex exec` forced to hosted web search,
/// 2026-07-20). It contains 7 web_search_call items, each framed as
/// <c>output_item.added</c> (status:in_progress, no action) → <c>web_search_call.
/// in_progress/.searching/.completed</c> → <c>output_item.done</c> (status:completed,
/// with <c>action</c> = <c>{type:search,query,queries}</c> or <c>{type:open_page,url}</c>).</para>
/// <para>The invariant: after T3→T4 the codex-facing stream MUST carry those 7
/// web_search_call items back (added + lifecycle + done with the completed action),
/// NOT empty text messages, and NO <c>bridge_web_search_call*</c> marker may leak to
/// the wire. Each test reddens if the carrier is reverted (mutation-check).</para>
/// </remarks>
public class CodexWebSearchRoundTripTests
{
    private const string StartMarker = "bridge_web_search_call";
    private const string ResultMarker = "bridge_web_search_call_result";

    // The real capture has exactly 7 web_search_call items (7× the
    // in_progress/searching/completed triple). Anchoring on the real count makes the
    // test fail if T3/T4 drops or duplicates a search item.
    private const int ExpectedWebSearchCalls = 7;

    [Fact]
    public void RealFixture_HasWebSearchLifecycle_ThatTodayWouldBeDropped()
    {
        // Guard the premise: the captured upstream really does carry the web_search_call
        // lifecycle events (so the round-trip test below is exercising the real shape,
        // not a stream that never had them). If the fixture is ever replaced with one
        // lacking web search, this reddens first with a clear message.
        var upstream = LoadFixture();
        var lifecycle = upstream.Count(e =>
            (e.EventType ?? "").StartsWith("response.web_search_call.", StringComparison.Ordinal));
        Assert.True(lifecycle >= ExpectedWebSearchCalls,
            $"fixture should carry ≥{ExpectedWebSearchCalls} web_search_call lifecycle events, saw {lifecycle}");
    }

    [Fact]
    public void T3ThenT4_WebSearchCallItems_ReachTheCodexFacingWire()
    {
        // The core contract: every server-side web_search_call must survive the
        // double-translation as a web_search_call OUTPUT ITEM (not an empty text
        // message), so codex renders the search it ran.
        var roundTripped = RunT3ThenT4(LoadFixture());

        var completed = FindWebSearchCallItems(roundTripped, status: "completed");
        Assert.Equal(ExpectedWebSearchCalls, completed.Count);
        // Each completed item carries its action (the search queries or opened url) —
        // the payload T3 captured from output_item.done, proving it's the DONE item and
        // not a stub.
        Assert.All(completed, item =>
            Assert.True(item["action"] is JsonObject, "completed web_search_call must carry its action"));
    }

    [Fact]
    public void T3ThenT4_EmitsWebSearchLifecycleEvents_NotEmptyTextMessages()
    {
        // The bug being fixed: T3 used to mis-map the web_search_call item to an empty
        // text block, so T4 emitted a bogus empty assistant message per search. After
        // the fix, the codex-facing stream carries the FULL web_search_call lifecycle
        // (in_progress + searching + completed) and NO empty-text message stands in for
        // a search.
        var roundTripped = RunT3ThenT4(LoadFixture());

        // Full lifecycle — all three phases, each once per search. Asserting only two
        // of the three would let a dropped `searching` phase pass unnoticed.
        Assert.Equal(ExpectedWebSearchCalls,
            roundTripped.Count(e => EventType(e) == "response.web_search_call.in_progress"));
        Assert.Equal(ExpectedWebSearchCalls,
            roundTripped.Count(e => EventType(e) == "response.web_search_call.searching"));
        Assert.Equal(ExpectedWebSearchCalls,
            roundTripped.Count(e => EventType(e) == "response.web_search_call.completed"));

        // The core regression guard: NO completed message item may be an EMPTY assistant
        // message — that empty text block is exactly what a swallowed web_search_call
        // used to become. (The one real message item in the fixture carries the answer
        // text; the 7 searches must NOT add empty ones.)
        var emptyMessages = FindCompletedMessageItems(roundTripped)
            .Count(m => string.IsNullOrEmpty(MessageText(m)));
        Assert.Equal(0, emptyMessages);
    }

    [Fact]
    public void T3ThenT4_Marker_NeverLeaksToTheCodexFacingWire()
    {
        // The bridge-internal markers T3 stamps (bridge_web_search_call on the block
        // start, bridge_web_search_call_result on the block stop) must NEVER reach the
        // codex-facing stream — T4 rebuilds the item from them and drops the markers.
        var roundTripped = RunT3ThenT4(LoadFixture());

        foreach (var e in roundTripped)
        {
            Assert.DoesNotContain(StartMarker, e.Data, StringComparison.Ordinal);
            Assert.DoesNotContain(ResultMarker, e.Data, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CcToGpt_Markers_ScrubbedBeforeReachingClaude()
    {
        // On the CC→gpt route there is NO T4 — ClaudeCodeOutboundAdapter is the client
        // edge. It must strip BOTH web-search markers (nested on content_block_start,
        // top-level on content_block_stop) so neither reaches claude.exe as bogus
        // metadata.
        var ir = RunT3(LoadFixture());

        // Sanity: T3 really did stamp the markers into the IR (else the scrub is a no-op
        // and the test proves nothing).
        Assert.Contains(ir, e => e.Data.Contains(StartMarker, StringComparison.Ordinal));
        Assert.Contains(ir, e => e.Data.Contains(ResultMarker, StringComparison.Ordinal));

        var scrubbed = ScrubForClaude(ir);
        foreach (var e in scrubbed)
        {
            Assert.DoesNotContain(StartMarker, e.Data, StringComparison.Ordinal);
            Assert.DoesNotContain(ResultMarker, e.Data, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CcToGpt_Buffered_Markers_ScrubbedBeforeReachingClaude()
    {
        // The BUFFERED CC-edge (ClaudeCodeOutboundAdapter.AdaptBufferedAsync) also scrubs
        // the web-search marker — but the streaming test above only exercises
        // AdaptStreamAsync. Without this, a buffered marker leak to claude.exe could
        // regress unnoticed (the buffered T3→T4 test exercises Codex T4, not the CC edge).
        // Build a buffered T3 body carrying bridge_web_search_call, feed it through the
        // buffered CC edge, and assert both marker names are gone.
        const string responsesObject =
            "{\"object\":\"response\",\"status\":\"completed\",\"id\":\"resp_1\",\"model\":\"gpt-5.5\","
            + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1},"
            + "\"output\":[{\"type\":\"web_search_call\",\"id\":\"ws_1\",\"status\":\"completed\","
            + "\"action\":{\"type\":\"open_page\",\"url\":\"https://nodejs.org/dist/latest-v20.x/\"}},"
            + "{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}]}";
        var bufferedIr = BufferedResponsesToAnthropic.TryTranslate(Enc(responsesObject));
        Assert.NotNull(bufferedIr);
        // Sanity: the marker is really in the buffered IR body (else the scrub is a no-op).
        Assert.Contains(StartMarker, System.Text.Encoding.UTF8.GetString(bufferedIr!), StringComparison.Ordinal);

        var adapter = NewClaudeAdapter();
        var scrubbed = adapter.AdaptBufferedAsync(bufferedIr!, default).AsTask().GetAwaiter().GetResult();
        var scrubbedText = System.Text.Encoding.UTF8.GetString(scrubbed);
        Assert.DoesNotContain(StartMarker, scrubbedText, StringComparison.Ordinal);
        Assert.DoesNotContain(ResultMarker, scrubbedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffered_T3ThenT4_WebSearchCall_SurvivesAsOutputItem()
    {
        // Buffered parity (codex always streams, but keep both edges honest): a
        // web_search_call in a non-streaming Responses `output[]` must survive
        // buffered T3 → buffered T4 as a web_search_call output item, not an empty
        // message. Built from the real streaming item's shape (type/status/action).
        const string wsItem =
            "{\"type\":\"web_search_call\",\"id\":\"ws_1\",\"status\":\"completed\","
            + "\"action\":{\"type\":\"open_page\",\"url\":\"https://nodejs.org/dist/latest-v20.x/\"}}";
        var responsesObject =
            "{\"object\":\"response\",\"status\":\"completed\",\"id\":\"resp_1\",\"model\":\"gpt-5.5\","
            + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1},"
            + "\"output\":[" + wsItem + ",{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}]}";

        var ir = BufferedResponsesToAnthropic.TryTranslate(Enc(responsesObject));
        Assert.NotNull(ir);
        // Marker rode across the IR…
        Assert.Contains(StartMarker, System.Text.Encoding.UTF8.GetString(ir!), StringComparison.Ordinal);

        var back = BufferedAnthropicToResponses.TryTranslate(ir);
        Assert.NotNull(back);
        var obj = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(back!))!.AsObject();
        var outputs = obj["output"]!.AsArray();
        var wsBack = outputs.FirstOrDefault(o =>
            o?["type"]?.GetValue<string>() == "web_search_call");
        Assert.NotNull(wsBack);
        Assert.Equal("completed", wsBack!["status"]!.GetValue<string>());
        Assert.Equal("open_page", wsBack["action"]!["type"]!.GetValue<string>());
        // …and no marker leaked to the codex-facing buffered body.
        Assert.DoesNotContain(StartMarker, System.Text.Encoding.UTF8.GetString(back!), StringComparison.Ordinal);
    }

    private static byte[] Enc(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void T4_InterruptedSearch_NoCompletedMarker_DoesNotFabricateCompletion()
    {
        // Contract: when a web-search block is closed WITHOUT a completed item — the
        // content_block_stop carries NO bridge_web_search_call_result (T3.Flush closing an
        // open block on response.incomplete, i.e. an interrupted search) — T4 must NOT tell
        // codex the search completed. It must emit neither response.web_search_call.completed
        // nor an output_item.done whose item claims status:"completed". Fabricating a
        // completed lifecycle from the in-progress item is a protocol lie (codex would render
        // a finished search that never finished).
        var ir = new List<SseItem<string>>
        {
            new("{\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"role\":\"assistant\",\"content\":[]}}", "message_start"),
            // Web-search block opened with the in-progress item (status:in_progress, no action)…
            new("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\","
                + "\"bridge_web_search_call\":{\"type\":\"web_search_call\",\"id\":\"ws_1\",\"status\":\"in_progress\"}}}", "content_block_start"),
            // …then closed by a PLAIN stop (no bridge_web_search_call_result) — the interrupted path.
            new("{\"type\":\"content_block_stop\",\"index\":0}", "content_block_stop"),
            new("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}", "message_delta"),
            new("{\"type\":\"message_stop\"}", "message_stop"),
        };
        var t4 = new AnthropicToResponsesStream("gpt-5.5");
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());

        // No fabricated completion event.
        Assert.DoesNotContain(outp, e => EventType(e) == "response.web_search_call.completed");
        // No output_item.done claiming the search completed.
        Assert.Empty(FindWebSearchCallItems(outp, status: "completed"));
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static List<SseItem<string>> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses-sse-websearch.txt");
        return ParseSse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parse an SSE stream (<c>event: X\ndata: Y</c> records separated by blank lines)
    /// into <see cref="SseItem{T}"/>s, matching what <c>SseParser</c> hands the strategy.
    /// </summary>
    private static List<SseItem<string>> ParseSse(string raw)
    {
        var items = new List<SseItem<string>>();
        string? ev = null;
        foreach (var lineRaw in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = lineRaw;
            if (line.StartsWith("event:", StringComparison.Ordinal))
                ev = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line["data:".Length..].TrimStart();
                items.Add(new SseItem<string>(data, ev));
            }
            else if (line.Length == 0)
                ev = null; // record boundary
        }
        return items;
    }

    private static List<SseItem<string>> RunT3(List<SseItem<string>> responsesStream, string model = "gpt-5.5")
    {
        var t3 = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in responsesStream) ir.AddRange(t3.Translate(e));
        ir.AddRange(t3.Flush());
        return ir;
    }

    private static List<SseItem<string>> RunT3ThenT4(List<SseItem<string>> responsesStream, string model = "gpt-5.5")
    {
        var ir = RunT3(responsesStream, model);
        var t4 = new AnthropicToResponsesStream(model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());
        return outp;
    }

    private static List<JsonObject> FindWebSearchCallItems(List<SseItem<string>> stream, string status)
    {
        var found = new List<JsonObject>();
        foreach (var e in stream)
        {
            JsonObject node;
            try { node = JsonNode.Parse(e.Data)!.AsObject(); }
            catch { continue; }
            if (node["type"]?.GetValue<string>() != "response.output_item.done") continue;
            if (node["item"] is JsonObject item
                && item["type"]?.GetValue<string>() == "web_search_call"
                && item["status"]?.GetValue<string>() == status)
                found.Add(item);
        }
        return found;
    }

    private static List<JsonObject> FindCompletedMessageItems(List<SseItem<string>> stream)
    {
        var found = new List<JsonObject>();
        foreach (var e in stream)
        {
            JsonObject node;
            try { node = JsonNode.Parse(e.Data)!.AsObject(); }
            catch { continue; }
            if (node["type"]?.GetValue<string>() != "response.output_item.done") continue;
            if (node["item"] is JsonObject item
                && item["type"]?.GetValue<string>() == "message")
                found.Add(item);
        }
        return found;
    }

    /// <summary>Concatenated output_text of a message item (empty when it carries none).</summary>
    private static string MessageText(JsonObject messageItem)
    {
        if (messageItem["content"] is not JsonArray content) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var part in content)
            if (part?["type"]?.GetValue<string>() == "output_text")
                sb.Append(part["text"]?.GetValue<string>() ?? "");
        return sb.ToString();
    }

    private static string? EventType(SseItem<string> e)
    {
        try { return JsonNode.Parse(e.Data)?["type"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static ClaudeCodeOutboundAdapter NewClaudeAdapter() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    private static List<SseItem<string>> ScrubForClaude(List<SseItem<string>> ir)
    {
        var adapter = NewClaudeAdapter();
        async IAsyncEnumerable<SseItem<string>> Source()
        {
            foreach (var e in ir) { yield return e; await Task.Yield(); }
        }
        var outp = new List<SseItem<string>>();
        var e = adapter.AdaptStreamAsync(Source(), default).GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                outp.Add(e.Current);
        }
        finally { e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return outp;
    }
}
