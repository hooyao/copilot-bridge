## ADDED Requirements

### Requirement: Confirmed native Codex context rejection is recoverable

For a streaming request received on `/codex/responses` and resolved to `BackendVendor.CopilotResponses`, the bridge SHALL adapt the confirmed pre-stream Copilot context-window rejection into the Codex-native failure condition. A confirmed rejection requires upstream HTTP 400, a bounded parseable JSON body whose `error.code` is exactly `invalid_request_body`, and Copilot's exact confirmed context-window message. The client-facing response SHALL be HTTP 200 `text/event-stream` containing exactly one `response.failed` event whose `response.error.code` is `context_length_exceeded` and whose message is bounded and bridge-owned. The bridge MUST NOT add `X-Reasoning-Included` to this response.

#### Scenario: Exact Copilot 400 becomes one native failed terminal

- **WHEN** a streaming native Codex request receives the exact confirmed Copilot context-window 400 from the resolved Responses backend
- **THEN** Codex receives HTTP 200 with `Content-Type: text/event-stream`
- **AND** receives exactly one `response.failed` terminal with `error.code=context_length_exceeded`
- **AND** receives no completed or incomplete terminal and no `X-Reasoning-Included` header.

#### Scenario: Raw and downstream audits retain their own truth

- **WHEN** tracing is enabled for an adapted context rejection
- **THEN** `upstream-resp` retains HTTP 400, the original headers, and the exact Copilot error body
- **AND** `inbound-resp` records HTTP 200, the event-stream content type, and the single adapted failed event.

#### Scenario: Near misses remain unchanged

- **WHEN** the request is non-streaming, the path or resolved vendor differs, the status is not 400, the code or message differs, the body is malformed, or the body exceeds the classifier bound
- **THEN** the native Codex adapter preserves the original downstream status and body
- **AND** does not label the result `context_length_exceeded`.

#### Scenario: Claude Code adaptation remains isolated

- **WHEN** the exact same Copilot error is returned for a `/cc` request routed to Responses
- **THEN** the existing Anthropic prompt-too-long translation applies
- **AND** no native Responses failed terminal crosses the Claude Code edge.

### Requirement: Real Codex proves compact retry and resumed execution

Acceptance SHALL drive a real headless Codex client through a Debug bridge subprocess and a deterministic Responses upstream. The scenario SHALL trigger automatic pre-turn compaction with a bounded history, return the exact confirmed Copilot HTTP 400 on the first compact attempt, accept the client's context-recovery retry, and then complete a real tool trajectory. The verdict SHALL use the per-run bridge trace and Codex's own structured log; a bridge status, synthetic replay, exit code, or final canary alone is insufficient.

#### Scenario: Codex trims and retries after the adapted failure

- **WHEN** the first automatic compaction attempt receives the adapted native context failure
- **THEN** Codex issues a later compaction request with reduced retained history
- **AND** the deterministic upstream accepts that retry and returns a compaction summary.

#### Scenario: Work continues after compaction

- **WHEN** the reduced compaction retry succeeds
- **THEN** Codex completes a matching tool call/output round trip and reports the final canary
- **AND** its own log contains no execution abort, router fatal, incompatible payload, or unhandled generic bad-request classification for the injected context error.
