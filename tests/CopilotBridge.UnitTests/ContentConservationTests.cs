using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Content-conservation contract for Claude-Code → gpt-5.5 (Copilot
/// <c>/responses</c>) translation (T2). For every real captured Claude Code
/// request, this INDEPENDENTLY derives the expected ordered content inventory
/// from the Anthropic IR and reconciles it against what T2 actually emitted,
/// proving the three properties the whole change is judged on:
/// <list type="bullet">
///   <item><b>不多 (nothing added):</b> every emitted message text / function_call /
///         function_call_output / reasoning item traces back to a source block in
///         the IR — no fabricated or duplicated content.</item>
///   <item><b>不映射错 (nothing mis-mapped):</b> text is byte-identical; tool_use
///         <c>input</c> → <c>arguments</c> is byte-identical (canonicalized);
///         tool_result content → <c>output</c> preserves the target's contracted
///         text/image modalities and values; order is preserved.</item>
///   <item><b>不少 (nothing lost):</b> the ONLY content that may leave the wire is a
///         plain <c>thinking</c> block — which gpt-5.5 hard-rejects (live-probed
///         400) and which is model-internal scratch; every other input block has a
///         representation in the output.</item>
/// </list>
/// </summary>
/// <remarks>
/// This is the committed form of the whole-corpus conservation analyzer used to
/// verify the change (Phase A) — it reads the same fixtures as
/// <see cref="TraceReplayResponsesTests"/> plus, when <c>BRIDGE_TRACE_DIR</c> is
/// set, a capped sample of real captures. The reconciliation derives the expected
/// output from the Anthropic contract, so a "consistently wrong" T2 cannot pass:
/// it would produce a MISMATCH/DROP/ADD against the independently-derived
/// expectation.
/// </remarks>
public class ContentConservationTests
{
    private const string TargetModel = "gpt-5.5";
    private static readonly CodexModelProfileCatalog Catalog = new();

    private readonly ITestOutputHelper _output;
    public ContentConservationTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> ClaudeCodeBodies()
    {
        foreach (var (name, json) in TraceCorpus.LoadClaudeCodeBodies())
            yield return [name, json];
    }

    [Theory]
    [MemberData(nameof(ClaudeCodeBodies))]
    public void Translation_ConservesContent(string name, string bodyJson)
    {
        var ir = JsonSerializer.Deserialize(bodyJson, JsonContext.Default.MessagesRequest)!
            with { Model = TargetModel };
        var wire = JsonNode.Parse(ResponsesRequestBuilder.Build(ir, Catalog).Body)!.AsObject();

        AssertConserved(name, ir, wire);
    }

    [Fact]
    public void Translation_ConservesToolResultImageModality()
    {
        const string bodyJson = """
            {
              "model":"claude-opus-5","max_tokens":1024,
              "messages":[
                {"role":"assistant","content":[{"type":"tool_use","id":"toolu_image_inventory","name":"Read","input":{"file_path":"red.png"}}]},
                {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_image_inventory","content":[
                  {"type":"text","text":"image follows"},
                  {"type":"image","source":{"type":"base64","media_type":"image/png","data":"exact-base64"}}
                ]}]}
              ],
              "stream":true
            }
            """;
        var ir = JsonSerializer.Deserialize(bodyJson, JsonContext.Default.MessagesRequest)!
            with { Model = "gpt-5.6-sol" };
        var wire = JsonNode.Parse(ResponsesRequestBuilder.Build(ir, Catalog).Body)!.AsObject();

        AssertConserved("synthetic-tool-result-image", ir, wire);
    }

    private void AssertConserved(string name, MessagesRequest ir, JsonObject wire)
    {
        var findings = Reconcile(ir, wire);
        if (findings.Count > 0)
            _output.WriteLine($"[{name}]\n  " + string.Join("\n  ", findings));

        Assert.True(findings.Count == 0,
            $"[{name}] content-conservation violations:\n" + string.Join("\n", findings));
    }

    /// <summary>
    /// Reconcile IR input against T2 output. Returns a list of violations; empty =
    /// conserved. Intended, live-verified drops (plain thinking) are NOT violations.
    /// </summary>
    private static List<string> Reconcile(MessagesRequest ir, JsonObject wire)
    {
        var findings = new List<string>();

        // system → instructions: verbatim concat, no added content.
        var instr = wire["instructions"]?.GetValue<string>();
        if (ir.System is { Count: > 0 })
        {
            var expected = string.Join("\n", ir.System.Select(s => s.Text));
            if (instr is null) findings.Add("system: all parts DROPPED (no instructions)");
            else if (instr != expected) findings.Add("system: instructions MISMATCH vs concatenated parts");
        }
        else if (!string.IsNullOrEmpty(instr))
        {
            findings.Add("instructions: ADD with no system source");
        }

        // Expected ordered inventory from the IR (thinking recorded as intended-drop).
        var expected1 = new List<Tok>();
        foreach (var msg in ir.Messages)
        {
            var pending = new List<Tok>();
            void Flush()
            {
                foreach (var p in pending) expected1.Add(p);
                pending.Clear();
            }
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case ToolUseBlockParam tu:
                        Flush();
                        expected1.Add(new Tok("function_call", tu.Id, tu.Name + "" + Canon(tu.Input.GetRawText())));
                        break;
                    case ToolResultBlockParam tr:
                        Flush();
                        expected1.Add(new Tok(
                            "function_call_output",
                            tr.ToolUseId,
                            ExpectedOutput(tr.Content, ir.Model)));
                        break;
                    case TextBlockParam t:
                        pending.Add(new Tok("text", msg.Role, t.Text));
                        break;
                    case ImageBlockParam:
                        pending.Add(new Tok("image", msg.Role, "image"));
                        break;
                    case RedactedThinkingBlockParam rt:
                        Flush();
                        expected1.Add(new Tok("reasoning", "", rt.Data));
                        break;
                    case ThinkingBlockParam:
                        // Intended, live-verified drop — not part of the expected wire.
                        break;
                }
            }
            Flush();
        }

        // Actual ordered inventory from the wire.
        var actual = new List<Tok>();
        foreach (var it in wire["input"]!.AsArray())
        {
            var o = it!.AsObject();
            switch (o["type"]!.GetValue<string>())
            {
                case "message":
                    var role = o["role"]!.GetValue<string>();
                    foreach (var p in o["content"]!.AsArray())
                    {
                        var po = p!.AsObject();
                        var pt = po["type"]!.GetValue<string>();
                        if (pt is "input_text" or "output_text")
                            actual.Add(new Tok("text", role, po["text"]!.GetValue<string>()));
                        else if (pt == "input_image")
                            actual.Add(new Tok("image", role, "image"));
                        else
                            actual.Add(new Tok("UNKNOWN:" + pt, role, po.ToJsonString()));
                    }
                    break;
                case "function_call":
                    actual.Add(new Tok("function_call", o["call_id"]?.GetValue<string>() ?? "",
                        (o["name"]?.GetValue<string>() ?? "") + "" + Canon(o["arguments"]?.GetValue<string>() ?? "")));
                    break;
                case "function_call_output":
                    actual.Add(new Tok(
                        "function_call_output",
                        o["call_id"]?.GetValue<string>() ?? "",
                        ActualOutput(o["output"]!)));
                    break;
                case "reasoning":
                    actual.Add(new Tok("reasoning", o["id"]?.GetValue<string>() ?? "", o["encrypted_content"]?.GetValue<string>() ?? ""));
                    break;
                default:
                    actual.Add(new Tok("UNKNOWN:" + o["type"]!.GetValue<string>(), "", o.ToJsonString()));
                    break;
            }
        }

        // Order-preserving positional reconciliation.
        int i = 0, j = 0;
        while (i < expected1.Count && j < actual.Count)
        {
            var e = expected1[i]; var a = actual[j];
            if (e == a) { i++; j++; continue; }
            if (e.Kind == a.Kind && e.Id == a.Id) { findings.Add($"MISMATCH {e.Kind} id={Trim(e.Id)}: {Diff(e.Content, a.Content)}"); i++; j++; continue; }
            var eResync = actual.Skip(j).Take(8).Any(x => x.Kind == e.Kind && x.Id == e.Id && x.Content == e.Content);
            var aResync = expected1.Skip(i).Take(8).Any(x => x.Kind == a.Kind && x.Id == a.Id && x.Content == a.Content);
            if (!eResync) { findings.Add($"DROP {e.Kind} id={Trim(e.Id)}: {Short(e.Content)}"); i++; }
            else if (!aResync) { findings.Add($"ADD {a.Kind} id={Trim(a.Id)}: {Short(a.Content)}"); j++; }
            else { findings.Add($"DESYNC {e.Kind}->{a.Kind}"); i++; j++; }
        }
        for (; i < expected1.Count; i++) findings.Add($"DROP {expected1[i].Kind} id={Trim(expected1[i].Id)}: {Short(expected1[i].Content)}");
        for (; j < actual.Count; j++) findings.Add($"ADD {actual[j].Kind} id={Trim(actual[j].Id)}: {Short(actual[j].Content)}");

        return findings;
    }

    private static string ExpectedOutput(JsonElement? content, string model)
    {
        if (content is not { } c) return "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            if (model == "gpt-5.6-sol" && TryExpectedMultimodalOutput(c, out var structured))
                return structured;

            var sb = new StringBuilder();
            var wroteBlock = false;
            foreach (var b in c.EnumerateArray())
            {
                if (wroteBlock) sb.Append('\n');
                wroteBlock = true;
                if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("type", out var bt)
                    && bt.GetString() is "text" or "input_text" or "output_text"
                    && b.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());
                else sb.Append(b.GetRawText());
            }
            return "scalar:" + sb;
        }
        return "scalar:" + (c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText());
    }

    private static bool TryExpectedMultimodalOutput(JsonElement content, out string inventory)
    {
        var items = new List<string>();
        var sawImage = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                inventory = "";
                return false;
            }

            if (type.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                items.Add("text:" + text.GetString());
                continue;
            }

            if (type.GetString() == "image"
                && TryExpectedImageUrl(block, out var imageUrl))
            {
                items.Add("image:" + imageUrl);
                sawImage = true;
                continue;
            }

            inventory = "";
            return false;
        }

        inventory = sawImage ? "items:" + string.Join('', items) : "";
        return sawImage;
    }

    private static bool TryExpectedImageUrl(JsonElement block, out string imageUrl)
    {
        imageUrl = "";
        if (!block.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("type", out var type))
            return false;

        if (type.GetString() == "base64"
            && source.TryGetProperty("media_type", out var mediaType)
            && source.TryGetProperty("data", out var data)
            && mediaType.ValueKind == JsonValueKind.String
            && data.ValueKind == JsonValueKind.String)
        {
            imageUrl = $"data:{mediaType.GetString()};base64,{data.GetString()}";
            return true;
        }

        if (type.GetString() == "url"
            && source.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String)
        {
            imageUrl = url.GetString() ?? "";
            return imageUrl.Length > 0;
        }

        return false;
    }

    private static string ActualOutput(JsonNode output)
    {
        if (output is JsonValue value && value.TryGetValue<string>(out var scalar))
            return "scalar:" + scalar;
        if (output is not JsonArray array) return "scalar:" + output.ToJsonString();

        var items = new List<string>();
        foreach (var node in array)
        {
            var item = node!.AsObject();
            switch (item["type"]?.GetValue<string>())
            {
                case "input_text":
                    items.Add("text:" + item["text"]!.GetValue<string>());
                    break;
                case "input_image":
                    items.Add("image:" + item["image_url"]!.GetValue<string>());
                    break;
                default:
                    items.Add("unknown:" + item.ToJsonString());
                    break;
            }
        }
        return "items:" + string.Join('', items);
    }

    private static string Canon(string json)
    {
        try { using var d = JsonDocument.Parse(json); return JsonSerializer.Serialize(d.RootElement); }
        catch { return json; }
    }

    private static string Short(string s) => s.Length <= 60 ? s.Replace("\n", " ") : s[..60].Replace("\n", " ") + "…";
    private static string Trim(string s) => s.Length <= 24 ? s : s[..24] + "…";
    private static string Diff(string e, string a) => $"exp[{Short(e)}] act[{Short(a)}]";

    private readonly record struct Tok(string Kind, string Id, string Content);
}
