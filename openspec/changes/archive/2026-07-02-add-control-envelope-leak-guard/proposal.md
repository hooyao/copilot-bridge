## Why

The tool-leak guard already catches a Copilot-served Claude model echoing a
`<invoke name="X">…</invoke>` tool call as literal text and forces a clean retry.
The same class of failure applies to **Claude Code's own control/event XML
envelopes** — task results, teammate/channel/cross-session messages, and
keepalive ticks. Claude Code *injects* these envelopes into the transcript (they
are not normal user text); a model that echoes one back as its own output has
leaked a control message. Left in the transcript it can self-reinforce exactly
like the `<invoke>` leak, and the bridge is again the one wire where it is both
detectable (it sees every SSE event) and fixable without polluting history.

## What Changes

- Add a second per-block streaming automaton,
  `ControlEnvelopeLeakAutomaton`, alongside `ToolLeakAutomaton`, and feed both
  from the existing `ToolLeakDetector`. A leaked control envelope forces the same
  `Abort` → clean client retry as a leaked `<invoke>`.
- **Detection is structural / shape-based**, over a single `text`/`thinking`
  block, unfenced (text) or always-unfenced (thinking), reusing the same
  `Pipeline:Detectors:ToolLeakGuard` config (`Enabled`, `PreserveStream`,
  `Signal`, `ScanThinking`). Recognized envelopes:
  - `<task-notification>` — closed, with a closed `<task-id>` AND at least one of
    closed `<summary>`/`<status>`/`<output-file>`.
  - `<teammate-message teammate_id="…">…</teammate-message>` — closed, non-empty
    `teammate_id`.
  - `<channel source="…">…</channel>` — closed, non-empty `source`. The distinct
    `<channel-message>` wrapper is deliberately NOT matched.
  - `<cross-session-message from="…">…</cross-session-message>` — closed,
    non-empty `from`.
  - `<tick>…</tick>` — closed, non-empty inner text.
- **Broaden the detection-point Warning** from "leaked tool name" to "leaked
  subject" (tool name for `<invoke>`, or the control-envelope subject such as
  `task-notification`). Still exactly one Warning, still never the leaked content.
- Transcript/display wrappers (`<bash-input>`, `<local-command-stdout>`,
  `<command-message>`, …) are **out of scope** — they routinely appear in
  assistant explanations and would be false-positive-prone.

## Capabilities

### Modified Capabilities
- `tool-leak-guard`: extend the guard to also detect leaked Claude Code control
  envelopes (not only leaked `<invoke>` tool calls), sharing the same detection
  discipline, delivery modes, signal, and retry behaviour.
- `observability`: the detection-point Warning names the leaked *subject* (tool
  name or control-envelope subject), not only a tool name.

## Impact

- **New code**: `ControlEnvelopeLeakAutomaton.cs` under
  `Pipeline/Response/Detection/`. `ToolLeakDetector` holds and feeds both
  automata; the log template field changes from `tool=` to `subject=`.
- **No new config, no new DTOs**: reuses `ToolLeakGuardOptions` and the hand-built
  error JSON (AOT-safe, no `JsonContext` change).
- **Hot path**: one extra O(1) automaton per scannable block; no full-response
  buffering in the default (`PreserveStream=true`) mode; nothing when the guard is
  disabled.
- **Client contract**: unchanged — same `overloaded_error`/`api_error` retry the
  `<invoke>` guard already relies on.
