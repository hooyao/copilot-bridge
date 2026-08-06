## MODIFIED Requirements

### Requirement: Cross-client protocol isolation is preserved

A Claude Code request routed from `/cc` to a Responses backend SHALL continue to receive Anthropic Messages stream semantics, not raw Responses events. Every bridge-private carrier used for native Responses fidelity SHALL be removed at the Claude client edge. Native Codex fidelity changes MUST NOT alter the native Codex request round-trip contract or the `/cc` native-Anthropic hot path. A Claude-originated image inside `tool_result.content` is not a native Codex opaque output: when the exact target profile is live-proven to support Responses structured multimodal function output, the bridge SHALL apply the `cc-responses-multimodal-tool-results` contract instead of flattening that recognized image into text.

#### Scenario: Claude Code cross-route receives Anthropic events only
- **WHEN** `/cc` traffic is routed to a gpt Responses model whose upstream response contains native carrier data
- **THEN** Claude Code receives only valid Anthropic Messages events
- **AND** no raw Responses event, `bridge_*` property, or carrier representation reaches the client.

#### Scenario: Codex request remains semantically unchanged
- **WHEN** a real Codex request passes through T1 and T2 on the same gpt model
- **THEN** its upstream Responses body remains equivalent under the existing documented request transformations.

#### Scenario: Responses text blocks flatten without JSON injection
- **WHEN** a function-call output carries an array containing `text`, `input_text`, or `output_text` blocks and no recognized Claude image requiring structured translation
- **THEN** the upstream output string contains each block's text value separated by newlines
- **AND** the bridge does not inject the block's JSON object wrapper into model context.

#### Scenario: Non-text tool-result blocks remain visible
- **WHEN** a function-call output array contains a non-text block that is not translated under the structured multimodal contract
- **THEN** its compact JSON value remains in the flattened output rather than being silently dropped.

#### Scenario: Proven Claude image uses structured function output
- **WHEN** a Claude-originated function output contains only supported text/image blocks and the exact Responses target is live-proven to accept structured multimodal output
- **THEN** the recognized image is emitted as `input_image` under the `cc-responses-multimodal-tool-results` contract
- **AND** this does not change the native Codex round-trip behavior above.

#### Scenario: Detailed reasoning echo retains required fields
- **WHEN** Codex echoes a prior reasoning item containing encrypted content and present `summary` or `content` fields on a later tool-result turn
- **THEN** T1/T2 re-emits every present reasoning field with the same JSON value
- **AND** Copilot does not reject the turn for a missing reasoning summary.
