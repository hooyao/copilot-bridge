using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract: one logical Responses message is one client item. Every lifecycle
/// event for its output index must carry one stable id, because Codex streams the
/// added item and later merges the completed snapshot by that id. Copilot's rolling
/// opaque ids are backend wire data, not valid client-side item identity.
/// </summary>
public class CodexMessageItemIdStabilityTests
{
    private const string Model = "gpt-5.6-sol";
    private const int MessageOutputIndex = 1;
    private const string Fixture = "responses-sse-rolling-message-ids.txt";
    private const string IdentitySentinel = "<stable-message-id>";

    [Fact]
    public void Rolling_message_lifecycle_ids_become_one_client_identity_without_other_changes()
    {
        var original = ParseFixture(Fixture);
        var emitted = RoundTripNative(original);

        Assert.Equal(original.Count, emitted.Count);
        Assert.Equal(original.Select(e => e.EventType), emitted.Select(e => e.EventType));

        var ids = MessageIds(emitted, MessageOutputIndex);
        var stable = Assert.Single(ids.Distinct(StringComparer.Ordinal));
        Assert.Equal("opaque-message-added", stable);
        Assert.False(stable.StartsWith("msg", StringComparison.Ordinal));

        AssertEventValuesEqual(
            NormalizeMessageIdentity(original, MessageOutputIndex),
            NormalizeMessageIdentity(emitted, MessageOutputIndex));
    }

    [Fact]
    public void Client_message_identity_is_deterministic_for_the_same_source_stream()
    {
        var source = ParseFixture(Fixture);

        var first = Assert.Single(MessageIds(RoundTripNative(source), MessageOutputIndex)
            .Distinct(StringComparer.Ordinal));
        var second = Assert.Single(MessageIds(RoundTripNative(source), MessageOutputIndex)
            .Distinct(StringComparer.Ordinal));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Already_stable_official_message_identity_remains_value_identical()
    {
        var source = TextMessageStream(
            responseId: "resp_official",
            addedId: "msg_official",
            deltaId: "msg_official",
            doneId: "msg_official",
            terminalId: "msg_official");

        AssertEventValuesEqual(source, RoundTripNative(source));
    }

    [Fact]
    public void Missing_added_id_gets_one_deterministic_non_msg_fallback()
    {
        var source = TextMessageStream(
            responseId: "resp_missing",
            addedId: null,
            deltaId: "opaque-delta",
            doneId: "opaque-done",
            terminalId: "opaque-terminal");

        var first = Assert.Single(MessageIds(RoundTripNative(source), outputIndex: 0)
            .Distinct(StringComparer.Ordinal));
        var second = Assert.Single(MessageIds(RoundTripNative(source), outputIndex: 0)
            .Distinct(StringComparer.Ordinal));

        Assert.StartsWith("item_", first, StringComparison.Ordinal);
        Assert.False(first.StartsWith("msg", StringComparison.Ordinal));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Multiple_messages_keep_distinct_canonical_ids_through_incomplete_terminal()
    {
        var source = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_multi","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"opaque-first","phase":"commentary","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"rolling-first-delta","output_index":0,"content_index":0,"delta":"first"}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"rolling-first-done","phase":"commentary","role":"assistant","status":"completed","content":[{"type":"output_text","text":"first"}]}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"opaque-second","phase":"final_answer","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.future_message_metric", """{"type":"response.future_message_metric","item_id":"rolling-future","output_index":1,"metric":{"keep":true}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"rolling-second-delta","output_index":1,"content_index":0,"delta":"second"}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"rolling-second-done","phase":"final_answer","role":"assistant","status":"incomplete","content":[{"type":"output_text","text":"second"}]}}"""),
            Sse("response.incomplete", """{"type":"response.incomplete","response":{"id":"resp_multi","status":"incomplete","output":[{"type":"message","id":"rolling-first-terminal","phase":"commentary","role":"assistant","status":"completed","content":[{"type":"output_text","text":"first"}]},{"type":"message","id":"rolling-second-terminal","phase":"final_answer","role":"assistant","status":"incomplete","content":[{"type":"output_text","text":"second"}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}"""),
        };

        var emitted = RoundTripNative(source);

        Assert.Equal(["opaque-first"], MessageIds(emitted, 0).Distinct(StringComparer.Ordinal));
        Assert.Equal(["opaque-second"], MessageIds(emitted, 1).Distinct(StringComparer.Ordinal));
        AssertEventValuesEqual(
            NormalizeMessageIdentity(
                NormalizeMessageIdentity(source, 0), 1),
            NormalizeMessageIdentity(
            NormalizeMessageIdentity(emitted, 0), 1));
    }

    [Fact]
    public void Synthesized_failed_terminal_keeps_the_message_identity_already_sent()
    {
        // response.failed is exceptional in T3: it throws before recording a native
        // carrier. T4 must therefore synthesize the failed terminal, but any message
        // completed before that fault is still the SAME client item whose lifecycle
        // was already restored with the output_item.added id.
        var source = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_failed","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"opaque-failed-added","phase":"final_answer","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"rolling-failed-delta","output_index":0,"content_index":0,"delta":"partial"}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"rolling-failed-done","phase":"final_answer","role":"assistant","status":"completed","content":[{"type":"output_text","text":"partial"}]}}"""),
            Sse("response.failed", """{"type":"response.failed","response":{"id":"resp_failed","status":"failed","error":{"code":"upstream_error","message":"generated detail"}}}"""),
        };

        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = new List<SseItem<string>>();
        var failure = Assert.Throws<UpstreamResponseFailedException>(() =>
        {
            foreach (var evt in source)
                ir.AddRange(t3.Translate(evt));
        });

        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var emitted = ir.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.FlushTerminal(failed: true, failureCode: failure.Code));

        Assert.Equal(["opaque-failed-added"],
            MessageIds(emitted, outputIndex: 0).Distinct(StringComparer.Ordinal));
        var terminal = emitted.Single(evt => evt.EventType == "response.failed");
        Assert.Equal(
            "opaque-failed-added",
            JsonNode.Parse(terminal.Data)!["response"]!["output"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void Unmapped_and_non_message_identities_are_not_normalized()
    {
        var source = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_near_miss","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"reasoning-added","encrypted_content":"blob-one","summary":[]}}"""),
            Sse("response.future_reasoning_metric", """{"type":"response.future_reasoning_metric","item_id":"reasoning-metric","output_index":0,"metric":1}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reasoning-done","encrypted_content":"blob-two","summary":[]}}"""),
            Sse("response.future_unmapped", """{"type":"response.future_unmapped","item_id":"unmapped-id","output_index":7,"keep":true}"""),
            Sse("response.completed", """{"type":"response.completed","response":{"id":"resp_near_miss","status":"completed","output":[{"type":"reasoning","id":"reasoning-terminal","encrypted_content":"blob-two","summary":[]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}"""),
        };

        AssertEventValuesEqual(source, RoundTripNative(source));
    }

    [Theory]
    [InlineData("opaque-message-added", false)]
    [InlineData("msg_official", true)]
    public void Echoed_message_identity_obeys_the_existing_upstream_boundary(
        string id,
        bool shouldRemain)
    {
        var request = $$"""
          {"model":"gpt-5.6-sol","input":[{
            "type":"message","role":"assistant","id":"{{id}}",
            "phase":"final_answer","status":"completed",
            "content":[{"type":"output_text","text":"stable-message-canary","future_part":1}],
            "future_message":{"keep":true}
          }],"stream":true}
          """;

        var emitted = CodexRoundTrip.RoundTrip(request).AsObject()["input"]!.AsArray().Single()!.AsObject();

        Assert.Equal(shouldRemain, emitted.ContainsKey("id"));
        if (shouldRemain) Assert.Equal(id, emitted["id"]!.GetValue<string>());
        Assert.Equal("final_answer", emitted["phase"]!.GetValue<string>());
        Assert.Equal("completed", emitted["status"]!.GetValue<string>());
        Assert.Equal("stable-message-canary", emitted["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, emitted["content"]![0]!["future_part"]!.GetValue<int>());
        Assert.True(emitted["future_message"]!["keep"]!.GetValue<bool>());
    }

    private static List<SseItem<string>> RoundTripNative(IReadOnlyList<SseItem<string>> source)
    {
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = source.SelectMany(t3.Translate).ToList();
        ir.AddRange(t3.Flush());

        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var emitted = ir.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.Flush());
        Assert.Equal(0, ledger.Count);
        return emitted;
    }

    private static List<SseItem<string>> TextMessageStream(
        string responseId,
        string? addedId,
        string deltaId,
        string doneId,
        string terminalId)
    {
        var addedIdProperty = addedId is null ? "" : $",\"id\":{JsonSerializer.Serialize(addedId)}";
        return
        [
            Sse("response.created", $$$"""{"type":"response.created","response":{"id":"{{{responseId}}}","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", $$$"""{"type":"response.output_item.added","output_index":0,"item":{"type":"message"{{{addedIdProperty}}},"phase":"final_answer","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.output_text.delta", $$$"""{"type":"response.output_text.delta","item_id":"{{{deltaId}}}","output_index":0,"content_index":0,"delta":"answer"}"""),
            Sse("response.output_item.done", $$$"""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"{{{doneId}}}","phase":"final_answer","role":"assistant","status":"completed","content":[{"type":"output_text","text":"answer"}]}}"""),
            Sse("response.completed", $$$$"""{"type":"response.completed","response":{"id":"{{{{responseId}}}}","status":"completed","output":[{"type":"message","id":"{{{{terminalId}}}}","phase":"final_answer","role":"assistant","status":"completed","content":[{"type":"output_text","text":"answer"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}"""),
        ];
    }

    private static SseItem<string> Sse(string type, string data) => new(data, type);

    private static IReadOnlyList<string> MessageIds(
        IEnumerable<SseItem<string>> events,
        int outputIndex)
    {
        var ids = new List<string>();
        foreach (var evt in events)
        {
            var root = JsonNode.Parse(evt.Data)!.AsObject();
            var type = root["type"]?.GetValue<string>();
            if (root["output_index"]?.GetValue<int>() == outputIndex)
            {
                if (root["item_id"]?.GetValue<string>() is { } itemId)
                    ids.Add(itemId);
                if (type is "response.output_item.added" or "response.output_item.done"
                    && root["item"] is JsonObject item
                    && item["type"]?.GetValue<string>() == "message"
                    && item["id"]?.GetValue<string>() is { } lifecycleId)
                    ids.Add(lifecycleId);
            }

            if (type is "response.completed" or "response.incomplete" or "response.failed"
                && root["response"]?["output"] is JsonArray output
                && outputIndex < output.Count
                && output[outputIndex] is JsonObject terminalItem
                && terminalItem["type"]?.GetValue<string>() == "message"
                && terminalItem["id"]?.GetValue<string>() is { } terminalId)
                ids.Add(terminalId);
        }
        return ids;
    }

    private static List<SseItem<string>> NormalizeMessageIdentity(
        IEnumerable<SseItem<string>> events,
        int outputIndex)
    {
        var result = new List<SseItem<string>>();
        foreach (var evt in events)
        {
            var root = JsonNode.Parse(evt.Data)!.AsObject();
            var type = root["type"]?.GetValue<string>();
            if (root["output_index"]?.GetValue<int>() == outputIndex)
            {
                if (root.ContainsKey("item_id"))
                    root["item_id"] = IdentitySentinel;
                if (type is "response.output_item.added" or "response.output_item.done"
                    && root["item"] is JsonObject item
                    && item["type"]?.GetValue<string>() == "message")
                    item["id"] = IdentitySentinel;
            }

            if (type is "response.completed" or "response.incomplete" or "response.failed"
                && root["response"]?["output"] is JsonArray output
                && outputIndex < output.Count
                && output[outputIndex] is JsonObject terminalItem
                && terminalItem["type"]?.GetValue<string>() == "message")
                terminalItem["id"] = IdentitySentinel;

            result.Add(new SseItem<string>(root.ToJsonString(), evt.EventType));
        }
        return result;
    }

    private static void AssertEventValuesEqual(
        IReadOnlyList<SseItem<string>> expected,
        IReadOnlyList<SseItem<string>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].EventType, actual[i].EventType);
            using var expectedJson = JsonDocument.Parse(expected[i].Data);
            using var actualJson = JsonDocument.Parse(actual[i].Data);
            Assert.True(
                JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement),
                $"event[{i}] {expected[i].EventType} changed outside message identity");
        }
    }

    private static List<SseItem<string>> ParseFixture(string name)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var result = new List<SseItem<string>>();
        string? eventType = null;
        var data = new StringBuilder();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line[5..].TrimStart());
            else if (line.Length == 0 && (eventType is not null || data.Length > 0))
            {
                result.Add(new SseItem<string>(data.ToString(), eventType));
                eventType = null;
                data.Clear();
            }
        }
        if (eventType is not null || data.Length > 0)
            result.Add(new SseItem<string>(data.ToString(), eventType));
        return result;
    }
}
