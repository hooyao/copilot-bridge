## Why

A production native-Codex trace shows an automatic pre-turn compaction request reaching Copilot only after the retained history can no longer be admitted. Copilot returns its confirmed HTTP 400 `invalid_request_body` context-window error, but the bridge relays that shape unchanged and Codex classifies it as a generic bad request instead of the recoverable `ContextWindowExceeded` condition, leaving the thread unable to compact or continue.

## What Changes

- Lower the bridge-projected Codex model-catalog auto-compaction policy from at most 90% to at most 85% of total context, while retaining the independent prompt-ceiling guard and fail-closed live-limit validation.
- Adapt only the exact confirmed Copilot native `/codex` context-window 400 into the Codex-native failed-stream condition the supported client recognizes as `ContextWindowExceeded`.
- Preserve the original upstream status, headers, and body in raw tracing while recording the adapted client-facing response separately.
- Keep every near-miss error, `/cc` translation, non-Responses route, malformed/oversized body, and client cancellation behavior isolated.
- Add contract-first, captured-byte, and real-client compact/retry verification, including Codex's own structured log as part of the verdict.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-model-catalog`: Change the projected automatic-compaction safety policy to no more than 85% of total context.
- `codex-response-fidelity`: Add a narrowly classified native Codex context-error adaptation and require real-client proof that compaction recovery proceeds.

## Impact

- Affects Codex catalog projection, native `/codex/responses` buffered-error handling, downstream audit representation, and Codex ClientBehavior/ApiContract coverage.
- Does not change Copilot request bytes, model routing, Claude Code error translation, authentication, user-authored explicit context overrides, or unrelated upstream 400 responses.
- Requires documentation updates for the new catalog threshold and native Codex recovery boundary.
