## Context

Claude Code injects several control/event XML envelopes into the model's context
that are not ordinary user text. Source research (`claude-code-sourcemap`,
`restored-src/src/constants/xml.ts` and the render components) fixes their shapes:

- `<task-notification>` (worker/agent results) with children `<task-id>`,
  optional `<tool-use-id>`/`<task-type>`/`<output-file>`, `<status>`, `<summary>`.
  It arrives wrapped in prose (`A background agent completed a task:\n<task-…>`),
  so the XML is NOT necessarily at the block start — detection must scan anywhere.
- `<teammate-message teammate_id="…" [color] [summary]>` — parser requires
  `teammate_id`.
- `<channel source="…" [user] [chat_id]>` — the *rendered* wrapper is `<channel>`,
  NOT `<channel-message>` (a different tag). Parser requires `source`.
- `<cross-session-message from="…">`.
- `<tick>time</tick>` — a proactive keepalive, rendered/hidden specially.

## Goals / Non-Goals

- **Goal**: detect a CLOSED, shape-valid, unfenced occurrence of any of the five
  envelopes as literal assistant text and force the same clean retry as the
  `<invoke>` guard, reusing its config, delivery modes, signal, and logging.
- **Non-Goal**: matching request-history fingerprints (the user's example may not
  match live history; shape-based closed-envelope detection is the safer minimum).
- **Non-Goal**: blocking transcript/display wrappers (`<bash-*>`,
  `<local-command-*>`, `<command-message>`, `<fork-boilerplate>`,
  `<remote-review*>`) — they legitimately appear in explanations.

## Decisions

### One automaton, several sub-matchers
`ControlEnvelopeLeakAutomaton` owns the shared concerns (fence tracking, the
tripped latch, `MatchedSubject`) and dispatches each character to five
independent per-envelope sub-matchers. This mirrors `ToolLeakAutomaton`'s
character-fed, O(1)-state, retain-no-content discipline so a signature split
across deltas — even char-by-char — is detected identically, and an arbitrarily
long block needs no window.

- **KMP for every fixed token** (open tag, close tag, child tags, `attr="`), so an
  overlapping restart like `<<task-notification` is never dropped.
- **Shape proof before trip**: each envelope must be fully closed with its
  required child(ren)/attribute before it trips, so prose that merely names a tag
  is not a leak. `task-notification` additionally requires a closed `<task-id>`
  AND one closed proof child (`<summary>`/`<status>`/`<output-file>`) — none of
  the optional children is universal enough to require alone.
- **`<channel>` vs `<channel-message>`**: the matcher's close tag is the exact
  `</channel>`; `</channel-message>` never completes it, so the sibling wrapper
  cannot trip the channel matcher even though its open prefix shares `<channel`.
- **Bounded fail-open**: the only buffers are small bounded counters (attribute
  value length ≤ 256, tick inner length capped just past the close-tag length);
  a runaway attribute value abandons the envelope rather than buffering unbounded.
- **Fence gating is the parent's job**: sub-matchers report pure shape; the parent
  ignores a close that lands inside a ``` fence (text blocks). Thinking blocks are
  always-unfenced (`trackFences:false`), matching the `<invoke>` guard.

### Reuse the existing detector, config, and retry
`ToolLeakDetector` holds both automata, resets both at `content_block_start`
(with the same `trackFences` selection), and feeds each scanned character to
both; either trip returns the existing `Abort` action. No new option is
introduced — `Pipeline:Detectors:ToolLeakGuard` governs both. `ScanThinking`
and the disabled-guard no-op apply unchanged.

### Broaden the log subject, keep the contract
The detection-point Warning changes its field from `tool={Tool}` to
`subject={Subject}` (the tool name for an `<invoke>` leak, or the envelope
subject like `task-notification`). Exactly one Warning per leak and "never log
the leaked content" are preserved.

## Risks / Trade-offs

- **False positives**: an assistant that reproduces a *complete, closed, unfenced*
  control envelope verbatim would trip. This is rare (fenced examples are
  excluded) and, like the `<invoke>` guard, a retry is a safe response.
- **`task-notification` shape drift**: children are matched by tag; if Claude Code
  renames a child the proof requirement may miss. Acceptable — re-derive from the
  sourcemap if the shape changes (same discipline as model profiles).
