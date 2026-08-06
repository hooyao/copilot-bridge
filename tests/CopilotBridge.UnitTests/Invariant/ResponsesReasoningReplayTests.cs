using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Reasoning state must survive Responses → IR → Claude Code → IR → Responses.
/// <para>The contract is layered: the SOURCE (T3) pushes the whole reasoning item into
/// the IR without knowing the downstream client; each DEST pulls what its own protocol
/// needs. The Claude edge folds the item into the one opaque string its wire can carry
/// (proven byte-faithful through a real 2.1.220 tool trajectory) and unfolds it on the
/// way back; T2 then pulls id/summary/content from the part bag it already reads —
/// gpt-5.6-sol live probes prove summary is required alongside encrypted_content.</para>
/// </summary>
public class ResponsesReasoningReplayTests
{
    private static readonly ClaudeCodeOutboundAdapter ClaudeEdge =
        new(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    [Fact]
    public void T3_PushesWholeReasoningItemIntoIr_WithoutKnowingTheClient()
    {
        var ir = RunT3(ReasoningThenToolStream());

        var starts = ir.Where(e => EventType(e) == "content_block_start").ToList();
        Assert.Equal(2, starts.Count);

        using var reasoningDoc = JsonDocument.Parse(starts[0].Data);
        var reasoningBlock = reasoningDoc.RootElement.GetProperty("content_block");
        Assert.Equal("redacted_thinking", reasoningBlock.GetProperty("type").GetString());
        Assert.Equal("opaque+/=bytes", reasoningBlock.GetProperty("data").GetString());
        // The whole item rides the marker so any edge can pull what it needs.
        var carried = reasoningBlock.GetProperty(ClaudeReasoningEnvelope.Marker);
        Assert.Equal("rs_contract", carried.GetProperty("id").GetString());
        Assert.Equal("[]", carried.GetProperty("summary").GetRawText());
        Assert.Equal(0, reasoningDoc.RootElement.GetProperty("index").GetInt32());

        using var toolDoc = JsonDocument.Parse(starts[1].Data);
        Assert.Equal("tool_use", toolDoc.RootElement.GetProperty("content_block")
            .GetProperty("type").GetString());
        Assert.Equal(1, toolDoc.RootElement.GetProperty("index").GetInt32());

        Assert.Equal(2, ir.Count(e => EventType(e) == "content_block_stop"));
    }

    [Fact]
    public async Task ClaudeEdge_FoldsItemIntoData_AndScrubsTheMarker()
    {
        var claudeFacing = await ClaudeStreamAsync(RunT3(ReasoningThenToolStream()));

        var start = claudeFacing.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        var block = doc.RootElement.GetProperty("content_block");

        Assert.False(block.TryGetProperty(ClaudeReasoningEnvelope.Marker, out _));
        var data = block.GetProperty("data").GetString()!;
        Assert.StartsWith(ClaudeReasoningEnvelope.Prefix, data, StringComparison.Ordinal);
        // The item's provider fields are folded into the opaque string, not exposed.
        Assert.DoesNotContain("rs_contract", start.Data, StringComparison.Ordinal);
        // No bridge-internal marker PROPERTY survives to the client on any event.
        foreach (var evt in claudeFacing)
            Assert.DoesNotContain(
                "\"" + ClaudeReasoningEnvelope.Marker + "\"", evt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderNativeBlob_IsNotMistakenForACarrier()
    {
        const string nativeBlob = "provider-native-encrypted+/=";
        var ctx = ContextFor(
            RequestWithRedactedThinking(nativeBlob), BackendVendor.CopilotResponses);
        var original = ctx.Request.Body;

        await new ClaudeReasoningUnfoldStage(
            ctx, NullLogger<ClaudeReasoningUnfoldStage>.Instance).ApplyAsync();

        // Untouched instance: a non-prefixed value is ordinary provider data.
        Assert.Same(original, ctx.Request.Body);

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(ctx.Request.Body, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()[0]!.AsObject();
        Assert.Equal(nativeBlob, reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Null(reasoning["summary"]);
    }

    [Fact]
    public async Task IncompleteReasoningItem_LeavesThePlainBlobRatherThanABadCarrier()
    {
        // No summary ⇒ not replayable. Emitting a carrier anyway would 400 next turn.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"blob-without-summary\"}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"blob-without-summary\"}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var claudeFacing = await ClaudeStreamAsync(ir);
        var start = claudeFacing.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);

        Assert.Equal("blob-without-summary",
            doc.RootElement.GetProperty("content_block").GetProperty("data").GetString());
        Assert.DoesNotContain(
            "\"" + ClaudeReasoningEnvelope.Marker + "\"", start.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCodexReasoningEcho_StillRoundTripsThroughItsOwnBag()
    {
        // The native Codex path fills the same part bag via T1; T2 pulls identically.
        const string requestJson = """
          {
            "model":"gpt-5.6-sol",
            "input":[
              {"type":"reasoning","id":"rs_native","encrypted_content":"NATIVE-BLOB",
               "summary":[{"type":"summary_text","text":"checked"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"go"}]}
            ],
            "stream":true
          }
          """;

        var emitted = CodexRoundTrip.RoundTrip(requestJson).AsObject()["input"]!.AsArray()[0]!.AsObject();

        Assert.Equal("rs_native", emitted["id"]!.GetValue<string>());
        Assert.Equal("NATIVE-BLOB", emitted["encrypted_content"]!.GetValue<string>());
        Assert.Equal(
            "[{\"type\":\"summary_text\",\"text\":\"checked\"}]",
            emitted["summary"]!.ToJsonString());
    }

    [Fact]
    public async Task NativeAnthropicPassthrough_NeverUnfoldsAPrefixedValue()
    {
        // A /cc request served by the ANTHROPIC backend must keep provider-native
        // opaque data untouched even if it happens to carry the bridge prefix — the
        // unfold stage is gated on the resolved target, not on the client.
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));
        var ctx = ContextFor(RequestWithRedactedThinking(carrier), BackendVendor.CopilotAnthropic);

        await new ClaudeReasoningUnfoldStage(
            ctx, NullLogger<ClaudeReasoningUnfoldStage>.Instance).ApplyAsync();

        var block = Assert.IsType<RedactedThinkingBlockParam>(ctx.Request.Body.Messages[0].Content[0]);
        Assert.Equal(carrier, block.Data);
        Assert.Null(block.ProviderExtensions);
    }

    [Fact]
    public async Task ResponsesTarget_UnfoldsIntoTheBagT2Reads()
    {
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));
        var ctx = ContextFor(RequestWithRedactedThinking(carrier), BackendVendor.CopilotResponses);

        await new ClaudeReasoningUnfoldStage(
            ctx, NullLogger<ClaudeReasoningUnfoldStage>.Instance).ApplyAsync();

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(ctx.Request.Body, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()[0]!.AsObject();
        Assert.Equal("rs_contract", reasoning["id"]!.GetValue<string>());
        Assert.Equal("opaque+/=bytes", reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Equal("[]", reasoning["summary"]!.ToJsonString());
    }

    [Fact]
    public async Task MalformedCarrier_FailsClosedOnTheResponsesPath()
    {
        var ctx = ContextFor(
            RequestWithRedactedThinking(ClaudeReasoningEnvelope.Prefix + "not-valid-base64url!"),
            BackendVendor.CopilotResponses);

        await Assert.ThrowsAsync<InvalidClaudeReasoningEnvelopeException>(async () =>
            await new ClaudeReasoningUnfoldStage(
                ctx, NullLogger<ClaudeReasoningUnfoldStage>.Instance).ApplyAsync());
    }

    [Fact]
    public void StaleAddedSnapshot_IsNotShipped_FinalSnapshotIs()
    {
        // Live capture shows encrypted_content and summary BOTH changing between the
        // reasoning item's `.added` and `.done` snapshots. Shipping the added one would
        // make the client replay stale state next turn.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"STALE\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"FINAL\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"done\"}]}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var start = ir.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        var block = doc.RootElement.GetProperty("content_block");

        Assert.Equal("FINAL", block.GetProperty("data").GetString());
        Assert.DoesNotContain("STALE", start.Data, StringComparison.Ordinal);
        var carried = block.GetProperty(ClaudeReasoningEnvelope.Marker);
        Assert.Equal(
            "[{\"type\":\"summary_text\",\"text\":\"done\"}]",
            carried.GetProperty("summary").GetRawText());
    }

    [Fact]
    public void ReasoningBeforeText_KeepsOutputOrder()
    {
        // The block position is reserved on `.added` and filled on `.done`, so a text
        // item that opens in between must not steal the reasoning block's index.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"m\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}"),
            Event("response.output_text.delta",
                "{\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"output_index\":1,\"delta\":\"hi\"}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"m\",\"status\":\"completed\"}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var indexes = ir
            .Where(e => EventType(e) == "content_block_start")
            .Select(e =>
            {
                using var d = JsonDocument.Parse(e.Data);
                return (
                    Type: d.RootElement.GetProperty("content_block").GetProperty("type").GetString(),
                    Index: d.RootElement.GetProperty("index").GetInt32());
            })
            .ToList();

        Assert.Equal(("redacted_thinking", 0), indexes[0]);
        Assert.Equal(("text", 1), indexes[1]);
        Assert.Equal(2, ir.Count(e => EventType(e) == "content_block_stop"));
    }

    private static BridgeContext<MessagesRequest> ContextFor(
        MessagesRequest body, BackendVendor vendor) =>
        new()
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = body,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            },
            Response = new BridgeResponse(),
            Target = new RouteTarget(
                vendor,
                vendor == BackendVendor.CopilotResponses ? "/responses" : "/v1/messages",
                body.Model),
        };

    private static readonly CodexModelProfileCatalog Catalog = new();

    private static List<SseItem<string>> RunT3(IReadOnlyList<SseItem<string>> upstream)
    {
        var t3 = new ResponsesToAnthropicStream("gpt-5.6-sol");
        var ir = new List<SseItem<string>>();
        foreach (var evt in upstream) ir.AddRange(t3.Translate(evt));
        ir.AddRange(t3.Flush());
        return ir;
    }

    private static async Task<List<SseItem<string>>> ClaudeStreamAsync(List<SseItem<string>> ir)
    {
        var result = new List<SseItem<string>>();
        await foreach (var evt in ClaudeEdge.AdaptStreamAsync(ToAsync(ir), default))
            result.Add(evt);
        return result;
    }

    private static async IAsyncEnumerable<SseItem<string>> ToAsync(List<SseItem<string>> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static string CarrierData(List<SseItem<string>> claudeFacing)
    {
        var start = claudeFacing.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        return doc.RootElement.GetProperty("content_block").GetProperty("data").GetString()!;
    }

    private static MessagesRequest RequestWithRedactedThinking(string data) =>
        new()
        {
            Model = "gpt-5.6-sol",
            Messages =
            [
                new MessageParam
                {
                    Role = Role.Assistant,
                    Content = [new RedactedThinkingBlockParam { Data = data }],
                },
            ],
        };

    private static IReadOnlyList<SseItem<string>> ReasoningThenToolStream() =>
    [
        Event("response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_reasoning_contract\",\"status\":\"in_progress\"}}"),
        Event("response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_contract\",\"encrypted_content\":\"opaque+/=bytes\",\"summary\":[],\"content\":[{\"type\":\"reasoning_text\",\"text\":\"opaque\"}]}}"),
        Event("response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_contract\",\"encrypted_content\":\"opaque+/=bytes\",\"summary\":[],\"content\":[{\"type\":\"reasoning_text\",\"text\":\"opaque\"}]}}"),
        Event("response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_contract\",\"call_id\":\"call_reasoning_tool\",\"name\":\"Bash\",\"arguments\":\"\",\"status\":\"in_progress\"}}"),
        Event("response.function_call_arguments.done",
            "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_contract\",\"output_index\":1,\"arguments\":\"{\\\"command\\\":\\\"echo ok\\\"}\"}"),
        Event("response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_contract\",\"call_id\":\"call_reasoning_tool\",\"name\":\"Bash\",\"arguments\":\"{\\\"command\\\":\\\"echo ok\\\"}\",\"status\":\"completed\"}}"),
        Event("response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_reasoning_contract\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}"),
    ];

    private static SseItem<string> Event(string type, string data) => new(data, type);

    private static string EventType(SseItem<string> item)
    {
        using var doc = JsonDocument.Parse(item.Data);
        return doc.RootElement.TryGetProperty("type", out var type)
            ? type.GetString() ?? item.EventType ?? ""
            : item.EventType ?? "";
    }
}
