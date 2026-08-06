# cc-responses-reasoning-replay Specification

## Purpose
Keep a Responses backend's encrypted reasoning state alive across Claude Code turns instead of discarding it at the client edge. Defines the hidden, versioned carrier the Anthropic wire can hold, its strict fail-closed decoding, the fields a live-proven backend requires on replay, and the real-client evidence that decides acceptance.
## Requirements
### Requirement: Complete Responses reasoning state crosses the Claude client edge

For a Claude Code request routed to a Responses backend, the bridge SHALL represent each replayable Responses reasoning output item as a standard hidden Anthropic `redacted_thinking` block. The block SHALL carry a versioned opaque envelope preserving `encrypted_content`, `summary`, and every present `id` and `content` value. It MUST NOT expose those values as visible text or plaintext thinking.

A reasoning item is replayable only when it contains a non-empty `encrypted_content` string and a `summary` array. The bridge MUST NOT emit a carrier that omits a backend-required field and predictably fails the next turn.

#### Scenario: Complete reasoning item becomes one hidden block
- **WHEN** gpt-5.6-sol emits a reasoning item with encrypted content, summary, id, and content
- **THEN** Claude Code receives exactly one valid `redacted_thinking` block in the corresponding output position
- **AND** decoding its opaque envelope yields the same JSON values.

#### Scenario: Incomplete reasoning item downgrades explicitly
- **WHEN** a Responses reasoning item lacks encrypted content or summary
- **THEN** the bridge does not emit a malformed replay carrier
- **AND** records the item as an unreplayable stateless downgrade without exposing it as text.

### Requirement: Claude echo reconstructs the Responses reasoning item exactly

When real Claude Code returns a valid bridge reasoning envelope on a subsequent tool-result request, T2 SHALL restore a Responses `reasoning` input item at the same conversation position with byte-identical encrypted content and JSON-value-identical summary plus every present id/content field. The restored shape SHALL satisfy the live-proven gpt-5.6-sol requirement that `summary` accompany encrypted content.

#### Scenario: Tool-result turn restores required reasoning fields
- **WHEN** Claude Code echoes the carrier before the prior tool call and sends its tool result
- **THEN** the next Responses request contains the restored reasoning item before the tool call/result trajectory
- **AND** Copilot accepts the turn without a missing-summary error.

#### Scenario: Empty summary array is preserved
- **WHEN** the original reasoning item contains `summary: []`
- **THEN** the replay contains an empty summary array rather than omitting the field.

### Requirement: Private reasoning envelopes are strict and protocol-isolated

The bridge SHALL recognize its reasoning envelope only on an explicitly identified Claude Code→Responses request path. The envelope SHALL have a fixed version, bounded size, fixed field set, and strict field types. A prefixed envelope that is malformed, oversized, has an unsupported version, or lacks required fields MUST fail closed with a bounded bridge-owned error and MUST NOT be forwarded as arbitrary JSON or rendered as visible content.

A non-prefixed Anthropic redacted-thinking value SHALL remain ordinary opaque provider data under the existing behavior. Native Codex request/response fidelity SHALL remain unchanged.

#### Scenario: Native Codex lookalike stays opaque
- **WHEN** a native Codex request contains data resembling the private envelope
- **THEN** the Claude envelope decoder does not run and the native Codex contract is unchanged.

#### Scenario: Malformed private envelope fails closed
- **WHEN** a Claude→Responses request contains the private prefix with invalid base64, JSON, version, or required fields
- **THEN** the request is rejected with a bounded non-secret error
- **AND** no encrypted/provider payload appears in the error or model-visible text.

#### Scenario: Provider-native redacted thinking is not mistaken for a carrier
- **WHEN** a Claude request contains a non-prefixed `redacted_thinking.data` value
- **THEN** the bridge does not parse it as private envelope JSON.

### Requirement: Reasoning block lifecycle and order remain valid

The bridge SHALL emit a complete redacted-thinking block using one `content_block_start` followed by one matching `content_block_stop`, without visible deltas. Reasoning, text, and tool-call blocks SHALL preserve Responses output order and use unique Anthropic block indexes. Response detectors SHALL remain authoritative over semantic text/tool output and SHALL NOT inspect encrypted carrier bytes as generated text.

#### Scenario: Reasoning precedes a tool call
- **WHEN** a Responses turn outputs reasoning followed by a function call
- **THEN** Claude Code receives a closed redacted-thinking block followed by a closed tool-use block at increasing indexes.

### Requirement: Live client and backend evidence prove the full replay loop

Acceptance SHALL include a real Claude Code 2.1.220 tool trajectory and current live gpt-5.6-sol. The client transcript SHALL prove the hidden block was stored, a tool executed, and the final turn completed without showing the carrier as visible text. Exact traces SHALL prove response N reasoning fields entered the carrier, request N+1 echoed the carrier, T2 restored the required reasoning item, and Copilot accepted the continuation. HTTP 200 or a synthetic replay alone is insufficient.

#### Scenario: Real Claude tool turn retains encrypted reasoning
- **WHEN** real Claude Code receives gpt-5.6-sol reasoning plus a tool call through CC→Responses
- **THEN** it executes the tool and completes the next turn
- **AND** exact traces show JSON-value-faithful reasoning replay with no visible marker leak or missing-summary 400.

