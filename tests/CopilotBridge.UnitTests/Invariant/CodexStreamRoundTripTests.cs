using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// A6 — stream round-trip fidelity (change 3, task 6.5). Feeds a REAL captured
/// <c>/responses</c> SSE fixture through T3 (Responses→IR Anthropic) then T4
/// (IR→Responses), and asserts the re-emitted Responses stream preserves what
/// Codex's parser needs: the event types in order, text/argument deltas that
/// concatenate to identical content, and the terminal <c>response.completed</c>
/// with NO spurious <c>[DONE]</c>. The fixtures are real wire captures
/// (<c>tests/.../Fixtures/responses-sse-*.txt</c>), used as input samples.
/// </summary>
public class CodexStreamRoundTripTests
{
    private readonly ITestOutputHelper _output;
    public CodexStreamRoundTripTests(ITestOutputHelper output) => _output = output;

    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static List<SseItem<string>> ParseSse(string path)
    {
        var raw = File.ReadAllText(path);
        var items = new List<SseItem<string>>();
        string? evt = null;
        var data = new System.Text.StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("event:", StringComparison.Ordinal)) evt = l[6..].Trim();
            else if (l.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(l[5..].TrimStart());
            }
            else if (l.Length == 0 && (evt is not null || data.Length > 0))
            {
                items.Add(new SseItem<string>(data.ToString(), evt));
                evt = null; data.Clear();
            }
        }
        return items;
    }

    /// <summary>Run T3 (Responses→IR) over a captured stream, returning the IR events.</summary>
    private static List<SseItem<string>> RunT3(List<SseItem<string>> responsesStream, string model = "gpt-5.3-codex")
    {
        var sm = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in responsesStream) ir.AddRange(sm.Translate(e));
        ir.AddRange(sm.Flush());
        return ir;
    }

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6_T3_ProducesValidAnthropicGrammar(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var ir = RunT3(responses);

        var types = ir.Select(EventType).ToList();
        _output.WriteLine($"{fixture}: T3 IR events = {string.Join(", ", types)}");

        // Anthropic grammar: message_start first, message_stop last, balanced blocks.
        Assert.Equal("message_start", types[0]);
        Assert.Equal("message_stop", types[^1]);
        Assert.Contains("message_delta", types);
        Assert.Equal(types.Count(t => t == "content_block_start"),
                     types.Count(t => t == "content_block_stop"));
        // No [DONE] leaked in.
        Assert.DoesNotContain(ir, e => e.Data.Contains("[DONE]", StringComparison.Ordinal));
    }

    [Fact]
    public void A6_ToolStream_T3_PreservesToolCallAndArgs()
    {
        var responses = ParseSse(Path.Combine(FixturesDir, "responses-sse-toolcall.txt"));
        var ir = RunT3(responses);

        // A tool_use content_block_start must appear, and input_json_delta(s).
        Assert.Contains(ir, e => e.Data.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal));
        Assert.Contains(ir, e => e.Data.Contains("input_json_delta", StringComparison.Ordinal));
        // The terminal stop_reason should be tool_use (a tool was called).
        var delta = ir.First(e => EventType(e) == "message_delta");
        Assert.Contains("tool_use", delta.Data);
    }

    // ── Custom-tool (grammar/exec) argument streaming ─────────────────────────
    // CONTRACT: gpt-5.6's custom tools (Codex's `exec`) stream their input via
    // response.custom_tool_call_input.delta/.done — NOT function_call_arguments.*.
    // T3 must translate those to input_json_delta so the arguments reach the IR;
    // otherwise Codex receives arguments:"" and ABORTS every exec call (the live
    // bug: 239/242 custom-tool calls lost their arguments). Grounded in the live
    // trace shape (request-traces custom_tool_call_input events) + the
    // CustomToolStreamingProbe live probe.

    [Fact]
    public void CustomTool_T3_TranslatesInputDeltasToInputJsonDelta()
    {
        var ir = RunT3(SyntheticCustomToolStream(
            callId: "call_abc", name: "exec",
            inputFragments: ["const r = ", "await tools.shell({cmd:\"ls\"});"]));

        // Opens a tool_use block for the custom tool...
        Assert.Contains(ir, e => e.Data.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal)
            && e.Data.Contains("\"name\":\"exec\"", StringComparison.Ordinal)
            && e.Data.Contains("\"id\":\"call_abc\"", StringComparison.Ordinal));
        // ...and STREAMS the input as PER-FRAGMENT input_json_delta events — one per
        // upstream delta, not a single done-fallback blob. Two fragments in ⇒ two
        // input_json_delta out; this distinguishes the delta path from the .done
        // fallback (so disabling the delta handler is caught even though .done would
        // otherwise mask it).
        var deltaCount = ir.Count(e => e.Data.Contains("input_json_delta", StringComparison.Ordinal));
        Assert.Equal(2, deltaCount);
        // Terminal stop is tool_use.
        var delta = ir.First(e => EventType(e) == "message_delta");
        Assert.Contains("tool_use", delta.Data);

        // The concatenated input_json_delta partial_json equals the original input.
        Assert.Equal("const r = await tools.shell({cmd:\"ls\"});", ConcatIrInputJson(ir));
    }

    [Fact]
    public void CustomTool_FullRoundTrip_ArgumentsReachCodexNonEmpty()
    {
        // The end-to-end regression: T3→T4 must put the custom tool's full input on
        // the Codex-facing function_call_arguments.done AND the function_call output
        // item — NOT an empty string (which caused `aborted`).
        const string fullInput = "const r = await tools.shell({cmd:\"ls\"});";
        var roundTripped = RunT3ThenT4(SyntheticCustomToolStream(
            callId: "call_abc", name: "exec",
            inputFragments: ["const r = ", "await tools.shell({cmd:\"ls\"});"]));

        var doneArgs = ExtractFunctionCallArgumentsDone(roundTripped);
        Assert.Equal(fullInput, doneArgs);
        Assert.NotEqual("", doneArgs);

        // And the function_call output item (output_item.done) carries the same
        // non-empty arguments — this is what Codex reads to execute the tool.
        var itemArgs = ExtractFunctionCallOutputItemArguments(roundTripped);
        Assert.Equal(fullInput, itemArgs);
    }

    [Fact]
    public void CustomTool_DoneOnly_NoDeltas_StillCarriesInput()
    {
        // Robustness: if a stream sends the full input only on .done (no deltas),
        // T3's .done fallback must still emit it so the call isn't empty.
        const string fullInput = "print('hi')";
        var ir = RunT3(SyntheticCustomToolStream(
            callId: "call_z", name: "exec", inputFragments: [], doneInput: fullInput));

        Assert.Contains(ir, e => e.Data.Contains("input_json_delta", StringComparison.Ordinal));
        Assert.Equal(fullInput, ConcatIrInputJson(ir));
    }

    [Fact]
    public void CustomTool_RealCapture_ArgumentsRoundTripEqualUpstream()
    {
        // A minimized, de-identified capture of a gpt-5.6-sol /responses SSE carrying
        // a custom `exec` tool call (the committed fixture trims the real 232-delta /
        // 386-char session to a handful of neutral-JS fragments — same event grammar,
        // no session content). Before the fix, T3 swallowed these and Codex got
        // arguments:"" → aborted. Assert the arguments the round trip re-emits to
        // Codex equal the upstream custom-tool input exactly.
        var fixture = Path.Combine(FixturesDir, "responses-sse-customtool.txt");
        var responses = ParseSse(fixture);

        // Upstream custom-tool input = concatenated custom_tool_call_input.delta
        // fragments (equivalently the .done `input`).
        var upstreamInput = ConcatCustomToolInput(responses);
        Assert.False(string.IsNullOrEmpty(upstreamInput), "fixture carried no custom-tool input");

        var roundTripped = RunT3ThenT4(responses, model: "gpt-5.6-sol");
        var codexArgs = ExtractFunctionCallArgumentsDone(roundTripped);

        Assert.Equal(upstreamInput, codexArgs);
        Assert.NotEqual("", codexArgs);
    }

    [Fact]
    public void FunctionTool_DoneOnly_NoDeltas_StillCarriesArguments()
    {
        // Mirror of the custom-tool .done fallback, for a FUNCTION tool: a stream
        // that delivers the arguments only on function_call_arguments.done (zero
        // deltas) must still emit them once — guards the symmetric fallback so
        // removing it reddens the suite (it otherwise stays green, as every other
        // function-tool test supplies deltas).
        const string fullArgs = "{\"path\":\"/tmp/x\"}";
        var ir = RunT3(SyntheticFunctionToolStream(
            callId: "call_fn", name: "read", argFragments: [], doneArgs: fullArgs));

        // Exactly one input_json_delta (the fallback), carrying the full arguments.
        var deltaCount = ir.Count(e => e.Data.Contains("input_json_delta", StringComparison.Ordinal));
        Assert.Equal(1, deltaCount);
        Assert.Equal(fullArgs, ConcatIrInputJson(ir));
    }

    [Fact]
    public void TwoToolCalls_FirstDeltas_SecondDoneOnly_BothArgsSurviveOnce()
    {
        // Back-to-back tool calls in ONE response: the first streams via deltas, the
        // second sends its input only on .done. This guards the _blockSawArgsDelta
        // RESET on each new output item — without the reset, the first call's
        // "saw deltas" state would leak and suppress the second call's .done
        // fallback, silently dropping its arguments. Assert both survive exactly once.
        const string firstArgs = "const a = 1;";
        const string secondArgs = "print('two')";
        var stream = TwoCustomToolStream(
            first: ("call_1", "exec", ["const a = ", "1;"]),
            second: ("call_2", "exec", secondArgs));   // second: done-only

        var ir = RunT3(stream);

        // Two tool_use blocks opened.
        Assert.Equal(2, ir.Count(e => e.Data.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal)));
        // Each call's input survives EXACTLY once (concatenated per block).
        var perBlock = ConcatIrInputJsonPerBlock(ir);
        Assert.Equal(2, perBlock.Count);
        Assert.Equal(firstArgs, perBlock[0]);
        Assert.Equal(secondArgs, perBlock[1]);
    }

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6_FullRoundTrip_T3ThenT4_BackToResponses(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var ir = RunT3(responses);

        // T4: IR → Responses.
        var t4 = new AnthropicToResponsesStream("gpt-5.3-codex");
        var roundTripped = new List<SseItem<string>>();
        foreach (var e in ir) roundTripped.AddRange(t4.Translate(e));
        roundTripped.AddRange(t4.Flush());

        var types = roundTripped.Select(EventType).ToList();
        _output.WriteLine($"{fixture}: T3→T4 Responses events = {string.Join(", ", types)}");

        // Must open with response.created and end with response.completed (Codex
        // parser requirement), and never emit [DONE].
        Assert.Equal("response.created", types[0]);
        Assert.Equal("response.completed", types[^1]);
        Assert.DoesNotContain(roundTripped, e => e.Data.Contains("[DONE]", StringComparison.Ordinal));
    }

    // ── A6c — usage cache/reasoning sub-counts survive T3→T4 ──────────────────
    // CONTRACT: the Codex /responses path is a near-passthrough, so the usage
    // Codex sees on response.completed must equal Copilot's upstream usage —
    // including input_tokens_details.cached_tokens (drives Codex's prompt-cache
    // telemetry) and output_tokens_details.reasoning_tokens. The Responses→IR→
    // Responses hub round-trip previously dropped both (T3 never captured them,
    // T4 hard-coded 0), so Codex showed 0% cache even when Copilot served the
    // whole prefix from cache. The real fixtures carry reasoning_tokens=32/8.

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void A6c_Usage_CacheAndReasoning_RoundTripEqualsUpstream(string fixture)
    {
        var responses = ParseSse(Path.Combine(FixturesDir, fixture));
        var upstream = ExtractCompletedUsage(responses);
        var emitted = ExtractCompletedUsage(RunT3ThenT4(responses));

        Assert.Equal(upstream.Input, emitted.Input);
        Assert.Equal(upstream.Output, emitted.Output);
        Assert.Equal(upstream.Cached, emitted.Cached);        // was forced to 0 (the bug)
        Assert.Equal(upstream.Reasoning, emitted.Reasoning);  // was forced to 0 (the bug)
        Assert.Equal(upstream.Total, emitted.Total);
        Assert.True(upstream.Reasoning > 0, "fixture should carry reasoning_tokens (guards the assertion)");
    }

    [Fact]
    public void A6c_HighCacheHit_CachedTokensSurvive()
    {
        // Mirror a real multi-turn Codex trace: Copilot served ~99.9% of the
        // prompt from cache (input_tokens=109589, cached_tokens=109440). Codex's
        // terminal must carry that, not cached_tokens=0.
        var emitted = ExtractCompletedUsage(
            RunT3ThenT4(SyntheticCompletedStream(input: 109589, cached: 109440, output: 315, reasoning: 0)));

        Assert.Equal(109589, emitted.Input);
        Assert.Equal(109440, emitted.Cached);   // the cache hit Codex was blind to
        Assert.Equal(315, emitted.Output);
        Assert.Equal(0, emitted.Reasoning);
        Assert.Equal(109904, emitted.Total);    // input + output; cached is a subset, not added
    }

    [Fact]
    public void A6c_MissingUsageDetails_DefaultToZero_NoInvention()
    {
        // Copilot omits input_tokens_details/output_tokens_details on a turn with
        // no cache hit and no reasoning. The round trip must not crash on the
        // absent sub-objects and must NOT invent counts — cached/reasoning = 0,
        // totals preserved.
        var emitted = ExtractCompletedUsage(
            RunT3ThenT4(SyntheticTerminalStream("response.completed", "completed",
                input: 2000, cached: 0, output: 10, reasoning: 0, withDetails: false)));

        Assert.Equal(2000, emitted.Input);
        Assert.Equal(0, emitted.Cached);
        Assert.Equal(10, emitted.Output);
        Assert.Equal(0, emitted.Reasoning);
        Assert.Equal(2010, emitted.Total);
    }

    [Fact]
    public void A6c_IncompleteTerminal_CacheSurvivesAndStatusIncomplete()
    {
        // A max_tokens turn arrives as response.incomplete (a different T3 branch
        // than response.completed) — the cache sub-count must still survive, and
        // T4 must mark the terminal status incomplete (not completed).
        var stream = RunT3ThenT4(SyntheticTerminalStream("response.incomplete", "incomplete",
            input: 50000, cached: 49000, output: 256, reasoning: 64));

        var usage = ExtractCompletedUsage(stream);
        Assert.Equal(50000, usage.Input);
        Assert.Equal(49000, usage.Cached);    // cache hit survives the incomplete path too
        Assert.Equal(64, usage.Reasoning);
        Assert.Equal(50256, usage.Total);
        Assert.Equal("incomplete", ExtractTerminalStatus(stream));
    }

    /// <summary>Run T3 (Responses→IR) then T4 (IR→Responses), returning the re-emitted stream.</summary>
    private static List<SseItem<string>> RunT3ThenT4(List<SseItem<string>> responsesStream, string model = "gpt-5.3-codex")
    {
        var ir = RunT3(responsesStream, model);
        var t4 = new AnthropicToResponsesStream(model);
        var outp = new List<SseItem<string>>();
        foreach (var e in ir) outp.AddRange(t4.Translate(e));
        outp.AddRange(t4.Flush());
        return outp;
    }

    /// <summary>Minimal Responses SSE carrying only the terminal usage (no output items).</summary>
    private static List<SseItem<string>> SyntheticCompletedStream(long input, long cached, long output, long reasoning) =>
        SyntheticTerminalStream("response.completed", "completed", input, cached, output, reasoning);

    /// <summary>
    /// Minimal Responses SSE (response.created + a terminal) carrying only usage.
    /// <paramref name="withDetails"/>=false omits the *_details sub-objects, the
    /// shape Copilot sends for a turn with no cache hit / no reasoning.
    /// </summary>
    private static List<SseItem<string>> SyntheticTerminalStream(
        string terminalType, string status, long input, long cached, long output, long reasoning, bool withDetails = true)
    {
        var usage = withDetails
            ? $"{{\"input_tokens\":{input},\"input_tokens_details\":{{\"cached_tokens\":{cached}}},"
              + $"\"output_tokens\":{output},\"output_tokens_details\":{{\"reasoning_tokens\":{reasoning}}},"
              + $"\"total_tokens\":{input + output}}}"
            : $"{{\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{input + output}}}";
        return
        [
            new SseItem<string>("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new SseItem<string>(
                $"{{\"type\":\"{terminalType}\",\"response\":{{\"id\":\"r\",\"status\":\"{status}\",\"usage\":{usage}}}}}",
                terminalType),
        ];
    }

    /// <summary>Read the status string off the terminal event (completed/incomplete/failed).</summary>
    private static string ExtractTerminalStatus(List<SseItem<string>> stream)
    {
        foreach (var e in stream)
        {
            if (EventType(e) is not ("response.completed" or "response.incomplete" or "response.failed")) continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("status", out var s))
                return s.GetString() ?? "";
        }
        return "";
    }

    /// <summary>Pull the usage off the terminal response.completed/incomplete/failed event.</summary>
    private static (long Input, long Cached, long Output, long Reasoning, long Total) ExtractCompletedUsage(
        List<SseItem<string>> stream)
    {
        static long Scalar(JsonElement o, string p) =>
            o.TryGetProperty(p, out var v) && v.TryGetInt64(out var n) ? n : 0;
        static long Nested(JsonElement o, string p, string c) =>
            o.TryGetProperty(p, out var d) && d.ValueKind == JsonValueKind.Object ? Scalar(d, c) : 0;

        foreach (var e in stream)
        {
            if (EventType(e) is not ("response.completed" or "response.incomplete" or "response.failed")) continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (!doc.RootElement.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object) continue;
            return (
                Scalar(usage, "input_tokens"),
                Nested(usage, "input_tokens_details", "cached_tokens"),
                Scalar(usage, "output_tokens"),
                Nested(usage, "output_tokens_details", "reasoning_tokens"),
                Scalar(usage, "total_tokens"));
        }
        throw new Xunit.Sdk.XunitException("no terminal event with response.usage found");
    }

    [Fact]
    public void A6_TextDeltas_ConcatenateToIdenticalText()
    {
        // The text the IR carries (T3) must equal the text the original Responses
        // stream carried — delta concatenation is lossless.
        var responses = ParseSse(Path.Combine(FixturesDir, "responses-sse-text.txt"));
        var originalText = ConcatResponsesText(responses);

        var ir = RunT3(responses);
        var irText = ConcatIrText(ir);

        Assert.Equal(originalText, irText);
        Assert.False(string.IsNullOrEmpty(originalText), "fixture carried no text deltas");
    }

    private static string ConcatResponsesText(List<SseItem<string>> stream)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in stream)
        {
            if (EventType(e) != "response.output_text.delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                sb.Append(d.GetString());
        }
        return sb.ToString();
    }

    private static string ConcatIrText(List<SseItem<string>> ir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in ir)
        {
            if (EventType(e) != "content_block_delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("delta", out var d)
                && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                && d.TryGetProperty("text", out var tx))
                sb.Append(tx.GetString());
        }
        return sb.ToString();
    }

    private static string EventType(SseItem<string> e)
    {
        // Prefer the data's "type" (authoritative), fall back to the SSE event field.
        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("type", out var t)) return t.GetString() ?? e.EventType ?? "";
        }
        catch (JsonException) { }
        return e.EventType ?? "";
    }

    /// <summary>
    /// Build a minimal Responses SSE for a CUSTOM tool call, mirroring the live
    /// shape (request-traces + CustomToolStreamingProbe): response.created →
    /// output_item.added (item.type=custom_tool_call, with call_id/name) →
    /// custom_tool_call_input.delta × N → custom_tool_call_input.done (field
    /// <c>input</c>) → output_item.done → response.completed. When
    /// <paramref name="inputFragments"/> is empty, only the <c>.done</c> carries the
    /// input (<paramref name="doneInput"/>) — the no-delta fallback case.
    /// </summary>
    private static List<SseItem<string>> SyntheticCustomToolStream(
        string callId, string name, string[] inputFragments, string? doneInput = null)
    {
        var full = doneInput ?? string.Concat(inputFragments);
        var items = new List<SseItem<string>>
        {
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new($"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"input\":\"\",\"status\":\"in_progress\"}}}}",
                "response.output_item.added"),
        };
        foreach (var frag in inputFragments)
            items.Add(new($"{{\"type\":\"response.custom_tool_call_input.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"delta\":{JsonEncode(frag)}}}",
                "response.custom_tool_call_input.delta"));
        items.Add(new($"{{\"type\":\"response.custom_tool_call_input.done\",\"item_id\":\"item_1\",\"output_index\":0,\"input\":{JsonEncode(full)}}}",
            "response.custom_tool_call_input.done"));
        items.Add(new($"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"input\":{JsonEncode(full)},\"status\":\"completed\"}}}}",
            "response.output_item.done"));
        items.Add(new("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
            "response.completed"));
        return items;
    }

    /// <summary>
    /// Minimal Responses SSE for a FUNCTION tool call (item.type=function_call,
    /// events function_call_arguments.delta/.done, field <c>arguments</c>). Empty
    /// <paramref name="argFragments"/> ⇒ only the <c>.done</c> carries the arguments
    /// (<paramref name="doneArgs"/>) — the no-delta fallback case.
    /// </summary>
    private static List<SseItem<string>> SyntheticFunctionToolStream(
        string callId, string name, string[] argFragments, string? doneArgs = null)
    {
        var full = doneArgs ?? string.Concat(argFragments);
        var items = new List<SseItem<string>>
        {
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
            new($"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{{\"type\":\"function_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"arguments\":\"\",\"status\":\"in_progress\"}}}}",
                "response.output_item.added"),
        };
        foreach (var frag in argFragments)
            items.Add(new($"{{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"delta\":{JsonEncode(frag)}}}",
                "response.function_call_arguments.delta"));
        items.Add(new($"{{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_1\",\"output_index\":0,\"arguments\":{JsonEncode(full)}}}",
            "response.function_call_arguments.done"));
        items.Add(new($"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"function_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"arguments\":{JsonEncode(full)},\"status\":\"completed\"}}}}",
            "response.output_item.done"));
        items.Add(new("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
            "response.completed"));
        return items;
    }

    /// <summary>
    /// A single Responses stream with TWO custom-tool calls: the first streams via
    /// deltas, the second sends its input only on <c>.done</c> (no deltas). Each item
    /// uses a distinct output_index/id so T3 opens two separate blocks.
    /// </summary>
    private static List<SseItem<string>> TwoCustomToolStream(
        (string callId, string name, string[] fragments) first,
        (string callId, string name, string doneOnlyInput) second)
    {
        var items = new List<SseItem<string>>
        {
            new("{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}", "response.created"),
        };
        // First call — deltas.
        items.Add(new($"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(first.callId)},\"name\":{JsonEncode(first.name)},\"input\":\"\",\"status\":\"in_progress\"}}}}",
            "response.output_item.added"));
        foreach (var frag in first.fragments)
            items.Add(new($"{{\"type\":\"response.custom_tool_call_input.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"delta\":{JsonEncode(frag)}}}",
                "response.custom_tool_call_input.delta"));
        items.Add(new($"{{\"type\":\"response.custom_tool_call_input.done\",\"item_id\":\"item_1\",\"output_index\":0,\"input\":{JsonEncode(string.Concat(first.fragments))}}}",
            "response.custom_tool_call_input.done"));
        items.Add(new($"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_1\",\"call_id\":{JsonEncode(first.callId)},\"name\":{JsonEncode(first.name)},\"input\":{JsonEncode(string.Concat(first.fragments))},\"status\":\"completed\"}}}}",
            "response.output_item.done"));
        // Second call — done-only (no deltas).
        items.Add(new($"{{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_2\",\"call_id\":{JsonEncode(second.callId)},\"name\":{JsonEncode(second.name)},\"input\":\"\",\"status\":\"in_progress\"}}}}",
            "response.output_item.added"));
        items.Add(new($"{{\"type\":\"response.custom_tool_call_input.done\",\"item_id\":\"item_2\",\"output_index\":1,\"input\":{JsonEncode(second.doneOnlyInput)}}}",
            "response.custom_tool_call_input.done"));
        items.Add(new($"{{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{{\"type\":\"custom_tool_call\",\"id\":\"item_2\",\"call_id\":{JsonEncode(second.callId)},\"name\":{JsonEncode(second.name)},\"input\":{JsonEncode(second.doneOnlyInput)},\"status\":\"completed\"}}}}",
            "response.output_item.done"));
        items.Add(new("{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
            "response.completed"));
        return items;
    }

    /// <summary>Concatenate every input_json_delta partial_json fragment in an IR stream.</summary>
    private static string ConcatIrInputJson(List<SseItem<string>> ir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in ir)
        {
            if (EventType(e) != "content_block_delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("delta", out var d)
                && d.TryGetProperty("type", out var dt) && dt.GetString() == "input_json_delta"
                && d.TryGetProperty("partial_json", out var pj))
                sb.Append(pj.GetString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Concatenate input_json_delta fragments PER tool_use block (keyed by the block
    /// index the content_block_start/_delta carry), returned in block order — so a
    /// multi-tool-call stream can be checked call-by-call.
    /// </summary>
    private static List<string> ConcatIrInputJsonPerBlock(List<SseItem<string>> ir)
    {
        var byIndex = new SortedDictionary<int, System.Text.StringBuilder>();
        foreach (var e in ir)
        {
            if (EventType(e) != "content_block_delta") continue;
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("delta", out var d)
                || !d.TryGetProperty("type", out var dt) || dt.GetString() != "input_json_delta"
                || !d.TryGetProperty("partial_json", out var pj)) continue;
            var idx = root.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : 0;
            if (!byIndex.TryGetValue(idx, out var sb)) { sb = new System.Text.StringBuilder(); byIndex[idx] = sb; }
            sb.Append(pj.GetString());
        }
        return byIndex.Values.Select(sb => sb.ToString()).ToList();
    }

    /// <summary>Read the arguments off a Responses function_call_arguments.done event.</summary>
    private static string ExtractFunctionCallArgumentsDone(List<SseItem<string>> stream)
    {
        foreach (var e in stream)
        {
            if (EventType(e) != "response.function_call_arguments.done") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                return a.GetString() ?? "";
        }
        throw new Xunit.Sdk.XunitException("no response.function_call_arguments.done event found");
    }

    /// <summary>Read the arguments off the function_call output_item.done item.</summary>
    private static string ExtractFunctionCallOutputItemArguments(List<SseItem<string>> stream)
    {
        foreach (var e in stream)
        {
            if (EventType(e) != "response.output_item.done") continue;
            using var doc = JsonDocument.Parse(e.Data);
            if (doc.RootElement.TryGetProperty("item", out var item)
                && item.TryGetProperty("type", out var it) && it.GetString() == "function_call"
                && item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                return a.GetString() ?? "";
        }
        throw new Xunit.Sdk.XunitException("no function_call output_item.done event found");
    }

    /// <summary>Concatenate the FIRST custom-tool call's input from its deltas (up to its .done).</summary>
    private static string ConcatCustomToolInput(List<SseItem<string>> stream)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in stream)
        {
            var et = EventType(e);
            if (et == "response.custom_tool_call_input.delta")
            {
                using var doc = JsonDocument.Parse(e.Data);
                if (doc.RootElement.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    sb.Append(d.GetString());
            }
            else if (et == "response.custom_tool_call_input.done")
            {
                // Stop at the first call's .done — ExtractFunctionCallArgumentsDone
                // reads the first function_call_arguments.done symmetrically.
                break;
            }
        }
        return sb.ToString();
    }

    private static string JsonEncode(string s) => JsonSerializer.Serialize(s);
}
