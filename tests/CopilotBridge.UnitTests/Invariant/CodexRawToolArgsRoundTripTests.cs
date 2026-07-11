using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the REQUEST-side custom-tool argument carriage (the mirror of
/// the response-side custom-tool fix). A custom (grammar) tool — Codex's `exec` —
/// echoes its prior call back as a <c>function_call</c> whose <c>arguments</c> is
/// RAW TEXT (JavaScript), not a JSON object. T1 must carry that through the IR and
/// T2 must re-emit it verbatim, instead of 400-ing on
/// <c>ExpectedStartOfValueNotFound</c> (the live bug: gpt-5.6 exec loop dies on the
/// SECOND turn when Codex replays the call). Copilot accepts a function_call with
/// raw-text arguments (live-probed 200, <c>CustomToolEchoProbe</c>).
/// </summary>
public class CodexRawToolArgsRoundTripTests
{
    // A prior exec call echoed back, exactly as Codex sends it to the bridge:
    // type=function_call, name=exec, arguments = raw JavaScript (NOT JSON).
    private const string ExecJs =
        "const r = await tools.shell_command({\n  command: \"git status --short\",\n  workdir: \"Q:\\\\proj\"\n});\ntext(r);";

    private static string BuildBody(string argumentsJsonEncoded) => $$"""
      {
        "model":"gpt-5.6-sol",
        "instructions":"You have an exec tool.",
        "input":[
          {"type":"message","role":"user","content":[{"type":"input_text","text":"run it"}]},
          {"type":"function_call","name":"exec","call_id":"call_x1","arguments":{{argumentsJsonEncoded}}},
          {"type":"function_call_output","call_id":"call_x1","output":"ok"},
          {"type":"message","role":"user","content":[{"type":"input_text","text":"now say done"}]}
        ],
        "stream":false,"store":false
      }
      """;

    [Fact]
    public void RawJsFunctionCallArguments_DoesNotThrow_AndRoundTripsVerbatim()
    {
        var body = BuildBody(JsonSerializer.Serialize(ExecJs));

        // T1 must NOT throw (the old ExpectedStartOfValueNotFound 400).
        var ex = Record.Exception(() => CodexRoundTrip.RoundTrip(body));
        Assert.Null(ex);

        var emitted = CodexRoundTrip.RoundTrip(body).AsObject();

        // The emitted upstream input[] must carry the function_call with the RAW JS
        // arguments verbatim — not "{}", not a JSON-escaped double-encoding.
        var fc = FindFunctionCall(emitted["input"]!.AsArray(), "call_x1");
        Assert.NotNull(fc);
        Assert.Equal("exec", fc!["name"]!.GetValue<string>());
        Assert.Equal(ExecJs, fc["arguments"]!.GetValue<string>());
    }

    [Fact]
    public void JsonFunctionCallArguments_StillRoundTripAsObjectString()
    {
        // A normal JSON-object function tool must be UNCHANGED: arguments emitted as
        // the compact JSON object string, exactly as before (no grammar-text path).
        const string jsonArgs = "{\"path\":\"/tmp/x\",\"n\":3}";
        var body = BuildBody(JsonSerializer.Serialize(jsonArgs));

        var emitted = CodexRoundTrip.RoundTrip(body).AsObject();
        var fc = FindFunctionCall(emitted["input"]!.AsArray(), "call_x1");
        Assert.NotNull(fc);
        // arguments is the JSON object serialized back to a string; parse both sides
        // and compare as JSON (whitespace-insensitive) to assert value fidelity.
        var got = JsonNode.Parse(fc!["arguments"]!.GetValue<string>())!;
        var want = JsonNode.Parse(jsonArgs)!;
        Assert.Equal(want.ToJsonString(), got.ToJsonString());
    }

    [Fact]
    public void EmptyArguments_BecomeEmptyObject_NotGrammarText()
    {
        var body = BuildBody("\"\"");   // arguments: ""
        var emitted = CodexRoundTrip.RoundTrip(body).AsObject();
        var fc = FindFunctionCall(emitted["input"]!.AsArray(), "call_x1");
        Assert.NotNull(fc);
        Assert.Equal("{}", fc!["arguments"]!.GetValue<string>());
    }

    private static JsonObject? FindFunctionCall(JsonArray input, string callId)
    {
        foreach (var n in input)
            if (n is JsonObject o
                && o["type"]?.GetValue<string>() == "function_call"
                && o["call_id"]?.GetValue<string>() == callId)
                return o;
        return null;
    }
}
