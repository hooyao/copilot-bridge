using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Codex A-invariant suite (change 3, tasks 6.1–6.4; design
/// <c>docs/ir-definition-design.md</c> §7). Asserts MATHEMATICAL PROPERTIES of
/// the Codex translators T1/T2 on real, de-identified Codex captures used only as
/// input samples — never as Copilot-behavior oracles. These hold regardless of
/// how Copilot evolves.
///
/// The round trip is <c>Responses →T1→ IR →T2→ Responses</c>: a Codex request
/// translated into the Anthropic IR and back. The hub-IR double-translation cost
/// is exactly what these guard — anything the Anthropic IR can't type must have
/// ridden the <c>ProviderExtensions["openai"]</c> bag and reappeared at T2.
/// </summary>
public class CodexARoundTripTests
{
    private readonly ITestOutputHelper _output;

    public CodexARoundTripTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Fixtures() =>
        CodexRoundTrip.FixtureSlugs().Select(s => new object[] { s });

    // ── A0: the DTOs deserialize every real capture (task 1 validation) ──────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A0_DtosDeserializeRealCapture(string slug)
    {
        var json = CodexRoundTrip.LoadBodyJson(slug);
        var req = CodexRoundTrip.ParseRequest(json);
        Assert.Equal("gpt-5.3-codex", req.Model);
        Assert.NotEmpty(req.Input);
        Assert.NotNull(req.Tools);
        // Re-serialize and confirm it parses again (round-trips through source-gen).
        var bytes = JsonSerializer.SerializeToUtf8Bytes(req, CopilotBridge.Cli.Models.JsonContext.Default.ResponsesRequest);
        Assert.True(bytes.Length > 0);
    }

    // ── A1: round-trip self-inverse under the per-field fidelity bar ──────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A1_RoundTripPreservesKeyFields(string slug)
    {
        var input = CodexRoundTrip.LoadBodyJson(slug);
        var original = CodexRoundTrip.ParseNode(input).AsObject();
        var emitted = CodexRoundTrip.RoundTrip(input).AsObject();

        // model — byte-identical.
        Assert.Equal(original["model"]!.GetValue<string>(), emitted["model"]!.GetValue<string>());

        // instructions — Codex's harness preamble must survive (semantically;
        // T1 folds developer messages into it, so emitted may be a superset).
        var origInstr = original["instructions"]?.GetValue<string>() ?? "";
        var emitInstr = emitted["instructions"]?.GetValue<string>() ?? "";
        Assert.Contains(origInstr.Split('\n')[0], emitInstr);

        // store / include / prompt_cache_key / text — carried via the bag, must reappear.
        Assert.Equal(
            original["prompt_cache_key"]?.GetValue<string>(),
            emitted["prompt_cache_key"]?.GetValue<string>());
        Assert.Equal(
            original["include"]?.ToJsonString(),
            emitted["include"]?.ToJsonString());
        if (original["store"] is not null)
            Assert.Equal(original["store"]!.GetValue<bool>(), emitted["store"]!.GetValue<bool>());
        Assert.Equal(
            original["text"]?.ToJsonString(),
            emitted["text"]?.ToJsonString());

        // reasoning.effort — accepted by gpt-5.3-codex (large profile), preserved.
        Assert.Equal(
            original["reasoning"]?["effort"]?.GetValue<string>(),
            emitted["reasoning"]?["effort"]?.GetValue<string>());
    }

    // ── A2: tools preserved byte-faithfully (function + apply_patch custom) ───

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A2_ToolsByteFaithfulThroughBag(string slug)
    {
        var input = CodexRoundTrip.LoadBodyJson(slug);
        var original = CodexRoundTrip.ParseNode(input).AsObject();
        var emitted = CodexRoundTrip.RoundTrip(input).AsObject();

        var origTools = original["tools"]!.AsArray();
        var emitTools = emitted["tools"]!.AsArray();

        // gpt-5.3-codex rejects no tools (large profile, accepts custom), and the
        // fixtures carry no image_generation tool, so the count is preserved.
        Assert.Equal(origTools.Count, emitTools.Count);

        // The apply_patch custom tool (grammar format) must be byte-identical —
        // the LiteLLM-class fidelity guard for opaque tool bodies.
        var origApply = origTools.FirstOrDefault(t => t!["name"]?.GetValue<string>() == "apply_patch");
        var emitApply = emitTools.FirstOrDefault(t => t!["name"]?.GetValue<string>() == "apply_patch");
        Assert.NotNull(origApply);
        Assert.NotNull(emitApply);
        Assert.Equal(origApply!.ToJsonString(), emitApply!.ToJsonString());
    }

    // ── A3: bag survival canary ──────────────────────────────────────────────

    [Fact]
    public void A3_BagCanary_SurvivesT2()
    {
        // A Codex request whose IR carries an unknown openai key must re-emit it.
        var input = CodexRoundTrip.LoadBodyJson("plain-3turn");
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(input));

        // Inject a canary into the openai bag.
        var bag = ir.ProviderExtensions!.ByProvider["openai"];
        using var doc = JsonDocument.Parse(bag.GetRawText());
        var withCanary = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in doc.RootElement.EnumerateObject())
            withCanary[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        withCanary["__canary__"] = "keep-me-verbatim";
        using var canaryDoc = JsonDocument.Parse(withCanary.ToJsonString());
        var irWithCanary = ir with
        {
            ProviderExtensions = new ProviderExtensions
            {
                ByProvider = new Dictionary<string, JsonElement> { ["openai"] = canaryDoc.RootElement.Clone() },
            },
        };

        var emitted = JsonNode.Parse(CodexRoundTrip.ToResponsesWire(irWithCanary))!.AsObject();
        Assert.Equal("keep-me-verbatim", emitted["__canary__"]?.GetValue<string>());
    }

    // ── A4: un-modeled knobs transit the bag intact ──────────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A4_UnmodeledKnobs_RideTheBag(string slug)
    {
        // After T1, the IR's openai bag must hold the knobs the Anthropic body
        // can't type. Assert they're present in the bag (T1's job) — A1 already
        // asserts they reappear at T2.
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(CodexRoundTrip.LoadBodyJson(slug)));
        Assert.NotNull(ir.ProviderExtensions);
        var bag = ir.ProviderExtensions!.ByProvider["openai"];
        var keys = bag.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("tools", keys);
        Assert.Contains("include", keys);
        Assert.Contains("prompt_cache_key", keys);
        Assert.Contains("text", keys);
        Assert.Contains("store", keys);
    }

    // ── A5: tool-call/result pairing survives T1→IR→T2 ───────────────────────
    // The 6 captures are pure-message turns (that session triggered no tool
    // call), so A5 uses a synthetic request with the EXACT Responses item shapes
    // Codex sends for a tool round trip (function_call + function_call_output),
    // verifying call_id linkage survives the Anthropic IR (where it becomes
    // tool_use.id ↔ tool_result.tool_use_id) and reappears at T2.

    [Fact]
    public void A5_FunctionCallPairing_SurvivesRoundTrip()
    {
        const string callId = "call_abc123";
        var requestJson = $$"""
          {
            "model": "gpt-5.3-codex",
            "instructions": "You may use tools.",
            "input": [
              {"type":"message","role":"user","content":[{"type":"input_text","text":"run ls"}]},
              {"type":"function_call","call_id":"{{callId}}","name":"shell","arguments":"{\"command\":\"ls\"}"},
              {"type":"function_call_output","call_id":"{{callId}}","output":"file1\nfile2"}
            ],
            "stream": true, "store": false
          }
          """;

        var req = CodexRoundTrip.ParseRequest(requestJson);
        var ir = CodexRoundTrip.ToIr(req);

        // In the IR: an assistant tool_use with Id=callId, then a user tool_result
        // with ToolUseId=callId.
        var toolUse = ir.Messages
            .SelectMany(m => m.Content)
            .OfType<CopilotBridge.Cli.Models.Anthropic.Request.ToolUseBlockParam>()
            .Single();
        Assert.Equal(callId, toolUse.Id);
        Assert.Equal("shell", toolUse.Name);
        // tool_use.input is the parsed object (byte-faithful through JsonElement).
        Assert.Equal("ls", toolUse.Input.GetProperty("command").GetString());

        var toolResult = ir.Messages
            .SelectMany(m => m.Content)
            .OfType<CopilotBridge.Cli.Models.Anthropic.Request.ToolResultBlockParam>()
            .Single();
        Assert.Equal(callId, toolResult.ToolUseId);

        // T2 re-emits them as function_call / function_call_output with the same call_id.
        var emitted = JsonNode.Parse(CodexRoundTrip.ToResponsesWire(ir))!.AsObject();
        var input = emitted["input"]!.AsArray();
        var fc = input.FirstOrDefault(i => i!["type"]?.GetValue<string>() == "function_call");
        var fco = input.FirstOrDefault(i => i!["type"]?.GetValue<string>() == "function_call_output");
        Assert.NotNull(fc);
        Assert.NotNull(fco);
        Assert.Equal(callId, fc!["call_id"]!.GetValue<string>());
        Assert.Equal(callId, fco!["call_id"]!.GetValue<string>());
        // arguments preserved as a JSON string.
        Assert.Equal("{\"command\":\"ls\"}", fc["arguments"]!.GetValue<string>());
    }

    // ── A7: conversation input[] (roles + text + order) survives T1→T2 ───────
    // The existing A1 only checks bag knobs/scalars, never the messages. This
    // asserts the actual turns round-trip (a reviewer-flagged gap).

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A7_InputMessages_SurviveRoundTrip(string slug)
    {
        var inbound = CodexRoundTrip.LoadBodyJson(slug);
        var original = CodexRoundTrip.ParseNode(inbound).AsObject();
        var emitted = CodexRoundTrip.RoundTrip(inbound).AsObject();

        // Collect (role, concatenated input_text) for the user/assistant message
        // items on each side. developer-role items fold into instructions (A8),
        // so compare only the non-developer messages.
        static List<(string role, string text)> Msgs(JsonNode body)
        {
            var result = new List<(string, string)>();
            foreach (var item in body["input"]!.AsArray())
            {
                if (item!["type"]?.GetValue<string>() != "message") continue;
                var role = item["role"]!.GetValue<string>();
                if (role is "developer" or "system") continue;
                var sb = new System.Text.StringBuilder();
                foreach (var part in item["content"]!.AsArray())
                {
                    var pt = part!["type"]?.GetValue<string>();
                    if (pt is "input_text" or "output_text")
                        sb.Append(part["text"]!.GetValue<string>());
                }
                result.Add((role, sb.ToString()));
            }
            return result;
        }

        var origMsgs = Msgs(original);
        var emitMsgs = Msgs(emitted);
        Assert.Equal(origMsgs.Count, emitMsgs.Count);
        for (var i = 0; i < origMsgs.Count; i++)
        {
            Assert.Equal(origMsgs[i].role, emitMsgs[i].role);   // role + order preserved
            Assert.Equal(origMsgs[i].text, emitMsgs[i].text);   // text preserved
        }
        Assert.NotEmpty(origMsgs); // the fixtures do carry user turns
    }

    // ── A8: developer-role messages fold into instructions ───────────────────

    [Fact]
    public void A8_DeveloperMessage_FoldsIntoInstructions()
    {
        var requestJson = """
          {
            "model": "gpt-5.3-codex",
            "instructions": "BASE-INSTRUCTIONS",
            "input": [
              {"type":"message","role":"developer","content":[{"type":"input_text","text":"DEV-PREAMBLE"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}
            ],
            "stream": true, "store": false
          }
          """;
        var ir = CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(requestJson));
        var emitted = JsonNode.Parse(CodexRoundTrip.ToResponsesWire(ir))!.AsObject();

        var instructions = emitted["instructions"]!.GetValue<string>();
        Assert.Contains("BASE-INSTRUCTIONS", instructions);
        Assert.Contains("DEV-PREAMBLE", instructions);   // developer content was folded in, not dropped
        // The developer message must NOT survive as an input[] message.
        Assert.DoesNotContain(emitted["input"]!.AsArray(),
            i => i!["type"]?.GetValue<string>() == "message"
                 && i["role"]?.GetValue<string>() == "developer");
    }

    // ── A9: non-JSON tool arguments → carried as grammar text, NOT rejected ────
    // A custom (grammar) tool — Codex's `exec` — echoes its call back with
    // arguments = raw text (JavaScript), which is not JSON. The old contract 400'd
    // on it (ExpectedStartOfValueNotFound), which broke the real gpt-5.6 exec loop
    // on the second turn. The correct behavior (Copilot round-trips it fine —
    // live-probed 200) is to carry it through the IR and re-emit it verbatim.

    [Fact]
    public void A9_NonJsonToolArguments_CarriedAsGrammarText_NotRejected()
    {
        const string rawJs = "const r = await tools.shell_command({ command: \"ls\" });";
        var requestJson = $$"""
          {
            "model": "gpt-5.3-codex",
            "instructions": "x",
            "input": [
              {"type":"function_call","call_id":"call_1","name":"exec","arguments":{{System.Text.Json.JsonSerializer.Serialize(rawJs)}}}
            ],
            "stream": true, "store": false
          }
          """;
        // T1 must NOT throw (was CodexBadRequestException / 400).
        var req = CodexRoundTrip.ParseRequest(requestJson);
        var ex = Record.Exception(() => CodexRoundTrip.ToIr(req));
        Assert.Null(ex);

        // And T2 re-emits the raw arguments verbatim.
        var emitted = CodexRoundTrip.RoundTrip(requestJson).AsObject();
        var fc = emitted["input"]!.AsArray()
            .First(i => i!["type"]?.GetValue<string>() == "function_call");
        Assert.Equal(rawJs, fc!["arguments"]!.GetValue<string>());
    }

    [Fact]
    public void A9b_NonObjectJsonToolArguments_CarriedAsRawText_NotRejected()
    {
        // arguments that parse as valid JSON but are a scalar/array are no longer an
        // error — they're carried through as their raw text (lossless), not 400'd.
        var requestJson = """
          {
            "model": "gpt-5.3-codex",
            "instructions": "x",
            "input": [
              {"type":"function_call","call_id":"call_2","name":"shell","arguments":"[1,2,3]"}
            ],
            "stream": true, "store": false
          }
          """;
        var req = CodexRoundTrip.ParseRequest(requestJson);
        var ex = Record.Exception(() => CodexRoundTrip.ToIr(req));
        Assert.Null(ex);

        var emitted = CodexRoundTrip.RoundTrip(requestJson).AsObject();
        var fc = emitted["input"]!.AsArray()
            .First(i => i!["type"]?.GetValue<string>() == "function_call");
        Assert.Equal("[1,2,3]", fc!["arguments"]!.GetValue<string>());
    }

    // ── A5b: tool pairing on a REAL captured tool turn (not synthetic) ───────
    // The codex-request-toolcall-multiturn fixture is a real codex.exe tool turn
    // harvested from the E1 live run: messages + function_call (shell_command) +
    // function_call_output. Verifies the real call_id linkage + the real output
    // (a string here) survive T1→IR→T2 — promoting the synthetic A5 to live data.

    [Fact]
    public void A5b_RealToolTurn_PairingAndOutputSurvive()
    {
        var inbound = CodexRoundTrip.LoadBodyJson("toolcall-multiturn");
        var original = CodexRoundTrip.ParseNode(inbound).AsObject();
        var emitted = CodexRoundTrip.RoundTrip(inbound).AsObject();

        static (string callId, string name, string args) FnCall(JsonNode body)
        {
            var fc = body["input"]!.AsArray().First(i => i!["type"]!.GetValue<string>() == "function_call");
            return (fc!["call_id"]!.GetValue<string>(), fc["name"]!.GetValue<string>(), fc["arguments"]!.GetValue<string>());
        }
        static (string callId, string output) FnOut(JsonNode body)
        {
            var fco = body["input"]!.AsArray().First(i => i!["type"]!.GetValue<string>() == "function_call_output");
            return (fco!["call_id"]!.GetValue<string>(), fco["output"]!.ToJsonString());
        }

        var (oCallId, oName, oArgs) = FnCall(original);
        var (eCallId, eName, eArgs) = FnCall(emitted);
        Assert.Equal(oCallId, eCallId);   // real call_id survives
        Assert.Equal(oName, eName);       // shell_command
        Assert.Equal(oArgs, eArgs);       // arguments byte-faithful

        var (oOutId, oOut) = FnOut(original);
        var (eOutId, eOut) = FnOut(emitted);
        Assert.Equal(oOutId, eOutId);     // result references the same call
        Assert.Equal(oCallId, eOutId);    // closed linkage: output ↔ call
        Assert.Equal(oOut, eOut);         // the real tool output survives
    }
}
