## MODIFIED Requirements

### Requirement: Cross-client protocol isolation is preserved

A Claude Code request routed from `/cc` to a Responses backend SHALL continue to receive Anthropic Messages stream semantics, not raw Responses events. Every bridge-private response carrier used for native Responses fidelity SHALL be removed at the Claude client edge. Native Codex request carriage and its authorized mutations SHALL be governed by the `codex-request-fidelity` capability; response-event restoration MUST NOT inspect or alter that request carrier.

The `cc-responses-reasoning-replay` capability MAY use a versioned value inside the standard hidden `redacted_thinking.data` field exclusively for a Claude Code→Responses round trip. That value SHALL be decoded only with explicit Claude-client provenance and MUST NOT alter native Codex event restoration, native Codex request carriage, native Codex reasoning replay, or ordinary provider-native redacted-thinking data.

Claude-originated tool-result translation SHALL remain source-independent at T2: the Claude source pushes semantic text/image content into the shared IR, and the Responses destination pulls that semantic content without checking source identity. Native Codex tool results carrying an OpenAI/Responses opaque-output extension are instead governed by `codex-request-fidelity` and MUST retain their original JSON value.

#### Scenario: Claude Code cross-route receives Anthropic events only
- **WHEN** `/cc` traffic is routed to a gpt Responses model whose upstream response contains native carrier data
- **THEN** Claude Code receives only valid Anthropic Messages events
- **AND** no raw Responses event, `bridge_*` property, or native-event carrier representation reaches the client.

#### Scenario: Codex request remains semantically unchanged
- **WHEN** a real Codex request passes through T1 and T2 on a Responses route
- **THEN** its request fidelity and any destination mutation are evaluated under `codex-request-fidelity`
- **AND** response carrier logic neither restores nor removes request fields.

#### Scenario: Responses text blocks flatten without JSON injection
- **WHEN** a Claude-originated function-call output contains `text`, `input_text`, or `output_text` blocks and no recognized image requiring structured translation
- **THEN** the Responses destination emits each semantic text value in order without injecting the block's JSON wrapper into model context.

#### Scenario: Non-text tool-result blocks remain visible
- **WHEN** a Claude-originated function-call output contains an unsupported non-text block that cannot use structured translation
- **THEN** its compact JSON value remains visible in the fallback output rather than being silently dropped.

#### Scenario: Proven Claude image uses structured function output
- **WHEN** a Claude-originated function output contains only supported text/image blocks and the exact Responses target is live-proven to accept structured multimodal output
- **THEN** the recognized image is emitted as `input_image` under the `cc-responses-multimodal-tool-results` contract
- **AND** this does not change native Codex request carriage.

#### Scenario: Claude reasoning carrier is client-specific
- **WHEN** a Claude Code→Responses turn uses a private redacted-thinking envelope to preserve a Responses reasoning item
- **THEN** only the explicit Claude client edge decodes it
- **AND** native Codex request and response fidelity remain unchanged.

#### Scenario: Detailed reasoning echo retains required fields
- **WHEN** Codex echoes a prior reasoning item containing encrypted content and present `id`, `summary`, `content`, or future sibling fields
- **THEN** those input fields are preserved under `codex-request-fidelity`
- **AND** the response-fidelity carrier does not participate in the request reconstruction.
