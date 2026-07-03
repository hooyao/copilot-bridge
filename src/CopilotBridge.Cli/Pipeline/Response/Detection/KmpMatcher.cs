namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Minimal KMP single-pattern matcher fed one character at a time. On a mismatch
/// it follows the failure function so an overlapping restart is not lost (e.g.
/// <c>&lt;&lt;</c> against a pattern starting with <c>&lt;</c> keeps the second
/// <c>&lt;</c> as a length-1 match rather than resetting to 0).
/// </summary>
/// <remarks>
/// Shared by <see cref="ResponseLeakAutomaton"/> and all of its per-signature
/// matchers (the <c>&lt;invoke&gt;</c> tool-call matcher and the control-envelope
/// matchers): the character-fed, O(1)-state discipline lets a fixed signature
/// split across streamed deltas be matched without retaining content.
/// </remarks>
internal sealed class KmpMatcher
{
    private readonly string _pat;
    private readonly int[] _fail;
    private int _state;

    public KmpMatcher(string pattern)
    {
        _pat = pattern;
        _fail = BuildFailure(pattern);
    }

    public void Reset() => _state = 0;

    /// <summary>Feed one char; returns true when the full pattern just matched.</summary>
    public bool Feed(char c)
    {
        while (_state > 0 && c != _pat[_state])
        {
            _state = _fail[_state - 1];
        }
        if (c == _pat[_state])
        {
            _state++;
        }
        if (_state == _pat.Length)
        {
            // Fall back to the failure of the full match so a repeated/overlapping
            // pattern can re-trigger on subsequent characters.
            _state = _fail[_state - 1];
            return true;
        }
        return false;
    }

    private static int[] BuildFailure(string pattern)
    {
        var fail = new int[pattern.Length];
        var k = 0;
        for (var i = 1; i < pattern.Length; i++)
        {
            while (k > 0 && pattern[i] != pattern[k])
            {
                k = fail[k - 1];
            }
            if (pattern[i] == pattern[k])
            {
                k++;
            }
            fail[i] = k;
        }
        return fail;
    }
}
