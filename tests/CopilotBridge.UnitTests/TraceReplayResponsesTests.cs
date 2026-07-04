using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Trace-replay harness for the Claude-Code → gpt-5.5 (Copilot <c>/responses</c>)
/// path. Takes REAL captured Claude Code request bodies and drives them through
/// T2 (<see cref="ResponsesRequestBuilder.Build"/>) with the model forced to
/// <c>gpt-5.5</c> — exactly what happens when an operator routes Claude Code to
/// the Responses backend. Asserts the wire body Copilot would receive from the
/// <b>contract</b> (what a Responses request MUST look like for gpt-5.5 to be
/// able to call tools), NOT from the current implementation.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The user reported that routing Claude Code to
/// gpt-5.5 fails on anything but trivial prompts. Root cause: T2 rebuilt
/// <c>tools</c>/<c>tool_choice</c> only from the Codex <c>openai</c> bag
/// (<c>ProviderExtensions["openai"]</c>) and never read the typed
/// <see cref="MessagesRequest.Tools"/>/<see cref="MessagesRequest.ToolChoice"/> a
/// Claude Code request carries — so every tool was silently dropped and gpt-5.5
/// could talk but never act. These assertions encode the fix's contract and go
/// RED on the pre-fix builder (mutation-checkable).</para>
/// <para><b>Inputs.</b> Two sources, unioned: (1) the de-identified CC fixtures
/// committed under <c>Fixtures/cc-request-*.json</c> (always present, CI-safe);
/// (2) — when <c>BRIDGE_TRACE_DIR</c> points at a real capture directory (the
/// user's <c>request-traces</c>) — a capped, size-bounded sample of real
/// <c>*-inbound-req.json</c> Claude Code bodies. The harness is deterministic and
/// network-free either way: it only exercises the pure T2 function.</para>
/// </remarks>
public class TraceReplayResponsesTests
{
    private const string TargetModel = "gpt-5.5";

    // The real shipping catalog: gpt-5.5 is the "large" profile
    // (accepts none/low/medium/high/xhigh, rejects minimal).
    private static readonly CodexModelProfileCatalog Catalog = new();

    private static readonly string[] AcceptedEfforts =
        ["none", "low", "medium", "high", "xhigh"];

    public static IEnumerable<object[]> ClaudeCodeBodies()
    {
        foreach (var (name, json) in TraceCorpus.LoadClaudeCodeBodies())
        {
            yield return [name, json];
        }
    }

    // ── The core contract: a Claude Code request with N tools must produce a
    //    Responses body with N function tools. Dropping tools is the bug. ──

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void EveryClaudeCodeTool_SurvivesAsResponsesFunctionTool(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        // Only meaningful for tool-carrying requests; a tool-less body is a valid
        // input but exercises a different contract (asserted separately).
        var inboundToolCount = ir.Tools?.Count ?? 0;
        if (inboundToolCount == 0)
        {
            return; // covered by ToollessBody_ProducesNoToolsKey
        }

        var wire = Emit(ir);

        // The expected surviving-tool count must mirror EVERY drop WriteIrTools
        // applies, not just server tools: it also drops the IDE-only
        // mcp__ide__executeCode (defer_loading != true), matching ToolsSanitizeStage.
        // A real capture (BRIDGE_TRACE_DIR) that carries that tool would otherwise
        // make `expected` over-count and fail even though T2 is correct.
        var expected = ir.Tools!.Count(t => !IsDroppedTool(t));

        var tools = wire["tools"]?.AsArray();
        if (expected == 0)
        {
            // All tools were dropped → no tools key at all (WriteIrTools emits none),
            // which is the correct "no tools" signal, not a bug.
            Assert.True(tools is null,
                $"[{name}] all {inboundToolCount} tools are droppable, but a 'tools' array was still emitted.");
            return;
        }

        Assert.True(tools is not null,
            $"[{name}] Claude Code sent {inboundToolCount} tools ({expected} non-droppable) but the Responses "
            + "body has no 'tools' key — gpt-5.5 will be unable to call any tool (the reported failure).");
        Assert.Equal(expected, tools!.Count);
    }

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void EveryEmittedTool_HasResponsesFunctionShape(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        if ((ir.Tools?.Count ?? 0) == 0) return;

        var wire = Emit(ir);
        var tools = wire["tools"]?.AsArray();
        Assert.NotNull(tools);

        foreach (var tool in tools!)
        {
            var obj = tool!.AsObject();
            var type = obj["type"]?.GetValue<string>();
            Assert.Equal("function", type);

            // gpt-5.5 wants {type,name,parameters}. name is required and non-empty.
            var toolName = obj["name"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(toolName),
                $"[{name}] emitted function tool missing 'name'");

            // parameters is the JSON-Schema object (renamed from Anthropic's
            // input_schema). It MUST be present and an object.
            Assert.True(obj["parameters"] is JsonObject,
                $"[{name}] tool '{toolName}' has no 'parameters' object — "
                + "Anthropic's input_schema was not translated to Responses 'parameters'.");

            // The Anthropic key name must NOT leak onto the Responses wire.
            Assert.False(obj.ContainsKey("input_schema"),
                $"[{name}] tool '{toolName}' leaked Anthropic 'input_schema' onto the Responses wire.");
        }
    }

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void NoServerToolReachesTheWire(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        var wire = Emit(ir);
        var tools = wire["tools"]?.AsArray();
        if (tools is null) return;

        foreach (var tool in tools)
        {
            var type = tool!.AsObject()["type"]?.GetValue<string>() ?? "";
            // Responses tool types Copilot accepts: function / custom / (and web_search
            // for Codex, which CC never sends). A leaked Anthropic server-tool
            // discriminator like "web_search_20250305" must never appear.
            Assert.False(type.Contains("web_search_", StringComparison.Ordinal),
                $"[{name}] leaked Anthropic server-tool type '{type}' onto the Responses wire.");
        }
    }

    // ── tool_result content must reach the wire as a STRING output ──

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void ToolResultOutputs_AreStrings(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        var wire = Emit(ir);
        var input = wire["input"]?.AsArray();
        Assert.NotNull(input);

        foreach (var item in input!)
        {
            var obj = item!.AsObject();
            if (obj["type"]?.GetValue<string>() != "function_call_output") continue;
            var output = obj["output"];
            Assert.True(output is JsonValue,
                $"[{name}] function_call_output.output is not a scalar string — "
                + "gpt-5.5 rejects an array/object output.");
            // JsonValue that is a string.
            Assert.True(output!.AsValue().TryGetValue<string>(out _),
                $"[{name}] function_call_output.output is not a string value.");
        }
    }

    // ── effort must be clamped to gpt-5.5's accepted set ──

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void Effort_IsClampedToGpt55AcceptedSet(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        var wire = Emit(ir);
        var effort = wire["reasoning"]?["effort"]?.GetValue<string>();
        if (effort is null) return; // dropped/absent is legal
        Assert.True(Array.IndexOf(AcceptedEfforts, effort) >= 0,
            $"[{name}] emitted effort '{effort}' is not in gpt-5.5's accepted set "
            + $"[{string.Join(",", AcceptedEfforts)}].");
    }

    // ── the emitted body is always valid JSON with the required top-level shape ──

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void EmittedBody_IsValidResponsesShape(string name, string bodyJson)
    {
        var ir = ParseIr(bodyJson);
        var wire = Emit(ir);
        Assert.Equal(TargetModel, wire["model"]?.GetValue<string>());
        Assert.True(wire["input"] is JsonArray, $"[{name}] missing input[]");
    }

    [Fact]
    public void ToollessBody_ProducesNoToolsKey()
    {
        // Contract: a request with no tools must not fabricate a tools key.
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
        };
        var wire = Emit(ir);
        Assert.Null(wire["tools"]);
    }

    [Fact]
    public void IdeExecuteCodeTool_IsDropped_MatchingToolsSanitizeStage()
    {
        // Contract: mcp__ide__executeCode (without defer_loading) is a no-op on a
        // non-IDE backend; ToolsSanitizeStage drops it on the /cc→Anthropic path,
        // and that stage is gated OFF for a Responses target, so T2 must drop it
        // too — otherwise gpt-5.5 could call a tool the client can't service.
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            Tools =
            [
                new Tool { Name = "Bash", InputSchema = new InputSchema() },
                new Tool { Name = "mcp__ide__executeCode", InputSchema = new InputSchema() },
            ],
        };
        var wire = Emit(ir);
        var names = wire["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Bash", names);
        Assert.DoesNotContain("mcp__ide__executeCode", names);
    }

    [Fact]
    public void ToolChoice_WithoutSurvivingTools_IsNotEmitted()
    {
        // Contract: tool_choice ("required" / {function,name}) with no tools array
        // is a Responses 400. If the only tool is dropped (server / IDE-only), the
        // body must carry neither tools nor tool_choice.
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            Tools = [new Tool { Name = "mcp__ide__executeCode", InputSchema = new InputSchema() }],
            ToolChoice = new ToolChoiceAny(),
        };
        var wire = Emit(ir);
        Assert.Null(wire["tools"]);        // the only tool was dropped
        Assert.Null(wire["tool_choice"]);  // so tool_choice must not be emitted
    }

    [Fact]
    public void ToolChoice_Any_MapsToRequired_WhenToolsPresent()
    {
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            Tools = [new Tool { Name = "Bash", InputSchema = new InputSchema() }],
            ToolChoice = new ToolChoiceAny(),
        };
        var wire = Emit(ir);
        Assert.Equal("required", wire["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void ToolChoice_ForcedSurvivingTool_EmitsFunctionChoice()
    {
        // Forced tool that SURVIVES the drop filter → {type:function,name}.
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            Tools = [new Tool { Name = "Bash", InputSchema = new InputSchema() }],
            ToolChoice = new ToolChoiceTool { Name = "Bash" },
        };
        var wire = Emit(ir);
        var tc = wire["tool_choice"]!.AsObject();
        Assert.Equal("function", tc["type"]!.GetValue<string>());
        Assert.Equal("Bash", tc["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolChoice_ForcedDroppedTool_DowngradesToAuto()
    {
        // Forced tool that gets DROPPED (IDE-only) must NOT be named in tool_choice —
        // that would reference a tool absent from tools[] (Responses 400). It is
        // downgraded to "auto" so the request still succeeds. Include a surviving
        // sibling so tools[] is non-empty (isolates the forced-name-dropped case
        // from the no-tools-at-all case).
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            Tools =
            [
                new Tool { Name = "Bash", InputSchema = new InputSchema() },
                new Tool { Name = "mcp__ide__executeCode", InputSchema = new InputSchema() },
            ],
            ToolChoice = new ToolChoiceTool { Name = "mcp__ide__executeCode" },
        };
        var wire = Emit(ir);
        // Bash survived; the IDE tool was dropped, so it must not be forced.
        var names = wire["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Bash", names);
        Assert.DoesNotContain("mcp__ide__executeCode", names);
        Assert.Equal("auto", wire["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void ThinkingOnlyAssistantTurn_VanishesCleanly_NoEmptyMessageItem()
    {
        // A structural edge the drop-thinking contract creates: if an assistant
        // turn's ONLY block is plain thinking (all dropped), the whole message must
        // vanish from input[] — NOT become an empty message item (Responses rejects
        // a message with empty content[]). Adjacent user turns are legal on gpt-5.5
        // (live-probed: consecutive user messages accepted), so a vanished turn is
        // harmless. Real Claude Code never sends a thinking-only turn (always
        // thinking+text+tool_use — corpus-verified), but the builder must handle it.
        var ir = new MessagesRequest
        {
            Model = TargetModel,
            MaxTokens = 0,
            Messages =
            [
                new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] },
                new MessageParam { Role = Role.Assistant, Content = [new ThinkingBlockParam { Thinking = "internal", Signature = "sig" }] },
                new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "continue" }] },
            ],
        };
        var wire = Emit(ir);
        var input = wire["input"]!.AsArray();
        // No empty message items, and no assistant item survived (its only block was dropped).
        foreach (var it in input)
        {
            var o = it!.AsObject();
            if (o["type"]!.GetValue<string>() == "message")
            {
                Assert.NotEqual(0, o["content"]!.AsArray().Count);
                Assert.Equal("user", o["role"]!.GetValue<string>());
            }
        }
        Assert.Equal(2, input.Count); // the two user turns; the thinking-only assistant turn vanished
    }

    // ── helpers ──

    private static JsonObject Emit(MessagesRequest ir) =>
        JsonNode.Parse(ResponsesRequestBuilder.Build(ir, Catalog).Body)!.AsObject();

    /// <summary>
    /// Parse a captured Claude Code body and force the model to gpt-5.5 — the
    /// replay's whole point. The captured bodies name claude-* (they were served
    /// by the passthrough path); routing them to gpt-5.5 is what the operator does.
    /// </summary>
    private static MessagesRequest ParseIr(string bodyJson)
    {
        var req = JsonSerializer.Deserialize(bodyJson, JsonContext.Default.MessagesRequest)
            ?? throw new InvalidOperationException("trace body deserialized to null");
        return req with { Model = TargetModel };
    }

    /// <summary>
    /// A tool T2 (<see cref="ResponsesRequestBuilder"/> → <c>WriteIrTools</c>) drops,
    /// so it never reaches the wire — MUST mirror the builder's contract exactly:
    /// server tools (<c>web_search_*</c>) and the IDE-only <c>mcp__ide__executeCode</c>
    /// unless the client defer-loaded it (matching <c>ToolsSanitizeStage</c>).
    /// </summary>
    private static bool IsDroppedTool(Tool t) =>
        (t.Type is { Length: > 0 } typ && typ.StartsWith("web_search_", StringComparison.OrdinalIgnoreCase))
        || (t.Name == "mcp__ide__executeCode" && t.DeferLoading != true);
}
