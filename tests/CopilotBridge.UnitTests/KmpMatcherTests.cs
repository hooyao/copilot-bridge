using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="KmpMatcher"/>'s per-pattern failure-table cache.
/// Two contracts: (1) all instances of a given pattern SHARE one immutable failure
/// table (the memoization invariant that removes per-request recomputation); and
/// (2) sharing that table does NOT couple instances — each keeps its own match
/// cursor, so one instance advancing or resetting never disturbs another. Behaviour
/// (including KMP overlapping-restart) must be identical to a non-cached matcher.
/// </summary>
public class KmpMatcherTests
{
    /// <summary>Feed a string; return the 0-based indices where the pattern completed.</summary>
    private static List<int> MatchEnds(KmpMatcher m, string input)
    {
        var ends = new List<int>();
        for (var i = 0; i < input.Length; i++)
        {
            if (m.Feed(input[i])) ends.Add(i);
        }
        return ends;
    }

    [Fact]
    public void SamePattern_SharesFailureTableInstance()
    {
        // Contract: the failure table is a pure function of the pattern and is
        // never mutated, so every instance of one pattern must reference the SAME
        // array — that is exactly the per-request recomputation the cache removes.
        var a = new KmpMatcher("</invoke>");
        var b = new KmpMatcher("</invoke>");

        Assert.Same(a.FailureTable, b.FailureTable);
    }

    [Fact]
    public void DistinctPatterns_GetDistinctFailureTables()
    {
        // Contract: the cache keys on the pattern, so different patterns must not
        // collide onto one table.
        var a = new KmpMatcher("<task-notification>");
        var b = new KmpMatcher("</task-notification>");

        Assert.NotSame(a.FailureTable, b.FailureTable);
    }

    [Fact]
    public void SharedTable_DoesNotCoupleInstanceState()
    {
        // Contract: sharing the immutable failure table must not share the match
        // cursor. Advance instance A partway into the pattern, then drive instance
        // B to a full match — B must still complete exactly where a fresh matcher
        // would, proving _state is per-instance.
        const string pat = "<invoke name=\"";
        var a = new KmpMatcher(pat);
        var b = new KmpMatcher(pat);

        // Advance A partway (not to a full match) — must not leak into B.
        foreach (var c in "<invoke ") a.Feed(c);

        var bEnds = MatchEnds(b, pat);
        Assert.Equal(new List<int> { pat.Length - 1 }, bEnds);
    }

    [Fact]
    public void OverlappingRestart_StillMatches_WithCache()
    {
        // Contract: the KMP failure edge must survive memoization — an overlapping
        // restart like "<<invoke name=\"" keeps the second '<' as a length-1 match
        // and still completes, rather than resetting to 0 and missing it.
        var m = new KmpMatcher("<invoke name=\"");
        var ends = MatchEnds(m, "<<invoke name=\"");

        Assert.Single(ends);
        Assert.Equal("<<invoke name=\"".Length - 1, ends[0]);
    }

    [Fact]
    public void CachedTableContents_ArePinned()
    {
        // Contract: memoization must not alter the failure function's output. Pin a
        // pattern with a known non-trivial self-overlap so a regression in the
        // cached values is caught directly.  "aabaa" → [0,1,0,1,2].
        var m = new KmpMatcher("aabaa");
        Assert.Equal(new[] { 0, 1, 0, 1, 2 }, m.FailureTable);
    }

    [Fact]
    public void RepeatedPattern_ReTriggersAcrossInstances()
    {
        // Contract: two instances of the same pattern (thus the same shared table)
        // each match a repeated input identically and independently.
        var a = new KmpMatcher("ab");
        var b = new KmpMatcher("ab");

        Assert.Equal(new List<int> { 1, 3, 5 }, MatchEnds(a, "ababab"));
        Assert.Equal(new List<int> { 1, 3, 5 }, MatchEnds(b, "ababab"));
    }
}
