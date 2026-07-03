using System.Collections.Generic;
using System.Linq;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Exhaustive contract tests for the <c>&lt;invoke&gt;</c> tool-call facet of
/// <see cref="ResponseLeakAutomaton"/> — the highest-risk component. Asserts the
/// structural leak contract (closed + balanced + in-tools + unfenced),
/// split-boundary invariance, KMP failure-edge restarts, negatives, per-block
/// reset, and name-bound fail-open. Each test states the contract it guards.
/// </summary>
public class ResponseLeakAutomatonInvokeTests
{
    private static readonly string[] Tools = { "Read", "Edit", "Bash", "Grep" };

    /// <summary>Feed a whole string into a fresh automaton; return whether it tripped.</summary>
    private static bool Detect(string text, IEnumerable<string>? tools = null)
    {
        var a = new ResponseLeakAutomaton(tools ?? Tools);
        foreach (var c in text)
        {
            if (a.Feed(c)) return true;
        }
        return a.Tripped;
    }

    [Fact]
    public void MatchedSubject_ExposedOnLeak_NullOtherwise()
    {
        // Contract: on a genuine leak the automaton names the matched tool; on a
        // clean/non-leak block it stays null. Feeds the tool name the detector
        // needs to log.
        var leaked = new ResponseLeakAutomaton(Tools);
        foreach (var c in MinimalLeak) leaked.Feed(c);
        Assert.True(leaked.Tripped);
        Assert.Equal("Read", leaked.MatchedSubject);

        var clean = new ResponseLeakAutomaton(Tools);
        foreach (var c in "just prose, no tool call here") clean.Feed(c);
        Assert.False(clean.Tripped);
        Assert.Null(clean.MatchedSubject);
    }

    // A minimal genuine leak: closed, balanced, one parameter, real tool, unfenced.
    private const string MinimalLeak =
        "<invoke name=\"Read\">\n<parameter name=\"file_path\">/x</parameter>\n</invoke>";

    // The real trace shape (seq 0060 style): prose + drifting token + closed call.
    private const string TraceLeak =
        "现在更新 catalog。\ncourt\n" +
        "<invoke name=\"Read\">\n" +
        "<parameter name=\"file_path\">Q:\\foo.cs</parameter>\n" +
        "<parameter name=\"limit\">12</parameter>\n" +
        "<parameter name=\"offset\">50</parameter>\n" +
        "</invoke>";

    // ---- Positives -------------------------------------------------------

    [Fact]
    public void MinimalClosedInToolsUnfenced_IsLeak()
    {
        // Contract: a single closed balanced <invoke name="X">…</invoke> with >=1
        // closed <parameter>, X in tools[], not fenced → leak.
        Assert.True(Detect(MinimalLeak));
    }

    [Fact]
    public void RealTraceShape_IsLeak()
    {
        // Contract: the observed leak shape (prose + 'court' + multi-param call)
        // is detected; the drifting prefix token is irrelevant.
        Assert.True(Detect(TraceLeak));
    }

    [Theory]
    [InlineData("court")]
    [InlineData("call")]
    [InlineData("")]
    public void PrefixTokenIrrelevant(string prefix)
    {
        // Contract: detection does not depend on the (drifting) prefix token.
        Assert.True(Detect(prefix + "\n" + MinimalLeak));
    }

    [Fact]
    public void MultipleParameters_Balanced_IsLeak()
    {
        var s = "<invoke name=\"Edit\">"
              + "<parameter name=\"a\">1</parameter>"
              + "<parameter name=\"b\">2</parameter>"
              + "</invoke>";
        Assert.True(Detect(s));
    }

    // ---- Split-boundary invariance (the core streaming property) ---------

    [Fact]
    public void CharByChar_SameResult()
    {
        // Contract: feeding one character per delta detects identically.
        var a = new ResponseLeakAutomaton(Tools);
        var tripped = TraceLeak.Aggregate(false, (acc, c) => acc || a.Feed(c));
        Assert.True(tripped);
    }

    [Fact]
    public void EverySplitPoint_DetectsIdentically()
    {
        // Contract: detection is invariant across ALL split boundaries. Feed the
        // same leak split at each possible index into two "deltas".
        for (var i = 0; i <= MinimalLeak.Length; i++)
        {
            var a = new ResponseLeakAutomaton(Tools);
            var first = MinimalLeak[..i];
            var second = MinimalLeak[i..];
            var tripped = false;
            foreach (var c in first) tripped |= a.Feed(c);
            foreach (var c in second) tripped |= a.Feed(c);
            Assert.True(tripped || a.Tripped, $"missed leak when split at index {i}");
        }
    }

    [Fact]
    public void SplitInsideToolName_StillDetected()
    {
        // "Read" arriving as "Re" | "ad".
        var a = new ResponseLeakAutomaton(Tools);
        var parts = new[]
        {
            "<invoke name=\"Re",
            "ad\"><parameter name=\"p\">v</parameter></invoke>",
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
        // Contract: a false '<' immediately before the real token must not drop
        // the valid restart. A naive reset-to-0 would miss this.
        Assert.True(Detect("<" + MinimalLeak));
        Assert.True(Detect("<<" + MinimalLeak.TrimStart('<')));
    }

    [Fact]
    public void RepeatedFalseStarts_ThenRealToken()
    {
        Assert.True(Detect("<in<in" + MinimalLeak));
    }

    [Fact]
    public void PartialInvokeThenRealOne_SameBlock()
    {
        // An incomplete <invoke name= that never closes, then a real closed one.
        var s = "<invoke name=\"Read\" (oops no close here) "
              + MinimalLeak;
        Assert.True(Detect(s));
    }

    // ---- Negatives -------------------------------------------------------

    [Fact]
    public void Unbalanced_MoreOpenThanClose_NotLeak()
    {
        // Prose quotation shape: opens without matching closes.
        var s = "<invoke name=\"Read\"> <invoke name=\"Edit\"> </invoke>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void NoClosedParameter_NotLeak()
    {
        var s = "<invoke name=\"Read\"></invoke>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void OpenParameterNeverClosed_NotLeak()
    {
        var s = "<invoke name=\"Read\"><parameter name=\"p\">v</invoke>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void ToolNameNotInTools_NotLeak()
    {
        var s = "<invoke name=\"FooTool\"><parameter name=\"p\">v</parameter></invoke>";
        Assert.False(Detect(s));
    }

    [Fact]
    public void Fenced_ClosedInvoke_NotLeak()
    {
        var s = "```\n" + MinimalLeak + "\n```";
        Assert.False(Detect(s));
    }

    [Fact]
    public void FenceToggles_LeakAfterFenceClosed_IsLeak()
    {
        // A fenced example, then the fence closes, then a REAL bare leak → leak.
        var s = "```\n<invoke name=\"Read\"><parameter name=\"p\">v</parameter></invoke>\n```\n"
              + MinimalLeak;
        Assert.True(Detect(s));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("````")]
    public void FenceRunLengths_ToggleOnce(string fence)
    {
        // A fence run of 3+ backticks toggles exactly once (4+ does not double-toggle
        // back to unfenced, which would wrongly expose the invoke).
        var s = fence + "\n" + MinimalLeak + "\n" + fence;
        Assert.False(Detect(s));
    }

    [Fact]
    public void FencesNotTracked_FencedInvokeStillDetected()
    {
        // Contract: thinking blocks have no fence concept — with trackFences:false
        // a ```-wrapped invoke is still a leak (fences are treated as plain text).
        var a = new ResponseLeakAutomaton(Tools);
        a.Reset(trackFences: false);
        var s = "```\n" + MinimalLeak + "\n```";
        var tripped = false;
        foreach (var c in s) tripped |= a.Feed(c);
        Assert.True(tripped);
    }

    // ---- Boundary / reset ------------------------------------------------

    [Fact]
    public void Reset_ClearsStateBetweenBlocks()
    {
        // Contract: state fully resets on a new block; a signature never assembles
        // across a block boundary (open in block A, close in block B).
        var a = new ResponseLeakAutomaton(Tools);
        foreach (var c in "<invoke name=\"Read\"><parameter name=\"p\">v</parameter>") a.Feed(c);
        a.Reset(); // new content_block_start
        var tripped = false;
        foreach (var c in "</invoke>") tripped |= a.Feed(c);
        Assert.False(tripped);
        Assert.False(a.Tripped);
    }

    [Fact]
    public void CleanBlockAfterRejectedPartial_NotLeak()
    {
        // A block that only quotes syntax (no closed balanced call) is clean.
        var s = "Here's how you call Read: write <invoke name=\"Read\"> then params.";
        Assert.False(Detect(s));
    }

    // ---- Name-capture bound (fail-open) ----------------------------------

    [Fact]
    public void RunawayName_FailsOpen()
    {
        // Contract: a <invoke name=" with a very long unterminated name abandons
        // the invoke (fail-open) — no unbounded buffer, and any later "</invoke>"
        // in that runaway does not trip.
        var longName = new string('x', 5000);
        var s = "<invoke name=\"" + longName + "\"><parameter name=\"p\">v</parameter></invoke>";
        Assert.False(Detect(s));
    }

    // ---- Balanced-count semantics ----------------------------------------

    [Fact]
    public void CloseParameterDoesNotCountAsOpen()
    {
        // Guards the <parameter vs </parameter> distinction: a lone </parameter>
        // must not satisfy paramOpen==paramClose>=1 by miscounting.
        var s = "<invoke name=\"Read\"></parameter></invoke>";
        Assert.False(Detect(s));
    }
}
