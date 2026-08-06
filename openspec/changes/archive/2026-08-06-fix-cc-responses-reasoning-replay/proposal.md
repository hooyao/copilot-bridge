## Why

Claude Code requests routed to `gpt-5.6-sol` receive Responses reasoning items containing encrypted state, but T3 intentionally discards every item, so no reasoning state reaches or returns from the client on the next tool-result turn. Real Claude Code 2.1.220 has now been proven to preserve an opaque `redacted_thinking.data` value byte-for-byte across a real tool execution, while live Copilot probes prove a replay needs at least `encrypted_content` plus `summary`; dropping it is therefore an avoidable state-fidelity loss.

## What Changes

- Carry each complete Responses reasoning item through the Claude client edge in a hidden, versioned `redacted_thinking.data` envelope instead of dropping it.
- Preserve and restore the encrypted blob, required `summary`, and present `id`/`content` values exactly across Responses→Anthropic→Responses tool-result turns.
- Decode the private envelope only on a Claude Code request translated to a Responses target; ordinary Anthropic redacted thinking and native Codex paths remain protocol-isolated.
- Validate the envelope strictly and fail closed without exposing encrypted/provider metadata as visible text.
- Preserve reasoning-item order and Anthropic content-block lifecycle while keeping response detectors authoritative.
- Add contract-derived T3/T2 round-trip tests, mutation proof, captured-byte replay, direct backend field-requirement probes, and a real Claude Code CC→gpt multi-turn behavior run judged from transcript and exact trace.

## Capabilities

### New Capabilities

- `cc-responses-reasoning-replay`: Defines hidden, byte-faithful Responses reasoning-state carriage across Claude Code tool-result turns, strict envelope isolation, and real-client acceptance.

### Modified Capabilities

- `codex-response-fidelity`: Clarifies that the new Claude-edge reasoning carrier does not alter native Codex event restoration or native Codex reasoning input replay.

## Impact

- Response translation: `ResponsesToAnthropicStream` and the CC→Responses strategy call site.
- Request translation: `ResponsesRequestBuilder` with explicit Claude client provenance.
- Protocol isolation: Claude Code outbound marker handling and strict private-envelope encoding/decoding.
- Tests: stream round-trip invariants, captured-byte ApiContract, backend reasoning-replay probe, deterministic real-Claude carrier proof, and live CC→gpt ClientBehavior acceptance.
- No endpoint, user configuration, dependency, or persisted-data change. The carrier exists only within the active Claude Code conversation and remains hidden protocol state.
