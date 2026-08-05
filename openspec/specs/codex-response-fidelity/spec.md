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

A Claude Code request routed from `/cc` to a Responses backend SHALL continue to receive Anthropic Messages stream semantics, not raw Responses events. Every bridge-private carrier used for native Responses fidelity SHALL be removed at the Claude client edge. Native Codex fidelity changes MUST NOT alter the request translation contract or the `/cc` native-Anthropic hot path.

#### Scenario: Claude Code cross-route receives Anthropic events only
- **WHEN** `/cc` traffic is routed to a gpt Responses model whose upstream response contains native carrier data
- **THEN** Claude Code receives only valid Anthropic Messages events
- **AND** no raw Responses event, `bridge_*` property, or carrier representation reaches the client.

#### Scenario: Codex request remains semantically unchanged
- **WHEN** a real Codex request passes through T1 and T2 on the same gpt model
- **THEN** its upstream Responses body remains equivalent under the existing documented request transformations.

#### Scenario: Responses text blocks flatten without JSON injection
- **WHEN** a function-call output carries an array containing `text`, `input_text`, or `output_text` blocks
- **THEN** the upstream output string contains each block's text value separated by newlines
- **AND** the bridge does not inject the block's JSON object wrapper into model context.

#### Scenario: Non-text tool-result blocks remain visible
- **WHEN** a function-call output array contains a genuinely non-text block
- **THEN** its compact JSON value remains in the flattened output rather than being silently dropped.

#### Scenario: Detailed reasoning echo retains required fields
- **WHEN** Codex echoes a prior reasoning item containing encrypted content and present `summary` or `content` fields on a later tool-result turn
- **THEN** T1/T2 re-emits every present reasoning field with the same JSON value
- **AND** Copilot does not reject the turn for a missing reasoning summary.

### Requirement: Capture and real-client evidence prove fidelity

Acceptance SHALL compare committed capture fixtures at both wire boundaries and SHALL run a real headless Codex multi-step, multi-tool task through a bridge subprocess. Contract tests SHALL prove complete event value/order conservation, authorized detector mutation, terminal integrity, and marker isolation. The real-client verdict SHALL inspect Codex's own structured dispatch log for successful tool execution and absence of router or incompatible-payload fatals; bridge HTTP status alone is insufficient.

#### Scenario: Real capture round-trips without undeclared differences
- **WHEN** a capture containing detailed reasoning, message phases, tool calls, complete usage, and unknown fields is replayed through T3, response inspection, and T4
- **THEN** a field-level diff reports no differences except a detector mutation explicitly asserted by that test.

#### Scenario: Real Codex executes through the fixed path
- **WHEN** real headless Codex runs a path-exercising multi-turn tool task through the bridge
- **THEN** the client executes the tools and completes the turn
- **AND** its own dispatch log contains no aborted tool, incompatible payload, or router fatal for that run.
