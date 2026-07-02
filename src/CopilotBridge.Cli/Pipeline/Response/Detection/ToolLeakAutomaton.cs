using System.Collections.Generic;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Single-pass streaming detector for a tool-call leak: a Copilot-served model
/// emitting a CLOSED, balanced <c>&lt;invoke name="X"&gt;…&lt;parameter…&gt;…
/// &lt;/parameter&gt;…&lt;/invoke&gt;</c> as literal text inside a text/thinking
/// content block, where <c>X</c> is a real tool from the request and the block
/// is not inside a markdown code fence.
/// </summary>
/// <remarks>
/// <para><b>Character-fed, O(1) state, retains no content.</b> Each character of
/// streamed block text is fed exactly once via <see cref="Feed(char)"/>; the
/// machine's state (not a text buffer) is the cross-delta memory, so a signature
/// split across deltas — even character-by-character — is detected identically.
/// A leaked block of any length is handled without a window that the opening
/// <c>&lt;invoke</c> could scroll out of.</para>
/// <para><b>True failure transitions, not naive reset.</b> Each fixed token is
/// matched with a KMP automaton whose mismatch retries the same character from
/// the failure state, so an overlapping restart like <c>&lt;&lt;invoke</c> is
/// not dropped (a naive reset-to-0 would miss the valid second <c>&lt;</c>).</para>
/// <para><b>State is per-block.</b> <see cref="Reset"/> at each
/// <c>content_block_start</c>; a signature is never assembled across a block
/// boundary.</para>
/// <para>Not thread-safe; one instance per content block per request.</para>
/// </remarks>
internal sealed class ToolLeakAutomaton
{
    // Fixed tokens (fence is handled separately as a backtick run).
    private const string InvokeOpen = "<invoke name=\"";
    private const string ParamOpen = "<parameter";
    private const string ParamClose = "</parameter>";
    private const string InvokeClose = "</invoke>";

    // Upper bound on a captured tool name. Beyond this we abandon the current
    // invoke (fail-open): a real tool name is short; an unterminated capture is
    // not a valid call. Prevents an unbounded name buffer.
    private const int MaxNameLength = 128;

    private readonly HashSet<string> _toolNames;

    private readonly KmpMatcher _invokeOpen = new(InvokeOpen);
    private readonly KmpMatcher _paramOpen = new(ParamOpen);
    private readonly KmpMatcher _paramClose = new(ParamClose);
    private readonly KmpMatcher _invokeClose = new(InvokeClose);

    private int _backtickRun;       // consecutive backticks; a run of >=3 toggles the fence
    private bool _inFence;
    private bool _trackFences = true; // false for thinking blocks (no fence concept)
    private bool _invokeOpen_;      // inside an <invoke …> whose close we await
    private bool _capturingName;    // between <invoke name=" and the closing "
    private readonly System.Text.StringBuilder _name = new(MaxNameLength);
    private string? _pendingName;   // captured tool name for the open invoke
    private int _paramOpenCount;
    private int _paramCloseCount;
    private bool _tripped;          // latched once a leak is confirmed
    private string? _matchedToolName; // the tool name that tripped the block

    public ToolLeakAutomaton(IEnumerable<string> toolNames)
    {
        _toolNames = new HashSet<string>(toolNames, System.StringComparer.Ordinal);
    }

    /// <summary>True once a leak has been confirmed in this block. Latches.</summary>
    public bool Tripped => _tripped;

    /// <summary>
    /// The tool name that tripped the block (from the leaked <c>&lt;invoke name="…"&gt;</c>),
    /// or null until a leak is confirmed. Latches with <see cref="Tripped"/>.
    /// </summary>
    public string? MatchedToolName => _matchedToolName;

    /// <summary>
    /// Reset all state for a new content block.
    /// <paramref name="trackFences"/> selects whether ``` runs toggle a code
    /// fence: true for <c>text</c> blocks (where a fenced example is teaching,
    /// not a leak), false for <c>thinking</c> blocks (which have no fence concept
    /// and are treated as always-unfenced, per the guard's contract).
    /// </summary>
    public void Reset(bool trackFences = true)
    {
        _invokeOpen.Reset(); _paramOpen.Reset();
        _paramClose.Reset(); _invokeClose.Reset();
        _backtickRun = 0;
        _inFence = false;
        _trackFences = trackFences;
        AbandonInvoke();
        _tripped = false;
        _matchedToolName = null;
    }

    /// <summary>
    /// Feed one character. Returns true the instant a leak is confirmed (also
    /// latched in <see cref="Tripped"/>). Once tripped, further feeds are no-ops.
    /// </summary>
    public bool Feed(char c)
    {
        if (_tripped) return true;

        // Fence toggling: a run of >=3 backticks flips in/out of a code fence.
        // Toggling at exactly the 3rd backtick makes a longer run (4+, or the
        // closing ``` of the same line) toggle only once, not once-per-3.
        // A genuine leak is bare (never fenced), so an invoke closed while
        // _inFence is ignored. Skipped entirely when fences aren't tracked
        // (thinking blocks), so _inFence stays false = always unfenced.
        if (_trackFences)
        {
            if (c == '`')
            {
                _backtickRun++;
                if (_backtickRun == 3) _inFence = !_inFence;
            }
            else
            {
                _backtickRun = 0;
            }
        }

        // Name capture: characters between <invoke name=" and the closing ".
        if (_capturingName)
        {
            if (c == '"')
            {
                _capturingName = false;
                _pendingName = _name.ToString();
                _name.Clear();
            }
            else if (_name.Length >= MaxNameLength)
            {
                AbandonInvoke(); // runaway name → fail-open
            }
            else
            {
                _name.Append(c);
            }
        }

        // <invoke name=" — begin a new call scope and start capturing the name.
        if (_invokeOpen.Feed(c))
        {
            _invokeOpen_ = true;
            _capturingName = true;
            _pendingName = null;
            _name.Clear();
            _paramOpenCount = 0;
            _paramCloseCount = 0;
        }

        if (!_invokeOpen_)
        {
            return false; // nothing else matters until an invoke is open
        }

        // <parameter … and </parameter> counting.
        if (_paramOpen.Feed(c)) _paramOpenCount++;
        if (_paramClose.Feed(c)) _paramCloseCount++;

        // </invoke> — evaluate the close condition.
        if (_invokeClose.Feed(c))
        {
            var name = _pendingName;
            var closed =
                !_inFence
                && name is not null
                && _paramCloseCount >= 1
                && _paramOpenCount == _paramCloseCount
                && _toolNames.Contains(name);

            if (closed)
            {
                _tripped = true;
                _matchedToolName = name;
                return true;
            }

            // A </invoke> that didn't satisfy the condition ends this invoke
            // scope; a later well-formed invoke in the same block can still trip.
            AbandonInvoke();
        }

        return false;
    }

    private void AbandonInvoke()
    {
        _invokeOpen_ = false;
        _capturingName = false;
        _name.Clear();
        _pendingName = null;
        _paramOpenCount = 0;
        _paramCloseCount = 0;
    }
}

/// <summary>
/// Minimal KMP single-pattern matcher fed one character at a time. On a mismatch
/// it follows the failure function so an overlapping restart is not lost (e.g.
/// <c>&lt;&lt;</c> against a pattern starting with <c>&lt;</c> keeps the second
/// <c>&lt;</c> as a length-1 match rather than resetting to 0).
/// </summary>
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
