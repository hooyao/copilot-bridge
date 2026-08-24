# codex-response-fidelity Specification

## Purpose
Preserve the complete native OpenAI Responses contract between GitHub Copilot and Codex while keeping the bridge's shared semantic response inspection authoritative, cross-client protocol boundaries isolated, and every non-cancelled stream honestly terminated.
## Requirements
### Requirement: Native Responses events are lossless on a clean Codex route

For a request received on `/codex/responses` and routed to Copilot `/responses`, the bridge SHALL emit every upstream Responses SSE event in the original order and with full JSON value fidelity when response inspection authorizes the event unchanged. This includes known and unknown event types, unknown sibling fields, reasoning items and summaries, encrypted reasoning content, message phases, response and item identities, usage detail fields, delta metadata, and terminal response metadata. The bridge MUST NOT synthesize a replacement event merely because the shared Anthropic IR does not model an upstream Responses field.

Insignificant SSE framing or JSON serialization differences are allowed, but every upstream event name and JSON value MUST survive. A bridge-internal carrier or marker MUST NOT appear as a property in the downstream Responses JSON.

The one protocol-corrective exception is a `custom_tool_call` item id: every client-facing lifecycle/output copy SHALL use a `ctc`-prefixed id derived from the call id until T3 observes Copilot's stable `ctc` input-event id, after which completed/terminal copies SHALL use that stable id. Every unrelated field remains value-identical.

#### Scenario: Detailed reasoning survives a same-protocol stream
- **WHEN** Codex requests a reasoning summary and Copilot emits reasoning item lifecycle events, reasoning-summary events, encrypted reasoning content, and a completed terminal
- **THEN** Codex receives those events in the same order with the same JSON values
- **AND** the completed terminal retains the original response id, output items, full usage details, and unmodeled metadata.

#### Scenario: Message phase and future fields survive
- **WHEN** Copilot emits a message item with `phase: "commentary"` or `phase: "final_answer"`, delta metadata, and an unmodeled sibling field
- **THEN** the Codex-facing event retains the phase, metadata, sibling field, item id, and content values.

#### Scenario: Unknown event type survives
- **WHEN** Copilot emits a valid Responses event type unknown to this bridge version between two known events
- **THEN** the unknown event reaches Codex at the same position with every JSON field intact.

#### Scenario: Custom-tool identity remains echo-safe
- **WHEN** Copilot emits rolling non-`ctc` custom-tool output-item ids and a stable `ctc` id on custom-tool input events
- **THEN** the Codex-facing added item has a synthesized `ctc` id
- **AND** the completed item and terminal output use the observed stable `ctc` id
- **AND** every non-id field retains its original JSON value.

### Requirement: Semantic inspection remains authoritative

The bridge SHALL continue to translate Responses content into the shared Anthropic-shaped inspection stream so all enabled response detectors observe the same text, tool input, block boundaries, usage, and terminal semantics they observed before lossless carriage. Original-event carriage MUST NOT bypass a detector decision.

If every detector returns no action for a semantic event, the corresponding original Responses event or events SHALL remain eligible for lossless delivery. If a detector drops, rewrites, or aborts a semantic event, the bridge SHALL NOT restore an original event whose delivery would undo that decision. The explicitly supported model-only rewrite SHALL patch only model identity into original events. Any other drop/rewrite on the native stream SHALL fail closed rather than reconstruct content from state accumulated before the mutation. On a stream-preserving abort, any withheld unsafe content SHALL remain suppressed and the Codex edge SHALL emit the protocol-native failed terminal required below. When a detector is configured to buffer before delivery, the existing real non-2xx HTTP response contract remains authoritative and no private carrier may enter that body.

#### Scenario: Clean content restores original events
- **WHEN** T3 maps one or more original Responses events to semantic inspection events and every enabled detector returns no action
- **THEN** T4 emits the associated original Responses events rather than a synthesized approximation.

#### Scenario: Leak abort cannot be bypassed by the carrier
- **WHEN** the leak detector aborts while inspecting text derived from an original Responses delta
- **THEN** the unsafe original delta is not restored after the detector
- **AND** the Codex stream ends through one `response.failed` terminal.

#### Scenario: Model rewrite remains effective
- **WHEN** a supported route intentionally rewrites response model identity
- **THEN** the downstream response reflects the configured client-facing model
- **AND** unrelated original fields remain value-identical.

### Requirement: Every non-cancelled Codex stream has exactly one terminal

After any Responses bytes have been accepted for a native Codex request, the bridge SHALL emit exactly one of `response.completed`, `response.incomplete`, or `response.failed`. A clean completed or incomplete upstream terminal SHALL be forwarded value-faithfully. An upstream failed terminal SHALL retain only its bounded machine code and MUST NOT expose a generated upstream error message. Premature EOF, malformed terminal state, upstream exception, detector abort, or stream-idle timeout SHALL produce exactly one Codex-native `response.failed` terminal after any safe prefix. The bridge MUST NOT fabricate a successful tool-input `.done`, output-item completion, or successful response terminal from an incomplete upstream stream.

If the downstream request cancellation token is already cancelled, the bridge SHALL stop writing and record client cancellation; it need not and generally cannot write a terminal after the client has disconnected.

#### Scenario: Upstream failed detail is bounded
- **WHEN** Copilot emits `response.failed` with a machine code and generated error message
- **THEN** Codex receives exactly one `response.failed` carrying a bounded bridge-owned message
- **AND** the generated upstream message does not cross the client edge.

#### Scenario: Premature EOF after custom-tool deltas
- **WHEN** Copilot emits custom-tool input deltas and closes without the tool-input done event, output-item done event, or a response terminal
- **THEN** the bridge does not synthesize successful tool completion
- **AND** Codex receives exactly one `response.failed` terminal unless Codex cancelled first.

#### Scenario: Upstream exception after visible output
- **WHEN** an upstream read throws after the bridge has relayed a safe event prefix and the downstream client remains connected
- **THEN** the safe prefix is retained and followed by exactly one `response.failed` terminal.

#### Scenario: Client cancels first
- **WHEN** Codex cancels the request before an upstream terminal is available
- **THEN** the bridge records the request as client-cancelled and does not claim that a complete response was delivered.

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

### Requirement: Capture and real-client evidence prove fidelity

Acceptance SHALL compare committed capture fixtures at both wire boundaries and SHALL run a real headless Codex multi-step, multi-tool task through a bridge subprocess. Contract tests SHALL prove complete event value/order conservation, authorized detector mutation, terminal integrity, and marker isolation. The real-client verdict SHALL inspect Codex's own structured dispatch log for successful tool execution and absence of router or incompatible-payload fatals; bridge HTTP status alone is insufficient.

#### Scenario: Real capture round-trips without undeclared differences
- **WHEN** a capture containing detailed reasoning, message phases, tool calls, complete usage, and unknown fields is replayed through T3, response inspection, and T4
- **THEN** a field-level diff reports no differences except a detector mutation explicitly asserted by that test.

#### Scenario: Real Codex executes through the fixed path
- **WHEN** real headless Codex runs a path-exercising multi-turn tool task through the bridge
- **THEN** the client executes the tools and completes the turn
- **AND** its own dispatch log contains no aborted tool, incompatible payload, or router fatal for that run.

### Requirement: Native Codex receives authoritative reasoning accounting

For a request received on `/codex/responses` and resolved to `BackendVendor.CopilotResponses`, the bridge SHALL add `X-Reasoning-Included: true` to every 2xx downstream HTTP response before any buffered body byte or SSE event is written. This client-edge signal declares that Copilot's reported input usage already accounts for replayed encrypted reasoning, so Codex MUST NOT need to add its fallback historical-reasoning estimate to that usage.

The bridge MUST NOT add the signal to the Copilot request, MUST NOT fabricate it in the raw `upstream-resp` audit when Copilot omitted it, and MUST NOT add it to `/cc`, a non-Responses destination, or a non-2xx upstream result. The `inbound-resp` audit SHALL record the signal exactly when the downstream Codex response carries it.

#### Scenario: Streaming Responses success carries the signal before SSE
- **WHEN** native Codex traffic resolves to Copilot Responses and Copilot returns HTTP 200 with an event stream
- **THEN** the downstream HTTP response contains `X-Reasoning-Included: true` before the first SSE event
- **AND** every authorized SSE event retains the existing native-response fidelity contract.

#### Scenario: Buffered Responses success carries the signal
- **WHEN** native Codex traffic resolves to Copilot Responses and Copilot returns a successful buffered Responses object
- **THEN** the downstream HTTP response contains `X-Reasoning-Included: true` before the body
- **AND** the body and usage retain their existing values.

#### Scenario: Raw and downstream audits tell different truthful facts
- **WHEN** Copilot returns a successful Responses result without `X-Reasoning-Included`
- **THEN** `upstream-resp` records no such header because Copilot did not send it
- **AND** `inbound-resp` records `X-Reasoning-Included: true` because the bridge supplied the Codex compatibility signal.

#### Scenario: Failure and cross-client paths remain isolated
- **WHEN** the request uses `/cc`, resolves to a non-Responses backend, or receives a non-2xx upstream HTTP result
- **THEN** the bridge does not synthesize `X-Reasoning-Included` for that response.

### Requirement: Backend reasoning-accounting fact is independently guarded

Acceptance SHALL include a live paired Copilot Responses probe in which the only material input difference is replayed reasoning state from a prior response. The request retaining replayed reasoning SHALL report a positive `input_tokens` delta over the request omitting it. The bridge-header regression test alone MUST NOT be treated as proof of this backend fact.

#### Scenario: Live Copilot usage charges replayed reasoning
- **WHEN** a live probe sends equivalent follow-up requests with and without complete reasoning items emitted by the same prior Copilot response
- **THEN** both requests complete successfully
- **AND** the with-reasoning response reports more input tokens than the without-reasoning response.

### Requirement: Real Codex proves the signal prevents false post-sampling compaction

Acceptance SHALL drive a real headless Codex client through a bridge subprocess for two turns on one thread. The first turn SHALL create historical encrypted reasoning while keeping the next pre-turn check below the configured compact limit. The second turn's tool-bearing sampling response SHALL report active usage below that limit while adding the first turn's fallback reasoning estimate would cross it. The client SHALL receive the accounting signal, execute the requested tools, and complete without issuing a false post-sampling compaction request.

The verdict SHALL use the run's bridge trace and Codex's own structured dispatch log. It SHALL require matching tool calls/results, no execution abort, and zero router/dispatch fatals. Bridge HTTP 200, a synthetic response replay without a real client, or a final-text canary alone is insufficient. A known Codex release that clears the signal before a later user-turn boundary is an external client limitation and is not evidence that the bridge omitted the signal during the completed turn.

#### Scenario: Second real-Codex turn stays below the false boundary
- **WHEN** a deterministic Responses upstream creates historical reasoning in turn one and returns a below-limit tool-bearing response in turn two
- **THEN** the bridge trace shows `X-Reasoning-Included: true` on successful client responses and no such header in raw upstream responses
- **AND** Codex completes the tool trajectory without a compact request before completion
- **AND** Codex's own log contains no aborted tool or router/dispatch fatal.

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

