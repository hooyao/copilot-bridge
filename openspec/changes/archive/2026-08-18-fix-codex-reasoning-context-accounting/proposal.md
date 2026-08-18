## Why

Copilot Responses usage already charges replayed encrypted reasoning, but its HTTP response omits Codex's `X-Reasoning-Included` accounting signal. Codex therefore adds a second local estimate for historical reasoning and automatically compacts long `gpt-5.6-sol` threads when Copilot reports only about 0.5–0.7M active input tokens, despite the configured approximately 0.9M compaction threshold.

## What Changes

- Mark successful native `/codex/responses` responses as having server-accounted reasoning so Codex trusts Copilot's usage instead of adding the encrypted-reasoning estimate again.
- Keep the signal client-facing and route-specific: do not send it upstream, do not add it to Claude Code responses, and do not claim successful accounting on non-success Responses results.
- Add contract-first header-boundary and captured-usage regression tests, including a mutation check that fails when the signal is removed.
- Add a real-Codex two-turn behavior actuator whose first turn creates historical encrypted reasoning and whose second tool turn proves the client does not compact at the false post-sampling threshold.
- Document the Copilot usage observation, the Codex header contract, and the remaining upstream Codex pre-turn flag-reset limitation.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-response-fidelity`: Extend the native Codex response contract to include the reasoning-accounting HTTP signal, strict route/status isolation, and client-observed compaction evidence.

## Impact

- Affects the Codex `/responses` endpoint/strategy response-header boundary and its unit, API-contract, and real-client behavior coverage.
- Does not change Copilot request bytes, Responses SSE event bytes, model catalog limits, Claude Code behavior, authentication, or dependencies.
- Requires validation against real Codex 0.147/0.148 behavior and the client's own persisted evidence; bridge HTTP 200 alone remains insufficient.
