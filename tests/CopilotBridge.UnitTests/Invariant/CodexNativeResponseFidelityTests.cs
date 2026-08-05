using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

public class CodexNativeResponseFidelityTests
{
    private const string Model = "gpt-5.6-sol";

    [Fact]
    public void Response_ledger_is_not_allocated_on_carrier_free_paths()
    {
        var response = new BridgeResponse();
        Assert.Null(response.NativeResponsesEvents);
    }

    [Fact]
    public void Clean_native_stream_preserves_every_ordered_event_value()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var emitted = RoundTripNative(original);

        Assert.Equal(original.Count, emitted.Count);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].EventType, emitted[i].EventType);
            using var expected = JsonDocument.Parse(original[i].Data);
            using var actual = JsonDocument.Parse(emitted[i].Data);
            Assert.True(
                JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
                $"event[{i}] {original[i].EventType} changed");
        }
    }

    [Fact]
    public void Native_T3_emits_private_carrier_for_semantic_and_unmodeled_events()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = original.SelectMany(t3.Translate).ToList();

        Assert.Equal(NativeResponsesEventCarrier.BeginType, ir[0].EventType);
        Assert.Equal(original.Count, ir.Count(e => e.EventType == NativeResponsesEventCarrier.EventType));
        Assert.Equal(original.Count, ledger.Count);
        Assert.All(ir.Where(e => e.EventType == NativeResponsesEventCarrier.EventType), e =>
        {
            Assert.DoesNotContain("reasoning_summary", e.Data, StringComparison.Ordinal);
            Assert.DoesNotContain("future_extension", e.Data, StringComparison.Ordinal);
            Assert.Equal("", e.Data);
        });
    }

    [Fact]
    public void Streaming_T3_T4_ledger_holds_at_most_one_source_event()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            Model, preserveNativeEvents: true, nativeLedger: ledger);
        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var maxEntries = 0;
        var emitted = new List<SseItem<string>>();

        foreach (var source in original)
        {
            foreach (var ir in t3.Translate(source))
            {
                maxEntries = Math.Max(maxEntries, ledger.Count);
                emitted.AddRange(t4.Translate(ir));
                maxEntries = Math.Max(maxEntries, ledger.Count);
            }
        }
        emitted.AddRange(t4.Flush());

        Assert.InRange(maxEntries, 1, 1);
        Assert.Equal(0, ledger.Count);
        AssertEventValuesEqual(original, emitted);
    }

    [Fact]
    public void Malformed_source_event_is_not_authorized_by_zero_semantic_carrier()
    {
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = t3.Translate(new SseItem<string>("{not-json", "response.future_extension")).ToList();

        Assert.Single(ir);
        Assert.Equal(NativeResponsesEventCarrier.BeginType, ir[0].EventType);
        Assert.Equal(0, ledger.Count);
        var emitted = RunT4(ir, ledger);
        Assert.DoesNotContain(emitted, e => e.Data.Contains("not-json", StringComparison.Ordinal));
    }

    [Fact]
    public void Carrier_without_ledger_entry_fails_closed()
    {
        var ledger = new NativeResponsesEventLedger();
        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var ir = new[]
        {
            NativeResponsesEventCarrier.Begin(),
            new SseItem<string>("", NativeResponsesEventCarrier.EventType),
        };

        var emitted = ir.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.FlushTerminal(failed: false));

        Assert.DoesNotContain(emitted, e => e.EventType is "response.completed" or "response.incomplete");
        Assert.Single(emitted, e => e.EventType == "response.failed");
    }

    [Fact]
    public void Premature_EOF_after_custom_delta_emits_failed_without_successful_done()
    {
        var partial = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_partial","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"custom_tool_call","id":"opaque","call_id":"call_partial","name":"exec","input":"","status":"in_progress"}}"""),
            Sse("response.custom_tool_call_input.delta", """{"type":"response.custom_tool_call_input.delta","item_id":"ctc_partial","output_index":0,"delta":"await tools.partial("}"""),
        };

        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = partial.SelectMany(t3.Translate).ToList();
        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var emitted = ir.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.FlushTerminal(failed: true));

        Assert.DoesNotContain(emitted, e => e.EventType is
            "response.custom_tool_call_input.done" or
            "response.function_call_arguments.done" or
            "response.output_item.done" or
            "response.completed" or
            "response.incomplete");
        Assert.Single(emitted, e => e.EventType == "response.failed");
    }

    [Fact]
    public void Detector_abort_does_not_restore_rejected_native_delta()
    {
        var source = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_abort","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_abort","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.output_text.delta", """{"type":"response.output_text.delta","item_id":"msg_abort","output_index":0,"content_index":0,"delta":"unsafe original"}"""),
        };
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = source.SelectMany(t3.Translate).ToList();

        // Model the response-stage contract exactly: it emits the semantic prefix,
        // replaces the unsafe semantic delta with one error event, and terminates
        // before the matching carrier can authorize restoration.
        var semanticIndex = ir.FindIndex(e => e.EventType == "content_block_delta"
            && e.Data.Contains("unsafe original", StringComparison.Ordinal));
        Assert.True(semanticIndex > 0);
        var carrierIndex = semanticIndex + 1;
        Assert.Equal(NativeResponsesEventCarrier.EventType, ir[carrierIndex].EventType);
        var inspected = ir.Take(semanticIndex).ToList();
        inspected.Add(new SseItem<string>(
            """{"type":"error","error":{"type":"overloaded_error","message":"retry"}}""",
            "error"));

        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var emitted = inspected.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.FlushTerminal(failed: false));

        Assert.DoesNotContain(emitted, e => e.Data.Contains("unsafe original", StringComparison.Ordinal));
        Assert.Single(emitted, e => e.EventType == "response.failed");
    }

    [Fact]
    public void Arbitrary_semantic_rewrite_fails_closed_and_never_resumes_native_restore()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var (ir, ledger) = NativeIr(original);
        var deltaIndex = ir.FindIndex(e => e.EventType == "content_block_delta"
            && e.Data.Contains("Audit complete.", StringComparison.Ordinal));
        Assert.True(deltaIndex > 0);
        ir[deltaIndex] = new SseItem<string>(
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"rewritten"}}""",
            "content_block_delta");

        var emitted = RunT4(ir, ledger);

        Assert.DoesNotContain(emitted, e => e.Data.Contains("Audit complete.", StringComparison.Ordinal));
        Assert.DoesNotContain(emitted, e => e.Data.Contains("response.future_extension", StringComparison.Ordinal));
        Assert.DoesNotContain(emitted, e => e.EventType is "response.completed" or "response.incomplete");
        Assert.Single(emitted, e => e.EventType == "response.failed");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Clean_native_stream_survives_each_detector_delivery_mode(
        bool wholeResponseBuffering,
        bool blockBuffering)
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var (ir, ledger) = NativeIr(original);
        var context = Context(ToAsync(ir));
        var detector = new ModeDetector(wholeResponseBuffering, blockBuffering);
        var stage = new ResponseInspectionStage(
            [detector], context, NullLogger<ResponseInspectionStage>.Instance);

        await stage.ApplyAsync();
        var inspected = await Drain(context.Response.EventStream!);
        var emitted = RunT4(inspected, ledger);

        AssertEventValuesEqual(original, emitted);
        Assert.Equal(ir.Count(e => !NativeResponsesEventCarrier.IsPrivate(e)), detector.InspectedEvents);
        Assert.Equal(0, detector.PrivateEventsInspected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_leak_detector_aborts_before_native_delta_can_be_restored(bool bufferBlocks)
    {
        const string leak = "<invoke name=\"Read\"><parameter name=\"file_path\">/secret</parameter></invoke>";
        var original = new List<SseItem<string>>
        {
            Sse("response.created", """{"type":"response.created","response":{"id":"resp_leak","status":"in_progress","model":"gpt-5.6-sol"}}"""),
            Sse("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_leak","role":"assistant","status":"in_progress","content":[]}}"""),
            Sse("response.output_text.delta", $$"""{"type":"response.output_text.delta","item_id":"msg_leak","output_index":0,"content_index":0,"delta":{{JsonSerializer.Serialize(leak)}}}"""),
            Sse("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_leak","role":"assistant","status":"completed","content":[]}}"""),
            Sse("response.completed", """{"type":"response.completed","response":{"id":"resp_leak","status":"completed","model":"gpt-5.6-sol","output":[]}}"""),
        };
        var (ir, ledger) = NativeIr(original);
        var context = Context(ToAsync(ir), tools: [new Tool { Name = "Read" }]);
        var detector = new ResponseLeakDetector(
            new DetectorOrder<ResponseLeakDetector>(0),
            TestOptions.Snapshot(new ResponseLeakGuardOptions
            {
                Enabled = true,
                PreserveStream = true,
                BufferScannableBlocks = bufferBlocks,
            }),
            context,
            NullLogger<ResponseLeakDetector>.Instance);
        var stage = new ResponseInspectionStage(
            [detector], context, NullLogger<ResponseInspectionStage>.Instance);

        await stage.ApplyAsync();
        var inspected = await Drain(context.Response.EventStream!);
        var emitted = RunT4(inspected, ledger);

        Assert.True(context.ResponseLeakDetected);
        Assert.DoesNotContain(emitted, e => e.Data.Contains(leak, StringComparison.Ordinal));
        Assert.Single(emitted, e => e.EventType == "response.failed");
        Assert.DoesNotContain(emitted, e => e.EventType is "response.completed" or "response.incomplete");
    }

    [Fact]
    public async Task Model_rewrite_patches_only_model_on_original_events()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var (ir, ledger) = NativeIr(original);
        var context = Context(ToAsync(ir));
        context.OriginalRequestedModel = "gpt-client-alias";
        var detector = new ModelRewriteDetector(
            new DetectorOrder<ModelRewriteDetector>(0),
            TestOptions.Snapshot(new ResponseModelRewriteOptions { Enabled = true }),
            context);
        var stage = new ResponseInspectionStage(
            [detector], context, NullLogger<ResponseInspectionStage>.Instance);

        await stage.ApplyAsync();
        var emitted = RunT4(await Drain(context.Response.EventStream!), ledger);

        Assert.Equal(original.Count, emitted.Count);
        for (var i = 0; i < original.Count; i++)
        {
            var expected = JsonNode.Parse(original[i].Data)!.AsObject();
            var actual = JsonNode.Parse(emitted[i].Data)!.AsObject();
            if (expected["response"] is JsonObject expectedResponse
                && expectedResponse.ContainsKey("model"))
            {
                expectedResponse["model"] = "gpt-client-alias";
                Assert.True(JsonNode.DeepEquals(expected, actual), $"event[{i}] changed beyond model rewrite");
            }
            else
            {
                Assert.True(JsonNode.DeepEquals(expected, actual), $"event[{i}] changed beyond model rewrite");
            }
        }
    }

    [Fact]
    public async Task Claude_edge_drops_private_carriers_and_keeps_semantic_events()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var (ir, _) = NativeIr(original);
        var adapter = new ClaudeCodeOutboundAdapter(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

        var emitted = await Drain(adapter.AdaptStreamAsync(ToAsync(ir), default));

        Assert.DoesNotContain(emitted, e => NativeResponsesEventCarrier.IsPrivate(e));
        Assert.Contains(emitted, e => e.EventType == "message_start");
        Assert.Contains(emitted, e => e.EventType == "content_block_delta");
        Assert.Contains(emitted, e => e.EventType == "message_stop");
    }

    [Fact]
    public async Task Codex_adapter_early_dispose_clears_unconsumed_ledger()
    {
        var original = ParseFixture("responses-sse-fidelity.txt");
        var (ir, ledger) = NativeIr(original);
        var context = Context(ToAsync(ir));
        context.Response.NativeResponsesEvents = ledger;
        var adapter = new IrToResponsesOutboundAdapter(
            context, NullLogger<IrToResponsesOutboundAdapter>.Instance);

        await using (var enumerator = adapter.AdaptStreamAsync(ToAsync(ir), default).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.True(ledger.Count > 0);
        }

        Assert.Equal(0, ledger.Count);
    }

    private static List<SseItem<string>> RoundTripNative(List<SseItem<string>> original)
    {
        var (ir, ledger) = NativeIr(original);
        return RunT4(ir, ledger);
    }

    private static SseItem<string> Sse(string type, string data) => new(data, type);

    private static (List<SseItem<string>> Events, NativeResponsesEventLedger Ledger) NativeIr(
        List<SseItem<string>> original)
    {
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            Model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = original.SelectMany(t3.Translate).ToList();
        ir.AddRange(t3.Flush());
        return (ir, ledger);
    }

    private static List<SseItem<string>> RunT4(
        IEnumerable<SseItem<string>> ir,
        NativeResponsesEventLedger ledger)
    {
        var t4 = new AnthropicToResponsesStream(Model, nativeLedger: ledger);
        var emitted = ir.SelectMany(t4.Translate).ToList();
        emitted.AddRange(t4.Flush());
        return emitted;
    }

    private static BridgeContext<MessagesRequest> Context(
        IAsyncEnumerable<SseItem<string>> stream,
        IReadOnlyList<Tool>? tools = null) =>
        new()
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/codex/responses",
                Body = new MessagesRequest
                {
                    Model = Model,
                    Messages = [],
                    Tools = tools,
                },
            },
            Response = new BridgeResponse { Mode = ResponseMode.Streaming, EventStream = stream },
            Ct = default,
        };

    private static async IAsyncEnumerable<SseItem<string>> ToAsync(IEnumerable<SseItem<string>> items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask;
    }

    private static async Task<List<SseItem<string>>> Drain(IAsyncEnumerable<SseItem<string>> stream)
    {
        var result = new List<SseItem<string>>();
        await foreach (var item in stream) result.Add(item);
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
            using var expectedDoc = JsonDocument.Parse(expected[i].Data);
            using var actualDoc = JsonDocument.Parse(actual[i].Data);
            Assert.True(JsonElement.DeepEquals(expectedDoc.RootElement, actualDoc.RootElement), $"event[{i}] changed");
        }
    }

    private sealed class ModeDetector(bool requiresBuffering, bool buffersBlocks) : IResponseDetector
    {
        public string Name => "Mode";
        public int Order => 0;
        public bool Enabled => true;
        public bool RequiresBuffering => requiresBuffering;
        public bool BuffersScannableBlocks => buffersBlocks;
        public int InspectedEvents { get; private set; }
        public int PrivateEventsInspected { get; private set; }
        public void Begin() { }
        public DetectionAction InspectEvent(in SseItem<string> evt)
        {
            InspectedEvents++;
            if (NativeResponsesEventCarrier.IsPrivate(evt)) PrivateEventsInspected++;
            return DetectionAction.None;
        }
    }

    private static List<SseItem<string>> ParseFixture(string name)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var result = new List<SseItem<string>>();
        string? eventType = null;
        var data = new System.Text.StringBuilder();
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
