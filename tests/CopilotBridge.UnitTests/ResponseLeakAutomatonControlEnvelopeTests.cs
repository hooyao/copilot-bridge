using System.Collections.Generic;
using System.Linq;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Exhaustive contract tests for <see cref="ResponseLeakAutomaton"/> — the
/// second high-risk detection automaton. Asserts the shape-based, closed-envelope
/// leak contract for each of Claude Code's control envelopes (task-notification,
/// teammate-message, channel, cross-session-message, tick), split-boundary
/// invariance, KMP failure-edge restarts, missing-proof negatives, fence
/// semantics, per-block reset, and bounded fail-open. Each test states the
/// contract it guards. Tests derive from the required behaviour (the envelope
/// shapes Claude Code renders), NOT from the implementation.
/// </summary>
public class ResponseLeakAutomatonControlEnvelopeTests
{
    /// <summary>Feed a whole string into a fresh automaton; return whether it tripped.</summary>
    private static bool Detect(string text)
    {
        var a = new ResponseLeakAutomaton();
        foreach (var c in text)
        {
            if (a.Feed(c)) return true;
        }
        return a.Tripped;
    }

    private static string? Subject(string text)
    {
        var a = new ResponseLeakAutomaton();
        foreach (var c in text) a.Feed(c);
        return a.MatchedSubject;
    }

    // ---- Canonical envelope shapes (as Claude Code renders them) ----------

    private const string TaskNotification =
        "A background agent completed a task:\n" +
        "<task-notification>\n" +
        "<task-id>abc-123</task-id>\n" +
        "<tool-use-id>tu-9</tool-use-id>\n" +
        "<output-file>/tmp/out.txt</output-file>\n" +
        "<status>completed</status>\n" +
        "<summary>Refactored the catalog.</summary>\n" +
        "</task-notification>";

    private const string TeammateMessage =
        "<teammate-message teammate_id=\"alice\" color=\"red\" summary=\"Brief update\">\n" +
        "message content\n" +
        "</teammate-message>";

    private const string Channel =
        "<channel source=\"server\" user=\"bob\" chat_id=\"c1\">\n" +
        "channel body\n" +
        "</channel>";

    private const string CrossSession =
        "<cross-session-message from=\"agent-x\">hello from another session</cross-session-message>";

    private const string Tick = "<tick>2026-07-02T10:00:00Z</tick>";

    public static IEnumerable<object[]> AllEnvelopes() => new[]
    {
        new object[] { TaskNotification, "task-notification" },
        new object[] { TeammateMessage, "teammate-message" },
        new object[] { Channel, "channel" },
        new object[] { CrossSession, "cross-session-message" },
        new object[] { Tick, "tick" },
    };

    // ---- Positives -------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void ClosedShapeValidUnfenced_IsLeak(string envelope, string subject)
    {
        // Contract: each closed, shape-valid, unfenced control envelope is a leak.
        Assert.True(Detect(envelope), $"expected {subject} to be detected");
    }

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void MatchedSubject_NamesTheEnvelope(string envelope, string subject)
    {
        // Contract: on a leak the automaton names the leaked subject (for the log).
        Assert.Equal(subject, Subject(envelope));
    }

    [Fact]
    public void CleanProse_NoSubject_NotLeak()
    {
        // Contract: benign prose that merely mentions the tag names is not a leak,
        // and MatchedSubject stays null.
        var s = "The coordinator sends a task-notification when a worker finishes, " +
                "and teammate messages arrive as special content, plus a tick keepalive.";
        Assert.False(Detect(s));
        Assert.Null(Subject(s));
    }

    [Fact]
    public void TaskNotification_MinimalProof_Status_IsLeak()
    {
        // task-id + a single proof child (status) is sufficient.
        var s = "<task-notification><task-id>x</task-id><status>failed</status></task-notification>";
        Assert.True(Detect(s));
    }

    [Fact]
    public void TaskNotification_MinimalProof_OutputFile_IsLeak()
    {
        var s = "<task-notification><task-id>x</task-id><output-file>/o</output-file></task-notification>";
        Assert.True(Detect(s));
    }

    // ---- Split-boundary invariance (the core streaming property) ---------

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void EverySplitPoint_DetectsIdentically(string envelope, string subject)
    {
        // Contract: detection is invariant across ALL split boundaries. Feed the
        // envelope split at each possible index into two "deltas".
        for (var i = 0; i <= envelope.Length; i++)
        {
            var a = new ResponseLeakAutomaton();
            var tripped = false;
            foreach (var c in envelope[..i]) tripped |= a.Feed(c);
            foreach (var c in envelope[i..]) tripped |= a.Feed(c);
            Assert.True(tripped || a.Tripped, $"missed {subject} when split at index {i}");
        }
    }

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void CharByChar_SameResult(string envelope, string subject)
    {
        // Contract: feeding one character per delta detects identically.
        var a = new ResponseLeakAutomaton();
        var tripped = envelope.Aggregate(false, (acc, c) => acc || a.Feed(c));
        Assert.True(tripped, $"missed {subject} char-by-char");
    }

    [Fact]
    public void SplitInsideAttributeValue_StillDetected()
    {
        // teammate_id value "alice" arriving as "al" | "ice".
        var a = new ResponseLeakAutomaton();
        var parts = new[]
        {
            "<teammate-message teammate_id=\"al",
            "ice\">hi</teammate-message>",
        };
        var tripped = false;
        foreach (var part in parts)
            foreach (var c in part) tripped |= a.Feed(c);
        Assert.True(tripped);
    }

    // ---- Failure-edge / overlapping restart ------------------------------

    [Fact]
    public void DoubleAngleBracket_RestartNotMissed()
    {
        // Contract: a false '<' immediately before the real opening token must not
        // drop the valid restart. A naive reset-to-0 would miss this.
        Assert.True(Detect("<" + TaskNotification.TrimStart()));
        Assert.True(Detect("<" + Tick));
    }

    [Fact]
    public void OverlappingPartialTag_ThenRealOpen_Detected()
    {
        // "<task<task-notification>…" — a partial tag name, then the real open.
        var s = "<task<task-notification><task-id>x</task-id><summary>s</summary></task-notification>";
        Assert.True(Detect(s));
    }

    [Fact]
    public void RepeatedFalseStarts_ThenRealTick()
    {
        Assert.True(Detect("<ti<ti" + Tick));
    }

    // ---- Fences ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void Fenced_Envelope_NotLeak(string envelope, string subject)
    {
        // Contract: a fenced example (```) is teaching, not a leak, when fences are
        // tracked (text blocks).
        Assert.False(Detect("```\n" + envelope + "\n```"), $"{subject} inside a fence must not trip");
    }

    [Fact]
    public void FenceToggles_LeakAfterFenceClosed_IsLeak()
    {
        // A fenced example, then the fence closes, then a REAL bare leak → leak.
        var s = "```\n" + Tick + "\n```\n" + Tick;
        Assert.True(Detect(s));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("````")]
    public void FenceRunLengths_ToggleOnce(string fence)
    {
        // A fence run of 3+ backticks toggles exactly once (4+ does not double-toggle
        // back to unfenced, which would wrongly expose the envelope).
        var s = fence + "\n" + TeammateMessage + "\n" + fence;
        Assert.False(Detect(s));
    }

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void FencesNotTracked_FencedEnvelopeStillDetected(string envelope, string subject)
    {
        // Contract: thinking blocks have no fence concept — with trackFences:false a
        // ```-wrapped envelope is still a leak (fences are treated as plain text).
        var a = new ResponseLeakAutomaton();
        a.Reset(trackFences: false);
        var s = "```\n" + envelope + "\n```";
        var tripped = false;
        foreach (var c in s) tripped |= a.Feed(c);
        Assert.True(tripped, $"{subject} should trip when fences are not tracked");
    }

    // ---- Per-block reset -------------------------------------------------

    [Fact]
    public void Reset_ClearsStateBetweenBlocks()
    {
        // Contract: state fully resets on a new block; a signature never assembles
        // across a block boundary (open in block A, close in block B).
        var a = new ResponseLeakAutomaton();
        foreach (var c in "<task-notification><task-id>x</task-id><summary>s</summary>") a.Feed(c);
        a.Reset(); // new content_block_start
        var tripped = false;
        foreach (var c in "</task-notification>") tripped |= a.Feed(c);
        Assert.False(tripped);
        Assert.False(a.Tripped);
    }

    // ---- Negatives: unclosed / incomplete --------------------------------

    [Theory]
    [InlineData("<task-notification><task-id>x</task-id><summary>s</summary>")] // no close
    [InlineData("<teammate-message teammate_id=\"alice\">body")]                 // no close
    [InlineData("<channel source=\"server\">body")]                              // no close
    [InlineData("<cross-session-message from=\"a\">body")]                       // no close
    [InlineData("<tick>2026-01-01")]                                            // no close
    public void UnclosedEnvelope_NotLeak(string s)
    {
        Assert.False(Detect(s));
    }

    // ---- Negatives: missing required proof -------------------------------

    [Fact]
    public void TaskNotification_NoTaskId_NotLeak()
    {
        var s = "<task-notification><summary>done</summary></task-notification>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void TaskNotification_NoProofChild_NotLeak()
    {
        // task-id alone (no summary/status/output-file) is not enough.
        var s = "<task-notification><task-id>x</task-id></task-notification>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void TeammateMessage_NoTeammateId_NotLeak()
    {
        var s = "<teammate-message color=\"red\">hi</teammate-message>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void TeammateMessage_EmptyTeammateId_NotLeak()
    {
        var s = "<teammate-message teammate_id=\"\">hi</teammate-message>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void Channel_NoSource_NotLeak()
    {
        var s = "<channel user=\"bob\">body</channel>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void CrossSession_NoFrom_NotLeak()
    {
        var s = "<cross-session-message>body</cross-session-message>";
        Assert.False(Detect(s));
    }

    [Theory]
    [InlineData("<tick></tick>")]     // empty inner
    [InlineData("<tick></tick> and more")]
    public void Tick_EmptyInner_NotLeak(string s)
    {
        Assert.False(Detect(s));
    }

    // ---- The channel-message sibling must not trip <channel> -------------

    [Fact]
    public void ChannelMessageWrapper_DoesNotTripChannel()
    {
        // Contract: <channel-message …>…</channel-message> is a DIFFERENT wrapper;
        // its close is </channel-message>, so the <channel> matcher (close
        // </channel>) never trips on it.
        var s = "<channel-message source=\"server\">payload</channel-message>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void ChannelMessageWrapper_ThenStrayChannelClose_DoesNotTrip()
    {
        // Contract (regression): a <channel-message …> sibling must NOT leave the
        // <channel> matcher in an "opened" state — otherwise a later bare </channel>
        // appearing anywhere in the block would falsely close it. The tag-name
        // boundary (whitespace must follow <channel>) rejects the longer
        // <channel-message> name outright, so nothing is ever opened.
        var s = "<channel-message source=\"server\">payload</channel-message>"
              + "\nlater the prose mentions a bare </channel> on its own line";
        Assert.False(Detect(s));
    }

    [Fact]
    public void Channel_LookalikeAttribute_DoesNotTrip()
    {
        // Contract (regression): an attribute whose name merely ENDS with the
        // required name (data-source vs source) must NOT satisfy the required
        // attribute. Anchoring the attribute match to a whitespace boundary stops
        // the "source=\"" substring of "data-source=\"" from proving the shape.
        var s = "<channel data-source=\"server\">body</channel>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void Channel_SourceNotFirstAttribute_IsLeak()
    {
        // Contract: the required attribute need not be first — a genuine source="…"
        // at a later whitespace boundary still proves the shape and trips.
        var s = "<channel user=\"bob\" source=\"server\">body</channel>";
        Assert.True(Detect(s));
        Assert.Equal("channel", Subject(s));
    }

    // ---- Bounded fail-open -----------------------------------------------

    [Fact]
    public void RunawayAttributeValue_FailsOpen()
    {
        // Contract: an attribute value that runs on far past any real id abandons
        // the envelope (fail-open, bounded buffer) — no unbounded capture, and the
        // envelope is not classified as a leak.
        var huge = new string('x', 5000);
        var s = "<teammate-message teammate_id=\"" + huge + "\">hi</teammate-message>";
        Assert.False(Detect(s));
    }

    // ---- Multiple envelopes in one block ---------------------------------

    [Fact]
    public void RejectedPartial_ThenValidEnvelope_SameSubject_StillDetects()
    {
        // A task-notification missing its proof child (rejected on close), then a
        // full valid one → the later valid envelope still trips.
        var s = "<task-notification><task-id>a</task-id></task-notification>" + // rejected
                TaskNotification;                                                // valid
        Assert.True(Detect(s));
    }

    [Fact]
    public void RejectedEnvelope_ThenValidDifferentSubject_StillDetects()
    {
        // A channel missing source (rejected), then a valid tick → tick trips.
        var s = "<channel user=\"bob\">body</channel>" + Tick;
        Assert.True(Detect(s));
        Assert.Equal("tick", Subject(s));
    }

    [Fact]
    public void FencedThenBareDifferentEnvelope_Detected()
    {
        // A fenced teammate example (not a leak), then a bare channel leak → leak.
        var s = "```\n" + TeammateMessage + "\n```\n" + Channel;
        Assert.True(Detect(s));
        Assert.Equal("channel", Subject(s));
    }

    // ---- Signature id & per-signature gating -----------------------------

    [Theory]
    [MemberData(nameof(AllEnvelopes))]
    public void MatchedSignature_NamesTheEnvelope(string envelope, string subject)
    {
        // Contract: for a control envelope the signature id equals its subject — the
        // stable kebab id used both to gate the matcher and to name the disable
        // switch.
        var a = new ResponseLeakAutomaton();
        foreach (var c in envelope) a.Feed(c);
        Assert.True(a.Tripped);
        Assert.Equal(subject, a.MatchedSignature);
    }

    [Fact]
    public void DisabledSignature_OmitsOnlyThatMatcher_SiblingsStillTrip()
    {
        // Contract: removing one id from the enabled set builds every matcher EXCEPT
        // that one — the disabled envelope can't trip, while an enabled sibling does.
        var enabled = new HashSet<string>(LeakSignatures.All);
        enabled.Remove(LeakSignatures.Channel);

        var chan = new ResponseLeakAutomaton(enabledSignatures: enabled);
        foreach (var c in Channel) chan.Feed(c);
        Assert.False(chan.Tripped);
        Assert.Null(chan.MatchedSubject);

        var task = new ResponseLeakAutomaton(enabledSignatures: enabled);
        foreach (var c in TaskNotification) task.Feed(c);
        Assert.True(task.Tripped);
        Assert.Equal("task-notification", task.MatchedSignature);
    }
}
