## Why

GitHub-Copilot-served Claude models intermittently leak a tool call as literal
`<invoke name="X"><parameter …>…</parameter></invoke>` XML inside a **text or
thinking** content block instead of emitting a proper `tool_use` block. Claude
Code renders it as text and executes nothing — the turn silently does the wrong
thing. Worse, the leaked text commits to the transcript and Claude Code replays
it every turn, so the model imitates its own bad format and the failure
self-reinforces. Fresh trace evidence puts this at ~2.2% of responses (5 / 229
in a 3-hour session), clustered — exactly the poisoning signature.

The bridge sits on the one wire where this is both **detectable** (it sees every
SSE event) and **fixable without polluting history** (it can make Claude Code
retry the turn cleanly before the dirty bytes ever commit).

## What Changes

- Add a **response pipeline stage** that detects the leak signature in the
  streaming (and, in buffer mode, the buffered) response and forces the client
  to retry the turn.
- **Detection is structural**: a single content block containing a *closed,
  balanced* `<invoke name="X">…</invoke>` with ≥1 closed `<parameter>`. It does
  **not** key off the drifting prefix token (observed `court`; memory recorded
  `call`), `stop_reason` (observed both `tool_use` and `end_turn`), or `tools[]`
  (useless in a repo that discusses tool-call syntax constantly). Closure
  balance is what separates a genuine leak from prose that merely quotes the
  syntax.
- **Two orthogonal knobs** govern the response:
  - `PreserveStream` (default `true`): keep streaming and inject the error
    mid-stream on detection (TTFT preserved; only the ~2.2% dirty turns pay) vs.
    buffer the whole response and emit a real HTTP status on a dirty turn
    (sacrifices streaming for **all** requests).
  - `Signal` (default `OverloadedError`): `OverloadedError` → Anthropic
    `overloaded_error` (buffer-mode HTTP 529), which Claude Code reliably
    retries and, after 3 consecutive, falls back opus→Sonnet — a fitting
    backstop for a poisoned session; or `ApiError` → generic `api_error`
    (buffer-mode HTTP 500), retried ~10× with no model fallback.
- New dedicated config section `Pipeline:ToolLeakGuard` (`Enabled` default on).
- Out of scope: the `feature/tool-call-repair` branch's JSON-repair stage
  targets a *different* failure (malformed JSON inside **real** `tool_use`
  blocks) and is not touched.

## Capabilities

### New Capabilities
- `tool-leak-guard`: Detect tool-call leakage into text/thinking blocks in the
  Anthropic `/cc/v1/messages` response and force a clean client retry, with
  configurable delivery (preserve-stream vs. buffer) and retry signal.

### Modified Capabilities
<!-- None — no existing capability spec changes its requirements. -->

## Impact

- **New code**: a response stage under `Pipeline/Response/`, a
  `ToolLeakGuardOptions` class under `Hosting/Options/`, DI registration, an
  `appsettings.json` section, and a `JsonContext` entry if the injected error
  body needs source-generated serialization.
- **Endpoint**: `ClaudeCodeMessagesEndpoint` gains a code path for buffer mode
  (`PreserveStream=false`), which drains the stream and flips
  `Mode`/`Status`/`BufferedBody`. The default stream path is a pure
  `EventStream` wrapper and does not touch the endpoint.
- **Hot path**: default config (`Enabled=true, PreserveStream=true`) adds one
  per-block accumulation buffer and a regex check per delta; no full-response
  buffering, no allocation when `Enabled=false`.
- **Client contract**: relies on Claude Code's documented mid-stream retry
  behavior for `overloaded_error` (verified against decompiled claude-code
  2.1.88). No change to the request wire or to non-Copilot-Anthropic routes.
