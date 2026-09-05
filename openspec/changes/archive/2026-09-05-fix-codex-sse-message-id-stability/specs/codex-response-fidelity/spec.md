## MODIFIED Requirements

### Requirement: Native Responses events are lossless on a clean Codex route

For a request received on `/codex/responses` and routed to Copilot `/responses`, the bridge SHALL emit every upstream Responses SSE event in the original order and with full JSON value fidelity when response inspection authorizes the event unchanged. This includes known and unknown event types, unknown sibling fields, reasoning items and summaries, encrypted reasoning content, message phases, response identity, every non-message item identity, usage detail fields, delta metadata, and terminal response metadata. The bridge MUST NOT synthesize a replacement event merely because the shared Anthropic IR does not model an upstream Responses field.

Insignificant SSE framing or JSON serialization differences are allowed, but every upstream event name and JSON value MUST survive except at the protocol-corrective identity paths declared below. A bridge-internal carrier or marker MUST NOT appear as a property in the downstream Responses JSON.

Two protocol-corrective identity exceptions are required:

1. A `custom_tool_call` item id: every client-facing lifecycle/output copy SHALL use a `ctc`-prefixed id derived from the call id until T3 observes Copilot's stable `ctc` input-event id, after which completed/terminal copies SHALL use that stable id.
2. A streaming `message` item id: once Copilot adds a message output item, every client-facing lifecycle reference for that output index SHALL use the added item's id as one canonical identity. If the added item has no usable id, the bridge SHALL use one deterministic, client-only, non-`msg` fallback. This includes the added and done item objects, every top-level `item_id` on message content/text events, and the corresponding terminal output item. A conforming stream that already uses one stable id SHALL remain identity-value-faithful. The correction SHALL NOT change event order, count, timing, content, phase, annotations, status, unknown siblings, or any non-message item identity. If Codex echoes an opaque or fallback non-`msg` identity, the existing request-side correction SHALL remove it before the Copilot request; a legitimate canonical `msg_...` identity SHALL retain the existing replay behavior.

#### Scenario: Detailed reasoning survives a same-protocol stream
- **WHEN** Codex requests a reasoning summary and Copilot emits reasoning item lifecycle events, reasoning-summary events, encrypted reasoning content, and a completed terminal
- **THEN** Codex receives those events in the same order with the same JSON values
- **AND** the completed terminal retains the original response id, reasoning items, full usage details, and unmodeled metadata.

#### Scenario: Rolling message ids become one stable client identity
- **WHEN** Copilot emits one logical message whose added item, content/text events, done item, and terminal output carry different opaque ids
- **THEN** Codex receives the added item's id at every message-id path for that output index
- **AND** receives each original event exactly once and in its original position
- **AND** every non-id JSON value remains unchanged.

#### Scenario: Message phase and future fields survive
- **WHEN** Copilot emits a message item with `phase: "commentary"` or `phase: "final_answer"`, delta metadata, and an unmodeled sibling field
- **THEN** the Codex-facing events retain the phase, metadata, sibling field, and content values
- **AND** only the message-id paths carry the declared stable client identity.

#### Scenario: Opaque corrected message identity is not replayed upstream
- **WHEN** Codex echoes a completed assistant message carrying an opaque or synthesized non-`msg` canonical identity on a later turn
- **THEN** T1/T2 remove that id before sending the message to Copilot
- **AND** preserve the message content, phase, status, and unrelated fields.

#### Scenario: Legitimate message identity remains replayable
- **WHEN** Copilot's added message item already carries a stable `msg_...` id and Codex echoes it on a later turn
- **THEN** the response lifecycle retains that id unchanged
- **AND** T1/T2 retain the existing valid-message-id replay behavior.

#### Scenario: Unknown event type survives
- **WHEN** Copilot emits a valid Responses event type unknown to this bridge version between two known events
- **THEN** the unknown event reaches Codex at the same position with every JSON field intact
- **EXCEPT** a top-level `item_id` is corrected when that event names an output index already established as a message.

#### Scenario: Custom-tool identity remains echo-safe
- **WHEN** Copilot emits rolling non-`ctc` custom-tool output-item ids and a stable `ctc` id on custom-tool input events
- **THEN** the Codex-facing added item has a synthesized `ctc` id
- **AND** the completed item and terminal output use the observed stable `ctc` id
- **AND** every non-id field retains its original JSON value.

### Requirement: Capture and real-client evidence prove fidelity

Acceptance SHALL compare committed capture fixtures at both wire boundaries and SHALL run a real headless Codex multi-step, multi-tool task through a bridge subprocess. Contract tests SHALL prove complete event value/order conservation, the declared message/custom-tool identity corrections, authorized detector mutation, terminal integrity, and marker isolation. The real-client verdict SHALL inspect Codex's own structured dispatch log for successful tool execution and absence of router or incompatible-payload fatals; bridge HTTP status alone is insufficient.

The message-id case SHALL use a current Codex client whose item lifecycle tracking reproduces the pre-fix `item completed without a recorded start timestamp` signal. Its final visible message SHALL contain a bounded canary exactly once, and the bridge trace SHALL prove that every event in that message lifecycle carries the same client-facing id.

#### Scenario: Real capture round-trips without undeclared differences
- **WHEN** a capture containing rolling message ids, detailed reasoning, message phases, tool calls, complete usage, and unknown fields is replayed through T3, response inspection, and T4
- **THEN** a field-level diff reports no differences except an identity correction or detector mutation explicitly authorized and asserted by that test
- **AND** every lifecycle reference for one message output index uses one stable client-facing id.

#### Scenario: Real Codex executes through the fixed path
- **WHEN** real current Codex runs a path-exercising multi-turn tool task through the bridge and completes with a visible message
- **THEN** the client executes the tools and completes the turn
- **AND** the visible canary occurs exactly once
- **AND** the final message lifecycle uses one id
- **AND** the client's own evidence contains no message-item start/completion mismatch, aborted tool, incompatible payload, or router fatal for that run.
