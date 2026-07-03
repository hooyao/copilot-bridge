using System.Collections.Generic;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Single-pass streaming detector for a <b>response leak</b>: a Copilot-served
/// Claude model emitting structured protocol markup as literal text inside a
/// <c>text</c>/<c>thinking</c> content block, where the markup is CLOSED,
/// shape-valid, and not inside a markdown code fence. Two families are detected:
/// <list type="bullet">
/// <item>a leaked tool call — <c>&lt;invoke name="X"&gt;…&lt;parameter…&gt;…
/// &lt;/parameter&gt;…&lt;/invoke&gt;</c> where <c>X</c> is a real tool from the
/// request (instead of a proper <c>tool_use</c> block);</item>
/// <item>a leaked Claude Code control/event envelope
/// (<c>&lt;task-notification&gt;</c>, <c>&lt;teammate-message&gt;</c>,
/// <c>&lt;channel&gt;</c>, <c>&lt;cross-session-message&gt;</c>,
/// <c>&lt;tick&gt;</c>) echoed back as the model's own output (instead of
/// remaining injected context).</item>
/// </list>
/// Both force the same clean client retry.
/// </summary>
/// <remarks>
/// <para><b>One mechanism, several matchers.</b> The automaton owns the shared
/// concerns exactly once — code-fence tracking, the tripped latch, and the matched
/// subject — and dispatches each character to a list of <see cref="ILeakMatcher"/>
/// (one for <c>&lt;invoke&gt;</c>, one per control envelope). Every matcher keeps
/// its own O(1) state and is fed the same character stream; the first to report a
/// closed, shape-valid signature on a character names the leaked subject. A trip is
/// gated on <c>!inFence</c>, so a fenced teaching example is never a leak.</para>
/// <para><b>Character-fed, O(1) state, retains no content.</b> Each character of
/// streamed block text is fed exactly once via <see cref="Feed(char)"/>; the
/// machine's state (not a text buffer) is the cross-delta memory, so a signature
/// split across deltas — even character-by-character — is detected identically,
/// and an arbitrarily long leaked block is handled without a window the opening
/// tag could scroll out of. The only per-matcher buffers are small bounded
/// counters/captures that fail open when exceeded.</para>
/// <para><b>True failure transitions, not naive reset.</b> Each fixed token is
/// matched with a <see cref="KmpMatcher"/> whose mismatch retries the same
/// character from the failure state, so an overlapping restart like
/// <c>&lt;&lt;invoke</c> or <c>&lt;&lt;task-notification</c> is not dropped.</para>
/// <para><b>State is per-block.</b> <see cref="Reset"/> at each
/// <c>content_block_start</c>; a signature is never assembled across a block
/// boundary. <paramref name="trackFences"/> semantics: <c>text</c> blocks track
/// ``` fences (a fenced example is not a leak); <c>thinking</c> blocks have no
/// fence concept and are always-unfenced.</para>
/// <para>Not thread-safe; one instance per content block per request.</para>
/// </remarks>
internal sealed class ResponseLeakAutomaton
{
    private readonly ILeakMatcher[] _matchers;

    private int _backtickRun;         // consecutive backticks; length decided at run end
    private int _fenceOpenLen;        // backtick length that opened the current code region
    private bool _inFence;
    private bool _trackFences = true; // false for thinking blocks (no fence concept)
    private bool _tripped;            // latched once a leak is confirmed
    private string? _matchedSubject;  // the subject (tool name or envelope) that tripped
    private string? _matchedSignature; // the stable signature id that tripped (kebab; LeakSignatures)

    /// <summary>
    /// Build the automaton. <paramref name="toolNames"/> seeds the
    /// <c>&lt;invoke&gt;</c> matcher's allow-list (a leak only trips on a tool the
    /// request actually offered). <paramref name="enabledSignatures"/> gates which
    /// signatures are watched at all: null (default) enables every signature; a set
    /// enables only the ids it contains (see <see cref="LeakSignatures"/>). A
    /// disabled signature's matcher is never built, so it costs nothing and can
    /// never trip — letting a caller clear a false positive on one shape without
    /// weakening the others.
    /// </summary>
    public ResponseLeakAutomaton(
        IEnumerable<string>? toolNames = null,
        IReadOnlySet<string>? enabledSignatures = null)
    {
        var names = toolNames ?? System.Array.Empty<string>();

        // Build only the matchers whose signature is enabled (null = all). Omitting
        // a disabled matcher — rather than building it and filtering its result —
        // is zero-cost and, crucially, keeps a disabled signature from matching
        // first on a character and masking an enabled one (Feed reports the FIRST
        // matcher to match).
        var matchers = new List<ILeakMatcher>(LeakSignatures.All.Length);
        void AddIfEnabled(string signature, System.Func<ILeakMatcher> create)
        {
            if (enabledSignatures is null || enabledSignatures.Contains(signature))
            {
                matchers.Add(create());
            }
        }

        AddIfEnabled(LeakSignatures.Invoke, () => new InvokeMatcher(names));
        AddIfEnabled(LeakSignatures.TaskNotification, () => new TaskNotificationMatcher());
        AddIfEnabled(LeakSignatures.TeammateMessage, () => new AttributeEnvelopeMatcher(
            openPrefix: "<teammate-message", attribute: "teammate_id",
            closeTag: "</teammate-message>", subject: LeakSignatures.TeammateMessage));
        AddIfEnabled(LeakSignatures.Channel, () => new AttributeEnvelopeMatcher(
            openPrefix: "<channel", attribute: "source",
            closeTag: "</channel>", subject: LeakSignatures.Channel));
        AddIfEnabled(LeakSignatures.CrossSessionMessage, () => new AttributeEnvelopeMatcher(
            openPrefix: "<cross-session-message", attribute: "from",
            closeTag: "</cross-session-message>", subject: LeakSignatures.CrossSessionMessage));
        AddIfEnabled(LeakSignatures.Tick, () => new TickMatcher());

        _matchers = matchers.ToArray();
    }

    /// <summary>The signature ids this automaton actually built a matcher for.
    /// Exposed for a test that guards against drift between
    /// <see cref="LeakSignatures.All"/> and the matcher factory list above — a new
    /// id added to <see cref="LeakSignatures"/> without a matcher would otherwise be
    /// silently unwatched.</summary>
    internal IReadOnlyList<string> BuiltSignatures =>
        System.Array.ConvertAll(_matchers, m => m.Signature);

    /// <summary>True once a leak has been confirmed in this block. Latches.</summary>
    public bool Tripped => _tripped;

    /// <summary>
    /// The subject that tripped the block — the tool name for an <c>&lt;invoke&gt;</c>
    /// leak, or the control-envelope subject (e.g. <c>task-notification</c>) — or
    /// null until a leak is confirmed. Latches with <see cref="Tripped"/>.
    /// </summary>
    public string? MatchedSubject => _matchedSubject;

    /// <summary>
    /// The stable signature id (kebab-case, from <see cref="LeakSignatures"/>) that
    /// tripped the block — e.g. <c>invoke</c> or <c>task-notification</c> — or null
    /// until a leak is confirmed. Distinct from <see cref="MatchedSubject"/>: for an
    /// <c>&lt;invoke&gt;</c> leak the subject is the captured tool name, whereas this
    /// is always <c>invoke</c> — the identity used to name the disable switch.
    /// Latches with <see cref="Tripped"/>.
    /// </summary>
    public string? MatchedSignature => _matchedSignature;

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
        _fenceOpenLen = 0;
        _inFence = false;
        _trackFences = trackFences;
        _tripped = false;
        _matchedSubject = null;
        _matchedSignature = null;
    }

    /// <summary>
    /// Decide what a just-ended backtick run does to the code region. A run OPENS a
    /// region (recording its length) when none is open; when a region is open it
    /// CLOSES only if this run is at least as long as the opener — so a shorter
    /// nested run (``` inside ````) cannot prematurely close the outer fence. A
    /// run of 1–2 backticks opens a markdown inline span (abandoned on the next
    /// newline; see <see cref="Feed"/>), a run of 3+ opens a block fence that
    /// persists across newlines.
    /// </summary>
    private void FinalizeBacktickRun()
    {
        if (!_inFence)
        {
            _inFence = true;
            _fenceOpenLen = _backtickRun;
        }
        else if (_backtickRun >= _fenceOpenLen)
        {
            _inFence = false;
            _fenceOpenLen = 0;
        }
        _backtickRun = 0;
    }

    /// <summary>
    /// Feed one character. Returns true the instant a leak is confirmed (also
    /// latched in <see cref="Tripped"/>). Once tripped, further feeds are no-ops.
    /// </summary>
    public bool Feed(char c)
    {
        if (_tripped) return true;

        // Code-region tracking, run-length aware so a fenced teaching example is
        // never read as a leak. A backtick run is evaluated when it ENDS (the first
        // non-backtick char): it OPENS a region (remembering its length) when none
        // is open, and CLOSES the region only when this run is at least as long as
        // the one that opened it — so a ``` example nested inside a ```` fence does
        // not prematurely close it. A run of 1–2 backticks is a markdown inline
        // span; a newline abandons an unclosed inline span so a stray short backtick
        // cannot suppress the rest of the block (a block fence, length >=3, persists
        // across newlines). Skipped when fences aren't tracked (thinking blocks) →
        // _inFence stays false = always unfenced. Backticks are still fed to the
        // matchers below, harmlessly — no signature pattern contains a backtick.
        if (_trackFences)
        {
            if (c == '`')
            {
                _backtickRun++;
            }
            else
            {
                if (_backtickRun > 0)
                {
                    FinalizeBacktickRun();
                }
                if (c == '\n' && _inFence && _fenceOpenLen < 3)
                {
                    _inFence = false;
                    _fenceOpenLen = 0;
                }
            }
        }

        // Feed EVERY matcher (each keeps its own independent state); the first that
        // reports a closed, shape-valid signature on this char names the subject.
        string? subject = null;
        string? signature = null;
        foreach (var m in _matchers)
        {
            if (m.Feed(c) && subject is null)
            {
                subject = m.Subject;
                signature = m.Signature;
            }
        }

        // A closed signature inside a fence is a teaching example, not a leak.
        if (subject is not null && !_inFence)
        {
            _tripped = true;
            _matchedSubject = subject;
            _matchedSignature = signature;
            return true;
        }

        return false;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    /// <summary>
    /// One per-signature sub-matcher. Fed the same character stream as its
    /// siblings; returns true on the character that completes a closed, shape-valid
    /// signature. Fence gating is the parent's concern — a matcher reports pure
    /// shape. <see cref="Subject"/> is read only right after <see cref="Feed"/>
    /// returns true.
    /// </summary>
    private interface ILeakMatcher
    {
        /// <summary>The stable signature id (kebab; see <see cref="LeakSignatures"/>)
        /// this matcher detects. Constant per matcher, independent of the matched
        /// value — the <c>&lt;invoke&gt;</c> matcher's signature is always
        /// <c>invoke</c> even though its <see cref="Subject"/> is the tool name.</summary>
        string Signature { get; }

        string? Subject { get; }
        void Reset();
        bool Feed(char c);
    }

    /// <summary>
    /// Matches a leaked tool call: a CLOSED, balanced
    /// <c>&lt;invoke name="X"&gt;…&lt;/invoke&gt;</c> containing ≥1 closed
    /// <c>&lt;parameter&gt;</c>, where <c>X</c> is a tool present in the request's
    /// <c>tools[]</c>. Detection does NOT depend on any prefix token, the message
    /// <c>stop_reason</c>, or a bare unbalanced <c>&lt;invoke</c>. Moved verbatim
    /// from the former standalone response-leak automaton; fence gating and the tripped
    /// latch now live in the parent.
    /// </summary>
    private sealed class InvokeMatcher : ILeakMatcher
    {
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

        private bool _invokeOpen_;      // inside an <invoke …> whose close we await
        private bool _capturingName;    // between <invoke name=" and the closing "
        private readonly System.Text.StringBuilder _name = new(MaxNameLength);
        private string? _pendingName;   // captured tool name for the open invoke
        private int _paramOpenCount;
        private int _paramCloseCount;
        private string? _matched;       // tool name that satisfied the close condition

        public InvokeMatcher(IEnumerable<string> toolNames)
        {
            _toolNames = new HashSet<string>(toolNames, System.StringComparer.Ordinal);
        }

        public string Signature => LeakSignatures.Invoke;

        public string? Subject => _matched;

        public void Reset()
        {
            _invokeOpen.Reset(); _paramOpen.Reset();
            _paramClose.Reset(); _invokeClose.Reset();
            AbandonInvoke();
            _matched = null;
        }

        public bool Feed(char c)
        {
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

            // A <parameter> value is OPAQUE: while inside an unclosed
            // <parameter>…</parameter> the invoke-shaped markup a value may quote is
            // data, not a real call, so <invoke>/</invoke> effects are ignored there
            // (only </parameter> counts). This lets a genuine outer call whose
            // parameter text contains "<invoke name=…>" still balance and trip, while
            // a second <invoke …> OUTSIDE any parameter stays prose/quotation that
            // re-opens a fresh scope (unchanged behavior).
            var insideParameter = _paramOpenCount > _paramCloseCount;

            // <invoke name=" — begin a new call scope and start capturing the name.
            // Ignored inside a parameter value (opaque, see above).
            if (_invokeOpen.Feed(c) && !insideParameter)
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

            // <parameter … and </parameter> counting. </parameter> must still count
            // while inside the value (it is what ENDS the opaque region).
            if (_paramOpen.Feed(c)) _paramOpenCount++;
            if (_paramClose.Feed(c)) _paramCloseCount++;

            // </invoke> — evaluate the close condition. Ignored inside a parameter
            // value (opaque): a </invoke> quoted in parameter text belongs to the
            // data, not the outer call, so it must not prematurely close/abandon it.
            if (_invokeClose.Feed(c) && !insideParameter)
            {
                var name = _pendingName;
                var closed =
                    name is not null
                    && _paramCloseCount >= 1
                    && _paramOpenCount == _paramCloseCount
                    && _toolNames.Contains(name);

                if (closed)
                {
                    _matched = name;
                    _invokeOpen_ = false; // end this invoke; a later one can still trip
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
    /// Matches <c>&lt;task-notification&gt;…&lt;/task-notification&gt;</c> only when
    /// the body contains a closed <c>&lt;task-id&gt;</c> child AND at least one
    /// closed proof child (<c>&lt;summary&gt;</c>, <c>&lt;status&gt;</c>, or
    /// <c>&lt;output-file&gt;</c>). Child tracking runs only while the envelope is
    /// open, so children outside it never count.
    /// </summary>
    private sealed class TaskNotificationMatcher : ILeakMatcher
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

        public string Signature => LeakSignatures.TaskNotification;

        public string? Subject => LeakSignatures.TaskNotification;

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
    /// Matches an attribute-bearing envelope: an opening tag
    /// (<c>&lt;teammate-message</c>, <c>&lt;channel</c>, …) whose tag name is
    /// followed by whitespace and carries a required non-empty attribute
    /// (<c>teammate_id="…"</c>, <c>source="…"</c>, …), followed by the matching
    /// close tag.
    /// </summary>
    /// <remarks>
    /// Two boundaries make the match precise:
    /// <list type="bullet">
    /// <item><b>Tag-name boundary.</b> The character after the opening prefix must
    /// be whitespace, so a longer sibling tag with the same prefix — e.g.
    /// <c>&lt;channel-message&gt;</c> against the <c>&lt;channel&gt;</c> matcher —
    /// does NOT start (and therefore cannot leave the matcher in an "opened" state
    /// that a later stray <c>&lt;/channel&gt;</c> could falsely close).</item>
    /// <item><b>Attribute boundary.</b> The required attribute is matched only when
    /// it begins right after whitespace, so a different attribute that merely ends
    /// with the same name — e.g. <c>data-source="…"</c> containing <c>source="</c> —
    /// does NOT satisfy the required-attribute proof.</item>
    /// </list>
    /// The exact close tag distinguishes <c>&lt;channel&gt;</c> from
    /// <c>&lt;channel-message&gt;</c>. A runaway attribute value fails open
    /// (abandons) rather than buffering unbounded.
    /// </remarks>
    private sealed class AttributeEnvelopeMatcher : ILeakMatcher
    {
        // Upper bound on a captured attribute value; beyond it we fail open
        // (abandon the envelope) rather than buffer without bound.
        private const int MaxValueLength = 256;

        private readonly KmpMatcher _open;   // "<channel" (tag name only)
        private readonly string _attrTarget; // "source=\""
        private readonly KmpMatcher _close;

        private bool _sawPrefix;   // prefix just matched; the next char decides the tag-name boundary
        private bool _inOpenTag;   // confirmed real tag (delimiter seen), scanning attributes
        private bool _opened;      // opening tag closed with the attribute present; awaiting close tag
        private bool _attrActive;  // an attribute-name match attempt is anchored at the current boundary
        private int _ai;           // chars of _attrTarget matched in the active attempt
        private bool _capturingVal;
        private int _valLen;
        private bool _hasAttr;

        public AttributeEnvelopeMatcher(string openPrefix, string attribute, string closeTag, string subject)
        {
            _open = new KmpMatcher(openPrefix);
            _attrTarget = attribute + "=\"";
            _close = new KmpMatcher(closeTag);
            Subject = subject;
            Signature = subject;
        }

        public string Signature { get; }

        public string? Subject { get; }

        public void Reset()
        {
            _open.Reset(); _close.Reset();
            Abandon();
        }

        public bool Feed(char c)
        {
            // (Re)start the opening tag name whenever the prefix matches. The close
            // tag starts with "</" so it can never re-trigger the opening prefix.
            if (_open.Feed(c))
            {
                _sawPrefix = true;
                _inOpenTag = false;
                _opened = false;
                _attrActive = false;
                _ai = 0;
                _capturingVal = false;
                _valLen = 0;
                _hasAttr = false;
                _close.Reset();
                return false;
            }

            if (_sawPrefix)
            {
                // The character immediately after the tag name decides whether this
                // is the real envelope tag or a longer sibling (e.g. channel-message)
                // or an attribute-less tag. Only whitespace is a valid boundary.
                _sawPrefix = false;
                if (IsWhitespace(c))
                {
                    _inOpenTag = true;
                    _attrActive = true; // the next char begins the first attribute
                    _ai = 0;
                }
                else
                {
                    Abandon();
                }
                return false;
            }

            if (_inOpenTag)
            {
                if (_capturingVal)
                {
                    if (c == '"')
                    {
                        _capturingVal = false;
                        if (_valLen >= 1) _hasAttr = true; // non-empty value satisfies the attribute
                    }
                    else if (_valLen >= MaxValueLength)
                    {
                        Abandon(); // runaway value → fail open
                    }
                    else
                    {
                        _valLen++;
                    }
                    return false;
                }

                if (c == '>')
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
                    return false;
                }

                // Attribute-name matching, anchored to a whitespace boundary so a
                // substring inside another attribute name cannot satisfy it.
                if (_attrActive)
                {
                    if (c == _attrTarget[_ai])
                    {
                        _ai++;
                        if (_ai == _attrTarget.Length)
                        {
                            _capturingVal = true;
                            _valLen = 0;
                            _attrActive = false;
                        }
                        return false;
                    }
                    // Mismatch: abandon this attempt; re-arm only at the next boundary.
                    _attrActive = false;
                    _ai = 0;
                }

                if (IsWhitespace(c))
                {
                    _attrActive = true;
                    _ai = 0;
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
            _sawPrefix = false;
            _inOpenTag = false;
            _opened = false;
            _attrActive = false;
            _ai = 0;
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
    private sealed class TickMatcher : ILeakMatcher
    {
        private const int CloseLength = 7; // "</tick>".Length

        private readonly KmpMatcher _open = new("<tick>");
        private readonly KmpMatcher _close = new("</tick>");

        private bool _opened;
        private int _sinceOpen; // capped at CloseLength + 1; > CloseLength ⇒ non-empty inner

        public string Signature => LeakSignatures.Tick;

        public string? Subject => LeakSignatures.Tick;

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
