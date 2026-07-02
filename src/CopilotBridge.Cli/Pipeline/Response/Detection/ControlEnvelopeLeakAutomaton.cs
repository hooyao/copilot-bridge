namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Single-pass streaming detector for a <b>Claude Code control-envelope leak</b>:
/// a Copilot-served model emitting one of Claude Code's protocol/control XML
/// wrappers as literal text inside a <c>text</c>/<c>thinking</c> content block,
/// where the wrapper is CLOSED, shape-valid, and not inside a markdown code fence.
/// These envelopes are injected into the transcript by Claude Code itself (task
/// results, teammate/channel/cross-session messages, keepalive ticks) — the model
/// echoing one back as its own output is the same class of failure as the
/// <see cref="ToolLeakAutomaton"/>'s leaked <c>&lt;invoke&gt;</c>, so it forces the
/// same clean client retry.
/// </summary>
/// <remarks>
/// <para><b>Character-fed, O(1) state, retains no content.</b> Like
/// <see cref="ToolLeakAutomaton"/>, each character is fed exactly once via
/// <see cref="Feed(char)"/>; the machine's state (not a text buffer) is the
/// cross-delta memory, so a signature split across deltas — even
/// character-by-character — is detected identically, and an arbitrarily long
/// leaked block is handled without a window the opening tag could scroll out of.
/// The only buffers are small, bounded per-envelope counters (attribute-value
/// length, tick inner length) that fail open when exceeded.</para>
/// <para><b>True failure transitions, not naive reset.</b> Each fixed token is
/// matched with a <see cref="KmpMatcher"/> whose mismatch retries the same
/// character from the failure state, so an overlapping restart like
/// <c>&lt;&lt;task-notification</c> is not dropped.</para>
/// <para><b>Shape-based, closed-envelope proof.</b> Each envelope must be fully
/// closed with its shape-specific proof (a required child or attribute) before it
/// trips, so prose that merely mentions a tag name is not a leak:</para>
/// <list type="bullet">
/// <item><c>task-notification</c>: closed <c>&lt;task-id&gt;</c> AND at least one
/// closed <c>&lt;summary&gt;</c>/<c>&lt;status&gt;</c>/<c>&lt;output-file&gt;</c>.</item>
/// <item><c>teammate-message</c>: non-empty <c>teammate_id="…"</c> before the
/// opening <c>&gt;</c>, then a matching close.</item>
/// <item><c>channel</c>: non-empty <c>source="…"</c> before the opening
/// <c>&gt;</c>, then a matching close. The distinct <c>&lt;channel-message&gt;</c>
/// wrapper never trips this (its close is <c>&lt;/channel-message&gt;</c>).</item>
/// <item><c>cross-session-message</c>: non-empty <c>from="…"</c> before the
/// opening <c>&gt;</c>, then a matching close.</item>
/// <item><c>tick</c>: closed <c>&lt;tick&gt;…&lt;/tick&gt;</c> with non-empty inner
/// text.</item>
/// </list>
/// <para><b>State is per-block.</b> <see cref="Reset"/> at each
/// <c>content_block_start</c>; a signature is never assembled across a block
/// boundary. <paramref name="trackFences"/> semantics match
/// <see cref="ToolLeakAutomaton"/>: text blocks track ``` fences (a fenced example
/// is not a leak); thinking blocks have no fence concept and are always-unfenced.</para>
/// <para>Not thread-safe; one instance per content block per request.</para>
/// </remarks>
internal sealed class ControlEnvelopeLeakAutomaton
{
    private readonly IEnvelopeMatcher[] _matchers;

    private int _backtickRun;         // consecutive backticks; a run of >=3 toggles the fence
    private bool _inFence;
    private bool _trackFences = true; // false for thinking blocks (no fence concept)
    private bool _tripped;            // latched once a leak is confirmed
    private string? _matchedSubject;  // the envelope subject that tripped the block

    public ControlEnvelopeLeakAutomaton()
    {
        _matchers = new IEnvelopeMatcher[]
        {
            new TaskNotificationMatcher(),
            new AttributeEnvelopeMatcher(
                openPrefix: "<teammate-message", attribute: "teammate_id",
                closeTag: "</teammate-message>", subject: "teammate-message"),
            new AttributeEnvelopeMatcher(
                openPrefix: "<channel", attribute: "source",
                closeTag: "</channel>", subject: "channel"),
            new AttributeEnvelopeMatcher(
                openPrefix: "<cross-session-message", attribute: "from",
                closeTag: "</cross-session-message>", subject: "cross-session-message"),
            new TickMatcher(),
        };
    }

    /// <summary>True once a leak has been confirmed in this block. Latches.</summary>
    public bool Tripped => _tripped;

    /// <summary>
    /// The envelope subject that tripped the block (e.g. <c>task-notification</c>),
    /// or null until a leak is confirmed. Latches with <see cref="Tripped"/>.
    /// </summary>
    public string? MatchedSubject => _matchedSubject;

    /// <summary>
    /// Reset all state for a new content block. <paramref name="trackFences"/>
    /// selects whether ``` runs toggle a code fence: true for <c>text</c> blocks
    /// (a fenced example is not a leak), false for <c>thinking</c> blocks (no fence
    /// concept, treated as always-unfenced).
    /// </summary>
    public void Reset(bool trackFences = true)
    {
        foreach (var m in _matchers)
        {
            m.Reset();
        }
        _backtickRun = 0;
        _inFence = false;
        _trackFences = trackFences;
        _tripped = false;
        _matchedSubject = null;
    }

    /// <summary>
    /// Feed one character. Returns true the instant a control-envelope leak is
    /// confirmed (also latched in <see cref="Tripped"/>). Once tripped, further
    /// feeds are no-ops.
    /// </summary>
    public bool Feed(char c)
    {
        if (_tripped) return true;

        // Fence toggling identical to ToolLeakAutomaton: a run of >=3 backticks
        // flips in/out of a code fence. A genuine leak is bare (never fenced), so
        // an envelope closed while _inFence is ignored. Skipped when fences aren't
        // tracked (thinking blocks) → _inFence stays false = always unfenced.
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

        // Feed EVERY matcher (each keeps its own independent state); the first that
        // reports a closed, shape-valid envelope on this char names the subject.
        string? subject = null;
        foreach (var m in _matchers)
        {
            if (m.Feed(c) && subject is null)
            {
                subject = m.Subject;
            }
        }

        // A closed envelope inside a fence is a teaching example, not a leak.
        if (subject is not null && !_inFence)
        {
            _tripped = true;
            _matchedSubject = subject;
            return true;
        }

        return false;
    }

    /// <summary>
    /// One per-envelope sub-matcher. Fed the same character stream as its siblings;
    /// returns true on the character that completes a closed, shape-valid envelope.
    /// Fence gating is the parent's concern — a matcher reports pure shape.
    /// </summary>
    private interface IEnvelopeMatcher
    {
        string Subject { get; }
        void Reset();
        bool Feed(char c);
    }

    /// <summary>
    /// Matches <c>&lt;task-notification&gt;…&lt;/task-notification&gt;</c> only when
    /// the body contains a closed <c>&lt;task-id&gt;</c> child AND at least one
    /// closed proof child (<c>&lt;summary&gt;</c>, <c>&lt;status&gt;</c>, or
    /// <c>&lt;output-file&gt;</c>). Child tracking runs only while the envelope is
    /// open, so children outside it never count.
    /// </summary>
    private sealed class TaskNotificationMatcher : IEnvelopeMatcher
    {
        private readonly KmpMatcher _open = new("<task-notification>");
        private readonly KmpMatcher _close = new("</task-notification>");

        // Required child: <task-id>…</task-id>.
        private readonly KmpMatcher _taskIdOpen = new("<task-id>");
        private readonly KmpMatcher _taskIdClose = new("</task-id>");

        // Proof children (any one closed satisfies the shape).
        private readonly KmpMatcher _summaryOpen = new("<summary>");
        private readonly KmpMatcher _summaryClose = new("</summary>");
        private readonly KmpMatcher _statusOpen = new("<status>");
        private readonly KmpMatcher _statusClose = new("</status>");
        private readonly KmpMatcher _outputOpen = new("<output-file>");
        private readonly KmpMatcher _outputClose = new("</output-file>");

        private bool _opened;
        private bool _taskIdOpened, _hasTaskId;
        private bool _summaryOpened, _statusOpened, _outputOpened, _hasProof;

        public string Subject => "task-notification";

        public void Reset()
        {
            _open.Reset(); _close.Reset();
            ResetChildren();
            _opened = false;
        }

        private void ResetChildren()
        {
            _taskIdOpen.Reset(); _taskIdClose.Reset();
            _summaryOpen.Reset(); _summaryClose.Reset();
            _statusOpen.Reset(); _statusClose.Reset();
            _outputOpen.Reset(); _outputClose.Reset();
            _taskIdOpened = _hasTaskId = false;
            _summaryOpened = _statusOpened = _outputOpened = _hasProof = false;
        }

        public bool Feed(char c)
        {
            // (Re)open on the literal opening tag; the closing tag never matches the
            // opening pattern (it starts with "</"), so a close can't re-open.
            if (_open.Feed(c))
            {
                _opened = true;
                ResetChildren();
                _close.Reset();
                return false;
            }

            if (!_opened) return false;

            // Track closed children (gated to the open envelope).
            if (_taskIdOpen.Feed(c)) _taskIdOpened = true;
            if (_taskIdClose.Feed(c) && _taskIdOpened) _hasTaskId = true;

            if (_summaryOpen.Feed(c)) _summaryOpened = true;
            if (_summaryClose.Feed(c) && _summaryOpened) _hasProof = true;
            if (_statusOpen.Feed(c)) _statusOpened = true;
            if (_statusClose.Feed(c) && _statusOpened) _hasProof = true;
            if (_outputOpen.Feed(c)) _outputOpened = true;
            if (_outputClose.Feed(c) && _outputOpened) _hasProof = true;

            if (_close.Feed(c))
            {
                var ok = _hasTaskId && _hasProof;
                _opened = false; // end this envelope; a later one can still trip
                return ok;
            }

            return false;
        }
    }

    /// <summary>
    /// Matches an attribute-bearing envelope: an opening prefix
    /// (<c>&lt;teammate-message</c>, <c>&lt;channel</c>, …) carrying a required
    /// non-empty attribute (<c>teammate_id="…"</c>, <c>source="…"</c>, …) before
    /// its opening <c>&gt;</c>, followed by the matching close tag. The close tag is
    /// exact, so a longer sibling tag with the same prefix (e.g.
    /// <c>&lt;channel-message&gt;…&lt;/channel-message&gt;</c> against the
    /// <c>&lt;channel&gt;</c> matcher) never trips.
    /// </summary>
    private sealed class AttributeEnvelopeMatcher : IEnvelopeMatcher
    {
        // Upper bound on a captured attribute value; beyond it we fail open
        // (abandon the envelope) rather than buffer without bound.
        private const int MaxValueLength = 256;

        private readonly KmpMatcher _open;
        private readonly KmpMatcher _attr;   // "<name>=\""
        private readonly KmpMatcher _close;

        private bool _inOpenTag;   // between the opening prefix and its '>'
        private bool _opened;      // opening tag done, body open, awaiting close
        private bool _capturingVal;
        private int _valLen;
        private bool _hasAttr;

        public AttributeEnvelopeMatcher(string openPrefix, string attribute, string closeTag, string subject)
        {
            _open = new KmpMatcher(openPrefix);
            _attr = new KmpMatcher(attribute + "=\"");
            _close = new KmpMatcher(closeTag);
            Subject = subject;
        }

        public string Subject { get; }

        public void Reset()
        {
            _open.Reset(); _attr.Reset(); _close.Reset();
            _inOpenTag = false;
            _opened = false;
            _capturingVal = false;
            _valLen = 0;
            _hasAttr = false;
        }

        public bool Feed(char c)
        {
            // (Re)start the opening tag whenever the prefix matches. The close tag
            // starts with "</" so it can never re-trigger the opening prefix.
            if (_open.Feed(c))
            {
                _inOpenTag = true;
                _opened = false;
                _capturingVal = false;
                _valLen = 0;
                _hasAttr = false;
                _attr.Reset();
                _close.Reset();
                return false;
            }

            if (_inOpenTag)
            {
                if (_capturingVal)
                {
                    if (c == '"')
                    {
                        // A non-empty value satisfies the required attribute.
                        _capturingVal = false;
                        if (_valLen >= 1) _hasAttr = true;
                    }
                    else if (_valLen >= MaxValueLength)
                    {
                        Abandon(); // runaway value → fail open
                    }
                    else
                    {
                        _valLen++;
                    }
                }
                else if (_attr.Feed(c))
                {
                    _capturingVal = true;
                    _valLen = 0;
                }
                else if (c == '>')
                {
                    // Opening tag closed: enter the body only if the required
                    // attribute was present, else abandon back to scanning.
                    _inOpenTag = false;
                    if (_hasAttr)
                    {
                        _opened = true;
                        _close.Reset();
                    }
                    else
                    {
                        Abandon();
                    }
                }
                return false;
            }

            if (_opened && _close.Feed(c))
            {
                _opened = false; // end this envelope; a later one can still trip
                return true;
            }

            return false;
        }

        private void Abandon()
        {
            _inOpenTag = false;
            _opened = false;
            _capturingVal = false;
            _valLen = 0;
            _hasAttr = false;
        }
    }

    /// <summary>
    /// Matches <c>&lt;tick&gt;…&lt;/tick&gt;</c> with non-empty inner text. Inner
    /// length is tracked as a small bounded counter (chars since the opening tag,
    /// minus the closing tag's length), so an empty <c>&lt;tick&gt;&lt;/tick&gt;</c>
    /// does not trip and the counter never grows without bound.
    /// </summary>
    private sealed class TickMatcher : IEnvelopeMatcher
    {
        private const int CloseLength = 7; // "</tick>".Length

        private readonly KmpMatcher _open = new("<tick>");
        private readonly KmpMatcher _close = new("</tick>");

        private bool _opened;
        private int _sinceOpen; // capped at CloseLength + 1; > CloseLength ⇒ non-empty inner

        public string Subject => "tick";

        public void Reset()
        {
            _open.Reset(); _close.Reset();
            _opened = false;
            _sinceOpen = 0;
        }

        public bool Feed(char c)
        {
            if (_open.Feed(c))
            {
                _opened = true;
                _sinceOpen = 0;
                _close.Reset();
                return false; // the opening '>' is not inner content
            }

            if (!_opened) return false;

            if (_sinceOpen <= CloseLength) _sinceOpen++;

            if (_close.Feed(c))
            {
                var nonEmpty = _sinceOpen > CloseLength;
                _opened = false; // end this envelope; a later one can still trip
                return nonEmpty;
            }

            return false;
        }
    }
}
