## Context

Codex `rust-v0.153.3` includes openai/codex change #39782, “Support
standalone named function call outputs.” Its public `ResponseItem` union now has
two valid `function_call_output` shapes:

- paired: `call_id` identifies a preceding model tool call;
- standalone: no `call_id`, a non-empty `name`, optional `namespace`, and an
  `output` injected as an external tool event through `thread/inject_items`.

The Codex app-server documentation says a standalone output retains tool-tier
authority. A captured Desktop heartbeat uses exactly this shape with
`name=automation_update`, `namespace=codex_app`, and an XML heartbeat payload.
Bridge v0.5.14 instead declares `ResponsesFunctionCallOutputItem.CallId` as
required. System.Text.Json therefore returns 400 before T1, before routing, and
before any upstream request. One affected thread accumulated 103 rejected
heartbeat inputs, proving this is a persistent history-compatibility failure rather
than one malformed turn.

The bridge's frozen shared IR represents paired tool results but has no semantic
form for an external, unpaired tool event. It already has an ordered OpenAI provider
carrier for source-native items that must traverse T1/T2 without semantic loss.

## Goals / Non-Goals

**Goals:**

- Mirror the Codex 0.153.3 paired/standalone function-output union without making
  invalid combinations silently valid.
- Preserve standalone output identity, authority, JSON value, future siblings, and
  input position on a native Codex-to-Responses route.
- Keep the paired tool-result path and all shared detector behavior unchanged.
- Prove Copilot accepts the standalone wire shape using a live probe and prove a
  real Codex app-server client can inject, send, and continue past the item.
- Turn the captured failure shape into a permanent contract/corpus regression net.

**Non-Goals:**

- Invent a `call_id`, synthesize a preceding function call, or reinterpret a
  standalone output as an ordinary user/developer message.
- Parse, deduplicate, or otherwise special-case heartbeat XML or the
  `automation_update` tool name.
- Repair or delete already accumulated events in a user's local thread history.
- Change Claude Code translation or Codex response streaming.

## Decisions

### 1. Model the union by invariant, not by one newly nullable field

`ResponsesFunctionCallOutputItem` will retain `output` as required and make
`call_id`, `name`, and `namespace` optional, matching Codex's source schema. A
single validation seam immediately after materializing the raw item will accept
exactly:

- a non-empty string `call_id`; or
- no usable `call_id` and a non-empty string `name`.

An item with neither identity is rejected with an actionable `JsonException`.
Both fields being present remains valid because Codex's source type permits it;
the presence of `call_id` selects the paired semantic path.

Alternative: introduce two classes with the same JSON discriminator. Rejected
because source-generated polymorphism cannot select two derived records for one
discriminator without moving all dispatch into a bespoke union converter. The
single DTO plus one invariant validation seam mirrors the upstream Rust enum and
keeps AOT metadata simple.

Alternative: catch every known-type deserialization exception and downgrade to
`ResponsesUnknownItem`. Rejected because it would turn genuinely malformed
messages/reasoning/tool results into opaque upstream traffic and hide useful client
errors.

### 2. Paired outputs remain semantic; standalone outputs use ordered provider carriage

T1 will keep translating an item with `call_id` into the existing IR
`ToolResultBlockParam`. For a standalone named output, T1 will serialize the complete
typed item and add it to the existing ordered `passthrough_items` OpenAI provider
bag at its current IR-message position. T2 will reinsert that raw JSON value into
the Responses `input[]` array through the existing passthrough writer.

This is the only representation that preserves tool-tier authority. Mapping the
payload to a user message would lower its authority; mapping it to a tool result
would require a fictitious tool-use id. Provider carriage also preserves optional
`id`, `namespace`, metadata, and future siblings without expanding the frozen IR.

On a non-Responses destination the OpenAI carrier remains inert under the existing
source-push/destination-pull contract. The bridge will not guess a cross-protocol
equivalent for a provider-specific authority tier.

### 3. Backend acceptance is model-dependent and does not authorize a rewrite

A live matrix sent the same minimal standalone named output directly to every current
bridge Responses profile. Copilot accepted it on `gpt-5.3-codex`, `gpt-5.4`,
`gpt-5.4-mini`, `gpt-5.5`, `gpt-5.6-luna`, `gpt-5.6-sol-fast`, and
`gpt-5.6-terra`. It rejected it on `gpt-5.6-sol`, `gpt-5-mini`, and
`mai-code-1-flash-picker` with `Function call output requires call_id`. A second
probe supplied an invented call id on each rejecting model; all three then rejected
the request with `No tool call found`, proving that a synthetic id is not a valid
coercion. A captured Desktop heartbeat shape containing two persisted events was
accepted by `gpt-5.6-sol-fast`.

The bridge will therefore preserve and forward the native item for every Responses
target without a target-specific rewrite. Supporting targets work; a rejecting target
returns its explicit backend error. This is preferable to silently lowering authority,
inventing history, or changing the requested model. An operator may deliberately route
such traffic to a supporting model (as the current `gpt-5.6-sol` →
`gpt-5.6-sol-fast` location does), but the profile layer will not make that policy
choice. Because the bridge performs no rejection-driven mutation, this result does not
add a new profile coercion; the live ApiContract matrix guards the backend fact.

### 4. Tests assert the external contract and include mutation proof

Unit coverage will state the contract independently of implementation:

- paired output retains `call_id` and follows the semantic tool-result path;
- standalone output retains absent `call_id`, name, namespace, output, future
  siblings, and relative order;
- removing the standalone branch reproduces the original deserialize/translation
  failure;
- neither `call_id` nor name is rejected before upstream I/O.

The production corpus replay will include the captured 0.153.3 shape. A real Codex
app-server behavior case will use `thread/inject_items` to insert a standalone output,
then start a path-exercising tool turn. The verdict requires the standalone item on
the bridge trace, a subsequent real tool call/output round trip, completed client
output, and zero router/error rows in Codex's own dispatch log.

### 5. Existing poisoned histories are not silently rewritten

The bridge will preserve every standalone event it receives, including repeated
ones. It will not infer that two scheduled events are equivalent from XML or tool
names. Operators should retire a thread that accumulated a large failed-event backlog
before upgrading; clean threads and future events are the supported recovery path.

## Risks / Trade-offs

- **Some Copilot models reject the Codex extension** -> Preserve the native item and
  surface the backend error; validate the real-client path on the supporting
  `gpt-5.6-sol-fast` target without silently changing other models.
- **Provider carriage bypasses semantic request stages for the standalone payload** ->
  This matches existing handling of provider-native authority-bearing items; the
  complete raw item remains auditable at both trace boundaries.
- **Making `call_id` optional could admit malformed ordinary outputs** -> Require a
  non-empty `name` whenever `call_id` is absent and mutation-test the rejection.
- **Old threads can replay many accumulated heartbeats after upgrade** -> Do not
  deduplicate in the bridge; document that such threads should be retired.
- **Codex evolves the standalone shape again** -> Preserve extension fields and keep
  the real inbound corpus replay as a version-drift detector.

## Migration Plan

1. Keep the live acceptance matrix and captured-shape probe as the backend oracle.
2. Deploy the DTO/T1 change and contract tests without changing the paired path.
3. Run the real Codex app-server injection case on a non-8765 subprocess and inspect
   the client's own dispatch evidence.
4. Archive/sync the OpenSpec change and ship it in the current PR.
5. After release, use a clean task for heartbeat verification; do not resume the
   thread containing the 103-event backlog.

Rollback is the previous binary. It restores the known 400 for standalone events but
does not mutate persisted thread history.

## Open Questions

None. The model-dependent acceptance matrix and failed synthetic-id alternative were
resolved by live probes before product implementation.
