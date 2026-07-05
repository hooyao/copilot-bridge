using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline.Stages;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="PoisonedContextScanner.Scan"/>. The contract
/// (from the trace-verified rework, <c>docs/gpt55-runaway-diagnosis.md</c>): the
/// poison signal is <b>structural, not lexical</b> — one tool name accumulating many
/// <b>failed</b> tool_results in a single request (the fingerprint of a client
/// retrying the same failing call). Specifically:
/// <list type="bullet">
///   <item>a tool_result is a failure when <c>is_error</c> is true OR its content
///         <b>begins</b> with an API-failure marker (<c>API Error:</c> / <c>Error:</c>,
///         anchored, case-insensitive) — a mid-text mention does NOT count;</item>
///   <item>failures are attributed to the tool that produced them (via
///         <c>tool_use_id</c> → tool name) and aggregated per tool;</item>
///   <item><c>WorstToolFailures</c> is the max over tools, <c>TotalFailures</c> the
///         sum — both independent of the specific error wording.</item>
/// </list>
/// </summary>
public class PoisonedContextScannerTests
{
    private static JsonElement StringContent(string s)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
        return doc.RootElement.Clone();
    }

    private static JsonElement ArrayContent(params string[] texts)
    {
        var parts = string.Join(",", texts.Select(t =>
            $$"""{"type":"text","text":{{JsonSerializer.Serialize(t)}}}"""));
        using var doc = JsonDocument.Parse($"[{parts}]");
        return doc.RootElement.Clone();
    }

    private static ToolUseBlockParam Use(string id, string name)
    {
        using var doc = JsonDocument.Parse("{}");
        return new ToolUseBlockParam { Id = id, Name = name, Input = doc.RootElement.Clone() };
    }

    private static ToolResultBlockParam Result(string useId, JsonElement content, bool? isError = null) =>
        new() { ToolUseId = useId, Content = content, IsError = isError };

    private static MessagesRequest Body(params ContentBlockParam[] blocks) =>
        new()
        {
            Model = "gpt-5.5",
            Messages = [new MessageParam { Role = Role.User, Content = blocks }],
        };

    // ── Structural: same tool failing repeatedly aggregates per tool ─────────────

    [Fact]
    public void AggregatesFailures_PerTool_ByToolName()
    {
        // Reproduces the runaway shape: one tool (Agent) fails many times, a
        // different tool fails once. WorstToolFailures must be Agent's count.
        var blocks = new List<ContentBlockParam>();
        for (var i = 0; i < 6; i++)
        {
            blocks.Add(Use($"a{i}", "Agent"));
            blocks.Add(Result($"a{i}", StringContent("API Error: 400 The requested model is not supported")));
        }
        blocks.Add(Use("g0", "Grep"));
        blocks.Add(Result("g0", StringContent("Error: bad pattern")));

        var r = PoisonedContextScanner.Scan(Body(blocks.ToArray()));

        Assert.Equal("Agent", r.WorstTool);
        Assert.Equal(6, r.WorstToolFailures);
        Assert.Equal(7, r.TotalFailures); // 6 Agent + 1 Grep
    }

    [Fact]
    public void WorstTool_IsTheHighestFailingTool_NotTheTotal()
    {
        // Two tools each fail a few times; the worst single-tool count — not the sum —
        // is the replay-loop signal the stage thresholds on.
        var blocks = new List<ContentBlockParam>();
        for (var i = 0; i < 3; i++) { blocks.Add(Use($"a{i}", "Agent")); blocks.Add(Result($"a{i}", StringContent("API Error: 500"))); }
        for (var i = 0; i < 2; i++) { blocks.Add(Use($"w{i}", "WebSearch")); blocks.Add(Result($"w{i}", StringContent("Error: timeout"))); }

        var r = PoisonedContextScanner.Scan(Body(blocks.ToArray()));

        Assert.Equal(3, r.WorstToolFailures);
        Assert.Equal("Agent", r.WorstTool);
        Assert.Equal(5, r.TotalFailures);
    }

    // ── Lexical-independence: any wording that begins the content counts ─────────

    [Theory]
    [InlineData("API Error: 400 The requested model is not supported")] // the runaway wording
    [InlineData("API Error: 429 rate limited")]                          // quota
    [InlineData("API Error: 403 not available in your region")]          // region (the HK-VM case)
    [InlineData("Error: upstream timeout")]                              // bare Error: prefix
    [InlineData("api error: 500 internal")]                              // case-insensitive
    [InlineData("   API Error: 502 bad gateway")]                        // leading whitespace tolerated
    public void CountsFailure_ForAnyErrorPrefixWording(string content)
    {
        var body = Body(Use("t0", "Agent"), Result("t0", StringContent(content)));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(1, r.TotalFailures);
        Assert.Equal(1, r.WorstToolFailures);
    }

    [Fact]
    public void CountsFailure_ViaIsErrorFlag_EvenWithoutErrorPrefix()
    {
        // is_error=true is honored even when the content doesn't start with a marker.
        var body = Body(Use("t0", "Bash"), Result("t0", StringContent("command exited 1"), isError: true));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(1, r.TotalFailures);
    }

    [Fact]
    public void CountsFailure_InArrayContentForm()
    {
        var body = Body(Use("t0", "Agent"),
            Result("t0", ArrayContent("API Error: 400 The requested model is not supported")));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(1, r.TotalFailures);
    }

    // ── What must NOT count ──────────────────────────────────────────────────────

    [Fact]
    public void DoesNotCount_MidTextErrorMention()
    {
        // A legitimate web-search result that merely DISCUSSES an error mid-text is
        // not a failure — the marker must be at the START of the content. (In the
        // real trace, 3 web/docs results mentioned "API Error" mid-body.)
        var body = Body(Use("w0", "WebSearch"),
            Result("w0", StringContent("Web search results for query: \"retry overloaded_error\" ... API Error: 400 appears in the docs")));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(0, r.TotalFailures);
    }

    [Fact]
    public void DoesNotCount_CleanToolResults()
    {
        var body = Body(
            Use("r0", "Read"), Result("r0", StringContent("1 using System;\n2 namespace X;")),
            Use("g0", "Grep"), Result("g0", StringContent("No matches found")));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(0, r.TotalFailures);
        Assert.Null(r.WorstTool);
    }

    [Fact]
    public void DoesNotCount_LineNumberedOutput_ThatStartsWithDigits()
    {
        // Read's line-numbered output can start with digits ("263- 264- ...") — this
        // must NOT be mistaken for an error (the signature anchors on API Error:/Error:,
        // never a bare leading number).
        var body = Body(Use("r0", "Read"), Result("r0", StringContent("263- 264- 265- source line")));

        var r = PoisonedContextScanner.Scan(body);

        Assert.Equal(0, r.TotalFailures);
    }

    [Fact]
    public void ReturnsNone_ForEmptyConversation()
    {
        var r = PoisonedContextScanner.Scan(new MessagesRequest
        {
            Model = "gpt-5.5",
            Messages = System.Array.Empty<MessageParam>(),
        });

        Assert.Equal(PoisonScanResult.None, r);
    }
}
