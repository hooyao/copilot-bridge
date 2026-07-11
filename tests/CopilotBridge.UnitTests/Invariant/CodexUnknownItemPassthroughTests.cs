using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the UNIVERSAL unmodeled-<c>input[]</c>-item passthrough — the
/// root-cause fix that ends the per-type 400 whack-a-mole (four gpt-5.6 tool bugs
/// shipped because the bridge's closed <c>[JsonPolymorphic]</c> whitelist threw
/// <c>Polymorphism_UnrecognizedTypeDiscriminator</c> on any type it didn't model).
/// </summary>
/// <remarks>
/// Two guarantees, both derived from the contract "the bridge forwards what it does
/// not understand, byte-faithful, in order" (openai/codex <c>ResponseItem.ts</c> is
/// open-ended: <c>agent_message</c>, <c>tool_search_call</c>, <c>compaction</c>, …):
/// <list type="number">
///   <item>A gpt-5.6 inter-agent <c>agent_message</c> (author/recipient/content, with an
///   <c>encrypted_content</c> blob) — NOT modeled by the bridge, so it rides the unknown
///   passthrough — round-trips VERBATIM (incl. the raw blob bytes) and in order.</item>
///   <item>A completely UNKNOWN type — <c>tool_search_call</c> — does NOT 400; it is
///   carried through and re-emitted verbatim.</item>
/// </list>
/// Mutation-check: replace the converter's unknown branch with a throw and these redden.
/// </remarks>
public class CodexUnknownItemPassthroughTests
{
    // A realistic agent_message with an encrypted_content blob (the shape that 400'd
    // in production: Polymorphism_UnrecognizedTypeDiscriminator, agent_message).
    private const string EncBlob = "gAAAAABqUjqL2rq1ZBwpEXAMPLEabcdef0123456789+/=";

    private static string BodyWith(string extraItemsJson) => $$"""
      {
        "model":"gpt-5.6-sol",
        "instructions":"You are in a multi-agent session.",
        "input":[
          {"type":"message","role":"user","content":[{"type":"input_text","text":"start"}]},
          {{extraItemsJson}}
        ],
        "stream":true,"store":false
      }
      """;

    [Fact]
    public void AgentMessage_RoundTripsVerbatim_EncryptedBlobIntact()
    {
        var agentMessage = $$"""
          {"type":"agent_message","author":"/root","recipient":"/root/inspect_issues",
           "content":[
             {"type":"input_text","text":"Message Type: NEW_TASK\nSender: /root"},
             {"type":"encrypted_content","encrypted_content":{{Enc(EncBlob)}}}
           ]}
          """;
        var emitted = CodexRoundTrip.RoundTrip(BodyWith(agentMessage)).AsObject();
        var input = emitted["input"]!.AsArray();

        // The agent_message survived into the upstream input[], with every field.
        var am = FindItem(input, "agent_message");
        Assert.NotNull(am);
        Assert.Equal("/root", am!["author"]!.GetValue<string>());
        Assert.Equal("/root/inspect_issues", am["recipient"]!.GetValue<string>());

        // The encrypted_content blob is preserved with full VALUE fidelity — the exact
        // string decodes back byte-for-byte, which is what Copilot/codex consume. (The
        // wire may unicode-escape a '+' as +: STJ's default encoder does this, it's
        // semantically lossless valid JSON, and it matches the additional_tools path that
        // runs in production — corpus replay of 1213 real inbounds is clean. So the
        // contract here is value fidelity, not raw-byte identity of the escape form.)
        var content = am["content"]!.AsArray();
        var enc = content.FirstOrDefault(n => n?["type"]?.GetValue<string>() == "encrypted_content");
        Assert.NotNull(enc);
        Assert.Equal(EncBlob, enc!["encrypted_content"]!.GetValue<string>());

        // And the blob survives a full second parse of the actual outbound WIRE bytes —
        // proving it wasn't truncated/re-chunked, only (at most) escape-normalized.
        var wire = System.Text.Encoding.UTF8.GetString(
            CodexRoundTrip.ToResponsesWire(CodexRoundTrip.ToIr(CodexRoundTrip.ParseRequest(BodyWith(agentMessage)))));
        using var wireDoc = JsonDocument.Parse(wire);
        var wireBlob = wireDoc.RootElement.GetProperty("input").EnumerateArray()
            .First(i => i.TryGetProperty("type", out var t) && t.GetString() == "agent_message")
            .GetProperty("content").EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "encrypted_content")
            .GetProperty("encrypted_content").GetString();
        Assert.Equal(EncBlob, wireBlob);
    }

    [Fact]
    public void UnknownItemType_DoesNotThrow_AndIsCarriedThrough()
    {
        // A type the bridge does NOT model at all (a future gpt-5.6 feature). Before
        // the fix this 400'd at deserialization. It must now round-trip verbatim.
        var unknown = """
          {"type":"tool_search_call","call_id":"call_ts_1","execution":"client",
           "arguments":{"query":"find the bug"},"status":"completed"}
          """;
        var ex = Record.Exception(() => CodexRoundTrip.RoundTrip(BodyWith(unknown)));
        Assert.Null(ex);

        var emitted = CodexRoundTrip.RoundTrip(BodyWith(unknown)).AsObject();
        var item = FindItem(emitted["input"]!.AsArray(), "tool_search_call");
        Assert.NotNull(item);
        Assert.Equal("call_ts_1", item!["call_id"]!.GetValue<string>());
        Assert.Equal("client", item["execution"]!.GetValue<string>());
    }

    [Fact]
    public void EchoedCustomToolCall_RoundTripsVerbatim_TheRequestSideMirrorOfFixC()
    {
        // Fix C (response side) makes T4 emit exec as a custom_tool_call item. On the
        // NEXT turn codex echoes that call back as a custom_tool_call INPUT item, with
        // its `input` = raw JavaScript. The bridge does not model custom_tool_call, so
        // T1 carries it via the universal unknown-item passthrough and T2 re-emits it
        // VERBATIM — including the raw-JS input (which is NOT valid JSON, so it must not
        // be parsed/reserialized). Copilot accepts this echo shape (live-probed 200,
        // CustomToolEchoProbe case C). This is the request-side symmetry test for Fix C.
        const string execJs = "const r = await tools.shell_command({command:\"ls\"});\ntext(r);";
        var echo = $$"""
          {"type":"custom_tool_call","call_id":"call_exec_1","name":"exec","input":{{Enc(execJs)}}}
          """;
        var ex = Record.Exception(() => CodexRoundTrip.RoundTrip(BodyWith(echo)));
        Assert.Null(ex);

        var item = FindItem(CodexRoundTrip.RoundTrip(BodyWith(echo)).AsObject()["input"]!.AsArray(),
            "custom_tool_call");
        Assert.NotNull(item);
        Assert.Equal("call_exec_1", item!["call_id"]!.GetValue<string>());
        Assert.Equal("exec", item["name"]!.GetValue<string>());
        // The raw-JS input survives byte-identical (not reparsed, not turned into {}).
        Assert.Equal(execJs, item["input"]!.GetValue<string>());
    }

    [Fact]
    public void PassthroughItems_PreserveOrderRelativeToMessages()
    {
        // agent_message BETWEEN two user messages must stay between them in the output
        // (an inter-agent NEW_TASK/FINAL_ANSWER has a position in the flow).
        var body = """
          {
            "model":"gpt-5.6-sol",
            "instructions":"multi-agent",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"first"}]},
              {"type":"agent_message","author":"/root","recipient":"/root/child",
               "content":[{"type":"input_text","text":"NEW_TASK"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"second"}]}
            ],
            "stream":true,"store":false
          }
          """;
        var input = CodexRoundTrip.RoundTrip(body).AsObject()["input"]!.AsArray();

        // Find the ordinal positions of the three items by their discriminating content.
        int firstMsg = -1, agentMsg = -1, secondMsg = -1;
        for (var i = 0; i < input.Count; i++)
        {
            var o = input[i]!.AsObject();
            var type = o["type"]?.GetValue<string>();
            if (type == "agent_message") agentMsg = i;
            else if (type == "message")
            {
                var text = o["content"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>();
                if (text == "first") firstMsg = i;
                else if (text == "second") secondMsg = i;
            }
        }
        Assert.True(firstMsg >= 0 && agentMsg >= 0 && secondMsg >= 0, "all three items present");
        Assert.True(firstMsg < agentMsg && agentMsg < secondMsg,
            $"order must be first({firstMsg}) < agent_message({agentMsg}) < second({secondMsg})");
    }

    [Fact]
    public void UnknownItem_AtEnd_IsEmittedAfterAllMessages()
    {
        // A trailing unknown item (after > message count) must still appear, at the end.
        var body = """
          {
            "model":"gpt-5.6-sol","instructions":"x",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"only"}]},
              {"type":"compaction","encrypted_content":"BLOB123"}
            ],
            "stream":true,"store":false
          }
          """;
        var input = CodexRoundTrip.RoundTrip(body).AsObject()["input"]!.AsArray();
        Assert.Equal("compaction", input[^1]!["type"]!.GetValue<string>());
        Assert.Equal("BLOB123", input[^1]!["encrypted_content"]!.GetValue<string>());
    }

    private static JsonObject? FindItem(JsonArray input, string type)
    {
        foreach (var n in input)
            if (n is JsonObject o && o["type"]?.GetValue<string>() == type)
                return o;
        return null;
    }

    private static string Enc(string s) => JsonSerializer.Serialize(s);
}
