using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Behavioural contract tests for <see cref="ToolInputValidationDetector"/> — the
/// response detector that validates a real <c>tool_use</c> block's reassembled input
/// against the request's declared tool schema.
/// </summary>
/// <remarks>
/// <para>
/// The detector is <b>observe-by-default</b>: it records
/// <c>tool_input_invalid=true</c> but relays the response unchanged, because Claude
/// Code self-heals an invalid tool call. The <c>Observe_*</c> tests pin that default;
/// the abort-machinery tests below opt both classes into an <c>Abort*</c> action via
/// the <see cref="Detector"/> / <see cref="RunStage"/> helpers.
/// </para>
/// <para>Two families:
/// <list type="bullet">
/// <item><b>Detector-level</b> (via <see cref="Feed"/>): push SSE events straight at
/// the detector and assert the returned <see cref="DetectionAction"/>s plus
/// <see cref="BridgeContext{T}.ToolInputInvalidDetected"/>.</item>
/// <item><b>Stage-level</b> (via <see cref="RunStage"/>): drive the real
/// <see cref="ResponseInspectionStage"/> and assert the exact event sequence a client
/// receives — a malformed block's <c>content_block_stop</c> is never delivered when an
/// <c>Abort*</c> action is set (Claude Code commits a content block only at
/// <c>content_block_stop</c>).</item>
/// </list>
/// </para>
/// </remarks>
public class ToolInputValidationDetectorTests
{
    /// <summary>
    /// Tests: a well-formed tool call whose input satisfies the declared schema is
    /// passed through untouched (the happy path — the guard must not fire on good input).
    /// Input: a streamed <c>AskUserQuestion</c> block whose input has every nested
    /// required field (<c>question</c>/<c>header</c>/<c>options</c>/<c>multiSelect</c>).
    /// Expects: every <c>InspectEvent</c> returns <see cref="DetectionActionKind.None"/>
    /// and <c>ToolInputInvalidDetected</c> stays false.
    /// </summary>
    [Fact]
    public async Task ValidToolInput_PassesThroughUnchanged()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    // --- Observe-by-default contract: the shipped default (both actions Observe) must
    // NOT abort an invalid tool call, because Claude Code self-heals it. It still
    // records tool_input_invalid=true for observability. This is the crux of the fix
    // for the AskUserQuestion "Server error mid-response" regression.

    /// <summary>
    /// Tests: with the DEFAULT options (both actions Observe), a schema-invalid tool
    /// call is relayed unchanged — no abort — but the summary flag is still set.
    /// Input: an <c>AskUserQuestion</c> whose question omits the required <c>question</c>
    /// field (the exact real-world AskUserQuestion regression), through a default-configured
    /// detector.
    /// Expects: every action is <see cref="DetectionActionKind.None"/> (CC's self-heal is
    /// not cut off) while <c>ToolInputInvalidDetected</c> is true (observed).
    /// </summary>
    [Fact]
    public async Task Observe_SchemaViolation_RelaysButRecordsFlag()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = DefaultDetector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: with the DEFAULT options, malformed-JSON tool input is also relayed
    /// unchanged (CC's safeParseJSON falls back to {} and re-prompts), flag still set.
    /// Input: an <c>AskUserQuestion</c> whose fragments close into truncated JSON,
    /// through a default-configured detector.
    /// Expects: no abort; <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task Observe_MalformedJson_RelaysButRecordsFlag()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"oops"}"""));
        var detector = DefaultDetector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: the two classes are independently configurable — Observe on schema
    /// violations, Abort on malformed JSON — and a schema violation with that mix is
    /// NOT aborted.
    /// Input: a schema-invalid (but valid-JSON) <c>AskUserQuestion</c> with
    /// <c>SchemaViolationAction=Observe</c>, <c>MalformedJsonAction=AbortOverloaded</c>.
    /// Expects: no abort (the malformed-JSON action does not apply to a schema violation),
    /// flag set.
    /// </summary>
    [Fact]
    public async Task PerClassActions_SchemaObserveMalformedAbort_SchemaViolationNotAborted()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions
            {
                Enabled = true,
                MalformedJsonAction = ToolInputAction.AbortOverloaded,
                SchemaViolationAction = ToolInputAction.Observe,
            }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests (mirror of the above): with <c>MalformedJsonAction=Observe</c>,
    /// <c>SchemaViolationAction=AbortOverloaded</c>, malformed JSON is relayed (its class
    /// is Observe) while a schema violation under the SAME config aborts — proving each
    /// class reads its own knob, in the opposite direction.
    /// </summary>
    [Fact]
    public async Task PerClassActions_MalformedObserveSchemaAbort_MalformedRelayed_SchemaAborts()
    {
        var opts = new ToolInputValidationOptions
        {
            Enabled = true,
            MalformedJsonAction = ToolInputAction.Observe,
            SchemaViolationAction = ToolInputAction.AbortOverloaded,
        };

        // Malformed JSON → its class is Observe → relayed, no abort.
        var malformedCtx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"oops"}"""));
        var d1 = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4), TestOptions.Snapshot(opts),
            malformedCtx, NullLogger<ToolInputValidationDetector>.Instance);
        d1.Begin();
        var malformedActions = await Feed(d1, malformedCtx.Response.EventStream!);
        Assert.All(malformedActions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.True(malformedCtx.ToolInputInvalidDetected);

        // Schema violation (valid JSON) → its class is Abort → aborts.
        var schemaCtx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        var d2 = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4), TestOptions.Snapshot(opts),
            schemaCtx, NullLogger<ToolInputValidationDetector>.Instance);
        d2.Begin();
        var schemaActions = await Feed(d2, schemaCtx.Response.EventStream!);
        Assert.Contains(schemaActions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(schemaCtx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: an observe-only configuration never forces whole-response buffering
    /// (<c>RequiresBuffering</c> false), so it adds no latency on the default path.
    /// Input: a default detector, and one with only <c>SchemaViolationAction=AbortOverloaded</c>
    /// but <c>PreserveStream=false</c>.
    /// Expects: default → <c>RequiresBuffering</c> false; the abort+PreserveStream=false
    /// one → true (buffering is paid only when an abort actually needs a real status).
    /// </summary>
    [Fact]
    public void RequiresBuffering_OnlyWhenAbortAndNotPreserveStream()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", "{}"));
        Assert.False(DefaultDetector(ctx).RequiresBuffering);

        var abortBuffered = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions
            {
                Enabled = true,
                SchemaViolationAction = ToolInputAction.AbortOverloaded,
                PreserveStream = false,
            }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);
        Assert.True(abortBuffered.RequiresBuffering);

        var observeBuffered = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions { Enabled = true, PreserveStream = false }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);
        Assert.False(observeBuffered.RequiresBuffering); // observe-only never buffers
    }

    /// Input: an <c>AskUserQuestion</c> block whose question object omits the nested
    /// required <c>question</c> field (schema-valid JSON, schema-invalid content).
    /// Expects: exactly one <see cref="DetectionActionKind.Abort"/>;
    /// <c>ToolInputInvalidDetected</c> true; abort HTTP status 529 and the error JSON
    /// names <c>overloaded_error</c> (the default <c>OverloadedError</c> signal).
    /// </summary>
    [Fact]
    public async Task MissingNestedRequiredField_AbortsAndMarksContext()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);
        var abort = actions.Single(a => a.Kind == DetectionActionKind.Abort);

        Assert.True(ctx.ToolInputInvalidDetected);
        Assert.Equal(529, abort.HttpStatus);
        Assert.Contains("overloaded_error", abort.ErrorJson);
    }

    /// <summary>
    /// Tests: when the reassembled tool input is not even valid JSON, the guard trips
    /// (this fires regardless of any schema — a broken JSON payload can never be parsed).
    /// Input: an <c>AskUserQuestion</c> block whose accumulated fragments close into
    /// truncated JSON (<c>{"questions":[{"question":"oops"}</c> — unbalanced braces).
    /// Expects: an <see cref="DetectionActionKind.Abort"/> and
    /// <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task MalformedJson_AbortsAndMarksContext()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"oops"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: a value whose JSON type disagrees with the schema type is rejected, and
    /// the detector does not silently "repair" it (e.g. by unwrapping the string). This
    /// guards the deliberate no-repair policy.
    /// Input: an <c>AskUserQuestion</c> block where <c>questions</c> is the string
    /// <c>"[]"</c> instead of an array (schema declares <c>type: array</c>).
    /// Expects: an <see cref="DetectionActionKind.Abort"/> and
    /// <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task StringifiedArrayForArrayProperty_AbortsInsteadOfRepairing()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":"[]"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: the detector recognises a tool block whose start payload uses the legacy
    /// <c>start</c> field name (instead of <c>content_block</c>) and still validates it.
    /// This pins backward compatibility of block-start parsing.
    /// Input: a stream whose <c>content_block_start</c> carries the tool_use under a
    /// <c>start</c> key, with schema-invalid input (<c>questions</c> as a string).
    /// Expects: an <see cref="DetectionActionKind.Abort"/> and
    /// <c>ToolInputInvalidDetected</c> true — i.e. the legacy shape is not ignored.
    /// </summary>
    [Fact]
    public async Task LegacyStartField_IsAcceptedForToolBlockStart()
    {
        var ctx = Context(ToolSchema(), LegacyStartToolStream("AskUserQuestion", """{"questions":"[]"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: duplicate tool names in the request's tool list do not throw during
    /// <c>Begin()</c> (the name-keyed lookup keeps the first and skips duplicates).
    /// Input: two identical <c>AskUserQuestion</c> schemas plus a valid tool call.
    /// Expects: no exception; all actions <see cref="DetectionActionKind.None"/> and
    /// <c>ToolInputInvalidDetected</c> false (the valid input still passes).
    /// </summary>
    [Fact]
    public async Task DuplicateToolNames_DoNotCrashDetector()
    {
        var tools = ToolSchema().Concat(ToolSchema()).ToArray();
        var ctx = Context(tools, ToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }


    /// <summary>
    /// Tests: when the streamed tool name is not among the request's declared tools, the
    /// detector cannot know its schema, so it only enforces the minimal invariant "input
    /// is a JSON object" and otherwise passes through (fail-open on unknown tools).
    /// Input: a tool call named <c>OtherTool</c> (not in the schema list) with an
    /// arbitrary but well-formed object input.
    /// Expects: all actions <see cref="DetectionActionKind.None"/> and
    /// <c>ToolInputInvalidDetected</c> false.
    /// </summary>
    [Fact]
    public async Task UnknownToolSchema_OnlyRequiresInputObject()
    {
        var ctx = Context(ToolSchema(), ToolStream("OtherTool", """{"anything":"goes"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: the buffered (non-streaming) entry point validates a <c>tool_use</c> block
    /// found in a whole response body and returns a real abort with an HTTP status.
    /// Input: a buffered JSON body containing an <c>AskUserQuestion</c> tool_use whose
    /// nested question omits the required <c>question</c> field; detector configured with
    /// <c>PreserveStream=false</c>.
    /// Expects: <see cref="ToolInputValidationDetector.InspectBuffered"/> returns an
    /// <see cref="DetectionActionKind.Abort"/> with HTTP 529 and
    /// <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public void BufferedInvalidToolInput_AbortsWithRealStatus()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""
        {"content":[{"type":"tool_use","id":"toolu_1","name":"AskUserQuestion","input":{"questions":[{"header":"DB","options":[],"multiSelect":false}]}}]}
        """);
        var ctx = Context(ToolSchema(), AsyncEnumerable(Array.Empty<SseItem<string>>()));
        ctx.Response.Mode = ResponseMode.Buffered;
        ctx.Response.BufferedBody = body;
        var detector = Detector(ctx, preserveStream: false);
        detector.Begin();

        var action = detector.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.Equal(529, action.HttpStatus);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    // --- End-to-end through ResponseInspectionStage: the contract the user actually
    // depends on is that a malformed tool block does NOT enter Claude Code's context.
    // Claude Code commits a content block into its message list only when it receives
    // content_block_stop; the stage's Abort replaces that stop with an `error` event
    // and ends the stream. So the delivered sequence must carry the error and must
    // NOT carry the bad block's content_block_stop (nor message_stop) — that is what
    // keeps the block out of context. These drive the real stage, not the detector
    // in isolation, so an inverted Enabled/RequiresBuffering or a broken stage-render
    // would be caught here (the direct-call tests above would not catch those).

    /// <summary>
    /// Tests (stage-level, the core no-context-pollution guarantee): a malformed tool
    /// block driven through the real stage with the default <c>PreserveStream=true</c>
    /// has its <c>content_block_stop</c> replaced by a terminal <c>error</c> event, so
    /// Claude Code never commits the block.
    /// Input: a complete stream (<c>message_start</c> … tool block with a nested-required
    /// violation … <c>message_stop</c>) run through <see cref="RunStage"/>.
    /// Expects: the delivered sequence contains NO <c>content_block_stop</c> and NO
    /// <c>message_stop</c>; the last (and only) event is a single <c>error</c> whose data
    /// names <c>overloaded_error</c>; and <c>ToolInputInvalidDetected</c> is true.
    /// </summary>
    [Fact]
    public async Task Streaming_MalformedTool_StageDropsStop_AndInjectsError_NoContextCommit()
    {
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true);

        var delivered = await Drain(ctx.Response.EventStream!);

        // The bad block is kept out of context: its content_block_stop is never
        // delivered (CC only commits at stop), and neither is message_stop.
        Assert.DoesNotContain(delivered, e => e.EventType == "content_block_stop");
        Assert.DoesNotContain(delivered, e => e.EventType == "message_stop");
        // Exactly one terminal error event, and it is last (the stream ended there).
        Assert.Equal("error", delivered[^1].EventType);
        Assert.Contains("overloaded_error", delivered[^1].Data);
        Assert.Single(delivered, e => e.EventType == "error");
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests (stage-level, negative case): a valid tool response streams through the
    /// stage completely untouched — the guard adds nothing on the happy path.
    /// Input: a complete stream carrying a schema-valid <c>AskUserQuestion</c> tool call.
    /// Expects: no <c>error</c> event; the final event is <c>message_stop</c>; the block's
    /// <c>content_block_stop</c> IS delivered; <c>ToolInputInvalidDetected</c> false.
    /// </summary>
    [Fact]
    public async Task Streaming_ValidTool_StageDeliversWholeStream_Untouched()
    {
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true);

        var delivered = await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(delivered, e => e.EventType == "error");
        Assert.Equal("message_stop", delivered[^1].EventType);
        Assert.Contains(delivered, e => e.EventType == "content_block_stop");
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests (stage-level, config gate): with <c>Enabled=false</c> the stage never begins
    /// or runs the detector, so even a malformed tool block is delivered whole.
    /// Input: the same malformed tool stream as the abort case, but
    /// <see cref="RunStage"/> is called with <c>enabled: false</c>.
    /// Expects: no <c>error</c> event; the final event is <c>message_stop</c>;
    /// <c>ToolInputInvalidDetected</c> stays false (the guard is fully inert).
    /// </summary>
    [Fact]
    public async Task Streaming_Disabled_IsInert_MalformedToolPassesThrough()
    {
        // Contract: Enabled=false → the detector is never begun/run by the stage, so
        // even a malformed tool block streams through whole and the flag stays clear.
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true, enabled: false);

        var delivered = await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(delivered, e => e.EventType == "error");
        Assert.Equal("message_stop", delivered[^1].EventType);
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests (stage-level, buffered delivery): with <c>PreserveStream=false</c> the stage
    /// buffers the whole stream and returns a real HTTP error status whose body is only
    /// the error envelope — none of the malformed tool input reaches the client.
    /// Input: the malformed tool stream run through <see cref="RunStage"/> with
    /// <c>preserveStream: false</c> (so <c>RequiresBuffering</c> is true).
    /// Expects: <c>Response.Mode == Buffered</c>, status 529,
    /// <c>ToolInputInvalidDetected</c> true, the body contains <c>overloaded_error</c>
    /// and does NOT contain any tool-input content (e.g. <c>multiSelect</c>).
    /// </summary>
    [Fact]
    public async Task BufferedPreserveStreamFalse_StageWithholdsMalformedContent_RealStatus()
    {
        // PreserveStream=false → RequiresBuffering=true → the stage buffers the whole
        // stream, trips on the bad stop, and flips to a real 529 whose body is the
        // error envelope only — the malformed tool text never reaches the client.
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: false);

        Assert.Equal(ResponseMode.Buffered, ctx.Response.Mode);
        Assert.Equal(529, ctx.Response.Status);
        Assert.True(ctx.ToolInputInvalidDetected);
        var body = System.Text.Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("multiSelect", body); // no leaked tool-input content
    }

    /// <summary>
    /// Tests (stage-level, the shipped default): with the DEFAULT observe options and
    /// Enabled=true, a schema-invalid tool block is delivered whole through the real
    /// stage — no <c>error</c> injected, <c>content_block_stop</c> and <c>message_stop</c>
    /// both present — while the summary flag is set. This is the end-to-end guarantee of
    /// the fix (CC's self-heal is not cut off), driven through <see cref="ResponseInspectionStage"/>
    /// rather than the detector alone.
    /// </summary>
    [Fact]
    public async Task Streaming_DefaultObserve_StageDeliversWholeStream_FlagSet_NoAbort()
    {
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStageObserve(ctx);

        var delivered = await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(delivered, e => e.EventType == "error");
        Assert.Contains(delivered, e => e.EventType == "content_block_stop");
        Assert.Equal("message_stop", delivered[^1].EventType);
        Assert.True(ctx.ToolInputInvalidDetected); // observed, not aborted
    }


    /// Input: two <c>AskUserQuestion</c> blocks at distinct indices — the first valid,
    /// the second missing a nested required field.
    /// Expects: exactly one <see cref="DetectionActionKind.Abort"/> (the second block)
    /// and <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task MultipleToolBlocks_ValidThenInvalid_TripsOnSecondOnly()
    {
        var ctx = Context(
            ToolSchema(),
            TwoToolStream(
                ("AskUserQuestion", """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""),
                ("AskUserQuestion", """{"questions":[{"header":"H","options":[],"multiSelect":false}]}""")));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Single(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: a non-tool (text) block preceding a tool block is ignored — its text deltas
    /// are never accumulated or parsed as tool input.
    /// Input: a text block whose delta contains brace characters (<c>{ braces }</c>, not
    /// JSON) followed by a valid <c>AskUserQuestion</c> tool block.
    /// Expects: all actions <see cref="DetectionActionKind.None"/> and
    /// <c>ToolInputInvalidDetected</c> false (the stray braces do not trip a false abort).
    /// </summary>
    [Fact]
    public async Task InterleavedTextBlock_IsIgnored_ValidToolPasses()
    {
        // A text block before the tool block must not be accumulated/parsed as tool
        // input; only the tool block is validated.
        var ctx = Context(ToolSchema(), TextThenToolStream(
            "Here is a plain-text answer with { braces } that is not JSON.",
            "AskUserQuestion",
            """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: the speculative path where a backend ships the whole input object on
    /// <c>content_block_start</c> and sends no deltas — that start-carried input is still
    /// validated.
    /// Input: a tool block whose <c>content_block_start</c> input is a complete but
    /// schema-invalid object (nested required missing), with no <c>input_json_delta</c>.
    /// Expects: an <see cref="DetectionActionKind.Abort"/> and
    /// <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task StartCarriedInput_NoDeltas_IsValidated()
    {
        // Speculative path: a backend ships the full input object on content_block_start
        // with no following deltas. An invalid one must still trip.
        var ctx = Context(ToolSchema(), StartInputToolStream(
            "AskUserQuestion", """{"questions":[{"header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests (regression for the start-input concatenation bug): when a block carries a
    /// complete valid input object on start AND also streams the same input as deltas,
    /// the two must NOT be concatenated (which would produce <c>{...}{...}</c> — malformed
    /// JSON — and a false abort). Deltas are authoritative when present.
    /// Input: a tool block with a valid input object on <c>content_block_start</c> plus
    /// delta fragments carrying the same valid input.
    /// Expects: all actions <see cref="DetectionActionKind.None"/> and
    /// <c>ToolInputInvalidDetected</c> false. (This test fails on the pre-fix code.)
    /// </summary>
    [Fact]
    public async Task StartCarriedInput_PlusDeltas_DoesNotConcatenateIntoMalformedJson()
    {
        // Regression: when a start carries a (complete, valid) input object AND deltas
        // also stream the full input, the two must NOT be concatenated into `{...}{...}`
        // (malformed JSON) and falsely aborted. Deltas are authoritative.
        var ctx = Context(ToolSchema(), StartInputPlusDeltasToolStream(
            "AskUserQuestion", """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: a required property declared at the TOP level of the schema (not nested)
    /// is enforced.
    /// Input: an <c>AskUserQuestion</c> call whose input is a valid object but omits the
    /// top-level required <c>questions</c> property (<c>{"other":"value"}</c>).
    /// Expects: an <see cref="DetectionActionKind.Abort"/> and
    /// <c>ToolInputInvalidDetected</c> true.
    /// </summary>
    [Fact]
    public async Task TopLevelRequiredMissing_Aborts()
    {
        // Input is a valid object but omits the top-level required `questions`.
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"other":"value"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    /// <summary>
    /// Tests: the <c>AbortApiError</c> action selects the abort's wire shape — HTTP 500
    /// / <c>api_error</c> instead of the <c>AbortOverloaded</c> 529 / <c>overloaded_error</c>.
    /// The wire shape is folded into the action, so there is no separate signal knob to
    /// diverge from it.
    /// Input: a schema-invalid tool call (top-level required missing) with
    /// <c>SchemaViolationAction = ToolInputAction.AbortApiError</c>.
    /// Expects: the abort's HTTP status is 500 and the error JSON names <c>api_error</c>.
    /// </summary>
    [Fact]
    public async Task AbortApiErrorAction_AbortsWith500()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"other":"value"}"""));
        var detector = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = true,
                SchemaViolationAction = ToolInputAction.AbortApiError,
            }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);
        var abort = actions.Single(a => a.Kind == DetectionActionKind.Abort);

        Assert.Equal(500, abort.HttpStatus);
        Assert.Contains("api_error", abort.ErrorJson);
    }

    private static async Task RunStage(BridgeContext<MessagesRequest> ctx, bool preserveStream, bool enabled = true)
    {
        // Drive the REAL stage with the always-on DONE filter plus the tool-input
        // detector, sharing ctx as production DI does. This exercises Begin() gating,
        // RequiresBuffering branch selection, and the stage's Abort rendering. Both
        // classes are opted into Abort so the stage-abort path is exercised; the
        // observe-default stage behaviour is covered by RunStageObserve.
        var detectors = new IResponseDetector[]
        {
            new DoneFilterDetector(new DetectorOrder<DoneFilterDetector>(0)),
            new ToolInputValidationDetector(
                new DetectorOrder<ToolInputValidationDetector>(4),
                TestOptions.Snapshot(new ToolInputValidationOptions
                {
                    Enabled = enabled,
                    PreserveStream = preserveStream,
                    MalformedJsonAction = ToolInputAction.AbortOverloaded,
                    SchemaViolationAction = ToolInputAction.AbortOverloaded,
                }),
                ctx,
                NullLogger<ToolInputValidationDetector>.Instance),
        };
        var stage = new ResponseInspectionStage(detectors, ctx, NullLogger<ResponseInspectionStage>.Instance);
        await stage.ApplyAsync();
    }

    // Drive the REAL stage with DEFAULT (observe) options — the shipped configuration.
    private static async Task RunStageObserve(BridgeContext<MessagesRequest> ctx)
    {
        var detectors = new IResponseDetector[]
        {
            new DoneFilterDetector(new DetectorOrder<DoneFilterDetector>(0)),
            new ToolInputValidationDetector(
                new DetectorOrder<ToolInputValidationDetector>(4),
                TestOptions.Snapshot(new ToolInputValidationOptions { Enabled = true }),
                ctx,
                NullLogger<ToolInputValidationDetector>.Instance),
        };
        var stage = new ResponseInspectionStage(detectors, ctx, NullLogger<ResponseInspectionStage>.Instance);
        await stage.ApplyAsync();
    }

    private static async Task<List<SseItem<string>>> Drain(IAsyncEnumerable<SseItem<string>> s)
    {
        var list = new List<SseItem<string>>();
        await foreach (var e in s) list.Add(e);
        return list;
    }

    // Ships the DEFAULT options (both actions Observe, PreserveStream true) — used by
    // the Observe_* tests to assert the real shipped behaviour.
    private static ToolInputValidationDetector DefaultDetector(BridgeContext<MessagesRequest> ctx) =>
        new(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions { Enabled = true }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);

    // Most tests below exercise the ABORT machinery, so this helper opts BOTH
    // classes into Abort. The observe-by-default contract (no abort, flag only) is
    // covered by the dedicated Observe_* tests, which pass explicit Observe options.
    private static ToolInputValidationDetector Detector(BridgeContext<MessagesRequest> ctx, bool preserveStream = true) =>
        new(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = preserveStream,
                MalformedJsonAction = ToolInputAction.AbortOverloaded,
                SchemaViolationAction = ToolInputAction.AbortOverloaded,
            }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);

    private static BridgeContext<MessagesRequest> Context(IReadOnlyList<Tool> tools, IAsyncEnumerable<SseItem<string>> stream) =>
        new()
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = new MessagesRequest
                {
                    Model = "claude-opus-4-8",
                    Messages = Array.Empty<MessageParam>(),
                    Tools = tools,
                },
            },
            Response = new BridgeResponse { Mode = ResponseMode.Streaming, EventStream = stream },
            Ct = default,
        };

    private static IReadOnlyList<Tool> ToolSchema() =>
    [
        new Tool
        {
            Name = "AskUserQuestion",
            InputSchema = new InputSchema
            {
                Type = "object",
                Properties = JsonDocument.Parse("""
                {
                  "questions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "question": { "type": "string" },
                        "header": { "type": "string" },
                        "options": { "type": "array" },
                        "multiSelect": { "type": "boolean" }
                      },
                      "required": ["question", "header", "options", "multiSelect"]
                    }
                  }
                }
                """).RootElement.Clone(),
                Required = ["questions"],
            },
        },
    ];

    private static async IAsyncEnumerable<SseItem<string>> ToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A COMPLETE Anthropic stream (message_start … content blocks … message_stop),
    // as a real client sees it — used for the end-to-end stage tests so we can assert
    // whether message_stop / content_block_stop are delivered.
    private static async IAsyncEnumerable<SseItem<string>> FullToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""", "message_delta");
        yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
        await Task.CompletedTask;
    }

    // Two tool blocks at distinct indices, each with its own start/deltas/stop.
    private static async IAsyncEnumerable<SseItem<string>> TwoToolStream(
        (string tool, string input) first,
        (string tool, string input) second)
    {
        var idx = 0;
        foreach (var (tool, input) in new[] { first, second })
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_start\",\"index\":" + idx + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_" + idx + "\",\"name\":" + JsonSerializer.Serialize(tool) + ",\"input\":{}}}",
                "content_block_start");
            foreach (var fragment in Split(input, 12))
            {
                yield return new SseItem<string>(
                    "{\"type\":\"content_block_delta\",\"index\":" + idx + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                    "content_block_delta");
            }
            yield return new SseItem<string>("{\"type\":\"content_block_stop\",\"index\":" + idx + "}", "content_block_stop");
            idx++;
        }
        await Task.CompletedTask;
    }

    // A text block (index 0) followed by a tool block (index 1). The text deltas must
    // be ignored by the tool detector.
    private static async IAsyncEnumerable<SseItem<string>> TextThenToolStream(string text, string toolName, string inputJson)
    {
        yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}",
            "content_block_delta");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A tool block whose full input rides on content_block_start, with NO deltas.
    private static async IAsyncEnumerable<SseItem<string>> StartInputToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":" + inputJson + "}}",
            "content_block_start");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A tool block that carries the full input on start AND also streams the same
    // input as deltas — the regression case that must not concatenate into bad JSON.
    private static async IAsyncEnumerable<SseItem<string>> StartInputPlusDeltasToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":" + inputJson + "}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SseItem<string>> LegacyStartToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"start\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }


    private static IEnumerable<string> Split(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }

    private static async Task<List<DetectionAction>> Feed(ToolInputValidationDetector detector, IAsyncEnumerable<SseItem<string>> stream)
    {
        var actions = new List<DetectionAction>();
        await foreach (var evt in stream)
        {
            actions.Add(detector.InspectEvent(evt));
        }
        return actions;
    }

    private static async IAsyncEnumerable<T> AsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
