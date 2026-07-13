using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Guards the T3 terminal contract for the Claude-Code → gpt-5.5 streaming path:
/// a completed <c>/responses</c> stream must translate to EXACTLY ONE Anthropic
/// <c>message_start</c> and one <c>message_stop</c> — the well-formed envelope a
/// Claude Code client can parse.
/// </summary>
/// <remarks>
/// <para><b>The bug this pins.</b> The production strategy
/// (<c>CopilotResponsesStrategy.TranslateStreamAsync</c>) runs the T3 state
/// machine over every upstream event and then calls
/// <see cref="ResponsesToAnthropicStream.FlushTerminal"/> <i>unconditionally</i>
/// after the loop (it must, to terminate a stream that ended without
/// <c>response.completed</c>). But on the normal path, <c>response.completed</c>
/// already drove <see cref="ResponsesToAnthropicStream.Flush"/>, which emitted
/// <c>message_delta</c>+<c>message_stop</c> and reset <c>_messageStarted=false</c>.
/// The old <c>FlushTerminal</c> then saw <c>!_messageStarted</c> and synthesized a
/// SECOND, dangling <c>message_start</c> (empty content, no matching
/// <c>message_stop</c>). Claude Code parses the extra envelope as a new message —
/// the "replies look weird / garbled" symptom on gpt-5.5.</para>
/// <para>The test replicates the exact production call sequence — Translate loop
/// then FlushTerminal — not just <c>Flush()</c> (the older helper used
/// <c>Flush()</c>, which is why the gap survived). It goes RED on the pre-fix
/// state machine.</para>
/// </remarks>
public class ResponsesStreamTerminalTests
{
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
        if (evt is not null || data.Length > 0)
            items.Add(new SseItem<string>(data.ToString(), evt));
        return items;
    }

    /// <summary>
    /// Drive T3 the way the production strategy does: translate every upstream
    /// event, THEN call FlushTerminal once after a clean loop.
    /// </summary>
    private static List<SseItem<string>> RunLikeStrategy(List<SseItem<string>> upstream, string model = "gpt-5.5")
    {
        var sm = new ResponsesToAnthropicStream(model);
        var ir = new List<SseItem<string>>();
        foreach (var e in upstream) ir.AddRange(sm.Translate(e));
        ir.AddRange(sm.FlushTerminal()); // mirrors CopilotResponsesStrategy
        return ir;
    }

    private static string? Type(SseItem<string> e)
    {
        try
        {
            using var d = JsonDocument.Parse(e.Data);
            return d.RootElement.TryGetProperty("type", out var t) ? t.GetString() : e.EventType;
        }
        catch { return e.EventType; }
    }

    [Theory]
    [InlineData("responses-sse-text.txt")]
    [InlineData("responses-sse-toolcall.txt")]
    public void CompletedStream_YieldsExactlyOneMessageStartAndStop(string fixture)
    {
        var upstream = ParseSse(Path.Combine(FixturesDir, fixture));
        var ir = RunLikeStrategy(upstream);

        var types = ir.Select(Type).ToList();
        var starts = types.Count(t => t == "message_start");
        var stops = types.Count(t => t == "message_stop");

        Assert.Equal(1, starts);
        Assert.Equal(1, stops);

        // And the terminal is genuinely terminal: message_start is the FIRST event
        // and message_stop is the LAST — no envelope dangles after the stop.
        Assert.Equal("message_start", types.First(t => t is "message_start" or "message_stop"));
        Assert.Equal("message_stop", types.Last(t => t is "message_start" or "message_stop"));
        Assert.Equal("message_stop", types[^1]);
    }

    [Fact]
    public void EmptyStream_StillYieldsOneWellFormedTerminal()
    {
        // Contract: an upstream that produced NOTHING (no response.created) must
        // still terminate with exactly one message_start + message_stop so the
        // client sees a complete, empty turn rather than a hung stream.
        var sm = new ResponsesToAnthropicStream("gpt-5.5");
        var ir = new List<SseItem<string>>();
        ir.AddRange(sm.FlushTerminal());

        var types = ir.Select(Type).ToList();
        Assert.Equal(1, types.Count(t => t == "message_start"));
        Assert.Equal(1, types.Count(t => t == "message_stop"));
    }
}
