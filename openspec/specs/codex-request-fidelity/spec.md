# codex-request-fidelity Specification

## Purpose
Preserve native Codex/OpenAI Responses request fields, JSON values, and input ordering through the shared Anthropic-shaped IR, while keeping source adapters destination-agnostic, destination builders source-agnostic, and every necessary backend coercion narrow, live-grounded, observable, and independently tested.
## Requirements
### Requirement: Clean native Responses requests preserve complete JSON values and order

For a request received on `/codex/responses` and routed to a Copilot Responses target, the bridge SHALL preserve every client-supplied request field, input item, input-item position, nested field, and JSON value when no routing rule, request stage, or destination contract explicitly mutates it. This applies to known and unknown top-level fields, unknown item types, future sibling fields on already-modeled item types, content parts, tool definitions, reasoning state, and provider metadata.

Insignificant JSON serialization differences such as whitespace, escaping, and object-property order are allowed. Array order and JSON values are significant. The bridge MUST NOT drop or reshape a value merely because the shared semantic IR does not type that provider-native field.

#### Scenario: Real gpt-5.6 request is value-faithful
- **WHEN** a real Codex Desktop request for `gpt-5.6-sol` passes through T1, the shared request pipeline, and T2 without an applicable route or profile mutation
- **THEN** a recursive JSON-value diff between the client body and the Copilot body reports no difference
- **AND** every `input[]` item remains at its original index.

#### Scenario: Reasoning context remains all-turns
- **WHEN** the client sends `reasoning.context: "all_turns"`
- **THEN** the Copilot request contains the same field and value
- **AND** the bridge does not silently fall back to the backend's `current_turn` default.

#### Scenario: Explicit null reasoning context remains present
- **WHEN** the client sends `reasoning.context: null`
- **THEN** the Copilot request still contains the `context` property with JSON null
- **AND** the bridge does not collapse explicit null into an absent property.

#### Scenario: Developer messages remain in conversation position
- **WHEN** the input contains developer or system messages before, between, or after conversation items
- **THEN** those input items remain at the same positions with the same roles, content, and valid metadata
- **AND** the bridge does not replace them with an earlier top-level `instructions` string when the destination is Responses.

#### Scenario: Modeled message metadata survives
- **WHEN** a message item contains a valid Responses `id`, `phase`, `status`, or a future sibling field
- **THEN** the Responses destination re-emits every present field with the same JSON value.

#### Scenario: Native function output retains structured shape
- **WHEN** a native Codex `function_call_output.output` is a string, object, or content-item array
- **THEN** the Responses destination emits the same JSON kind and value
- **AND** it does not flatten text arrays or inject JSON wrappers.

#### Scenario: Complete reasoning item survives
- **WHEN** Codex echoes a reasoning item containing encrypted content and any present `id`, `summary`, `content`, or future sibling field
- **THEN** T1/T2 re-emits every present field with the same JSON value and at the same conversation position.

#### Scenario: Future field on a known item survives
- **WHEN** a future Codex version adds an unmodeled sibling field to `message`, `function_call`, `function_call_output`, or `reasoning`
- **THEN** the field traverses the IR opaquely and reaches the Responses destination unchanged instead of being silently discarded.

### Requirement: Provider data crosses the IR through source-push and destination-pull

The Codex Responses source adapter SHALL push provider-native request data into provider-scoped IR extensions while also producing the semantic IR projection required by shared stages. It MUST NOT inspect, branch on, or otherwise know the selected destination. The Responses destination SHALL pull Responses-native data from those IR extensions and MUST NOT inspect, branch on, or otherwise know which client/source produced the IR.

Provider-native carriage SHALL be inert for destinations that do not understand that provider namespace. A destination MUST NOT infer source identity from the presence or absence of a carrier, and a source MUST NOT encode destination-specific policy into a carrier.

#### Scenario: Codex source is destination-agnostic
- **WHEN** the Codex inbound adapter converts a request containing Responses-only fields
- **THEN** it records their values and source positions under the OpenAI/Responses provider extension namespace
- **AND** its output is identical regardless of the route that will later be selected.

#### Scenario: Responses destination is source-agnostic
- **WHEN** T2 receives an IR request carrying valid OpenAI/Responses provider extensions
- **THEN** it restores those values according to the Responses destination contract
- **AND** it does not test whether the originating client was Codex, Claude Code, or a fixture.

#### Scenario: Unrelated destination ignores the provider carrier
- **WHEN** an IR request carrying OpenAI/Responses extensions is handled by a non-Responses destination
- **THEN** that destination neither emits the private carrier nor changes its normal wire behavior because of it.

### Requirement: Destination mutations are explicit narrow and live-grounded

Any departure from original native Codex request JSON SHALL be owned by the Responses destination or by an explicit routing/request-stage decision. Each mutation SHALL name the affected field or array element, preserve every unrelated value, be grounded in a current live backend rejection or an operator-configured route, and be observable in request diagnostics. An unclassified difference SHALL fail a contract test.

Authorized classes include configured model/effort routing, profile-derived effort coercion, backend-rejected fields or tool variants, vision signaling derived from retained images, and protocol validation of provider-native identities. The implementation MUST NOT use this allowance to discard an entire modeled object when only one field is invalid.

#### Scenario: Valid message identity is preserved
- **WHEN** a native message id satisfies the Responses destination contract, including a `msg`-prefixed id accepted by Copilot
- **THEN** T2 preserves that id unchanged.

#### Scenario: Rejected message identity is removed narrowly
- **WHEN** a message carries an id such as `item_0` that the live Responses backend rejects because it is not a valid message id
- **THEN** the Responses destination omits only that id
- **AND** preserves the message role, phase, status, content, position, and every unrelated field
- **AND** records the coercion in request diagnostics.

#### Scenario: Model routing patches only routed fields
- **WHEN** a location changes the requested model or maps effort for the target
- **THEN** the destination request reflects those configured values
- **AND** every unrelated original request value remains identical.

#### Scenario: Profile rejection cannot be restored by opaque carriage
- **WHEN** a live-grounded target profile removes a rejected field or tool variant
- **THEN** the provider carrier does not restore the rejected value after the profile decision
- **AND** the request diagnostics identify the exact mutation.

### Requirement: Native request carriage is isolated from cross-protocol translation

Native Codex request carriers SHALL affect only a Responses destination that understands the OpenAI/Responses provider-extension namespace. Claude Code→Responses requests SHALL continue to be generated from Anthropic semantic IR according to their own translation contracts and MUST NOT acquire stale Codex message identities, input positions, developer items, or opaque request fields.

#### Scenario: Claude request has no native Codex restoration
- **WHEN** Claude Code traffic is routed to `gpt-5.6-sol`
- **THEN** T2 emits the defined Anthropic→Responses translation
- **AND** no native-Codex request carrier or source-only metadata appears on the wire.

#### Scenario: Native Codex tool output is not treated as Claude content
- **WHEN** a native Codex function output contains an array that resembles Anthropic content blocks
- **THEN** it remains an opaque native Responses value
- **AND** Claude-specific image/text translation rules are not applied to it.

### Requirement: Contract corpus and real client prove request fidelity

Acceptance SHALL include contract-first unit tests, mutation checks, production-capture replay, paired direct-Copilot probes for every retained or removed silent rewrite, and a real headless Codex multi-turn tool task. The corpus oracle SHALL start from the independent client inbound body and an explicit reviewed mutation allowlist; it MUST NOT treat an already-transformed `upstream-req` capture as the expected request contract.

The real-client verdict SHALL require a function call and a custom tool call with matching client-returned outputs, completed final output, no execution abort, and zero router/dispatch fatal rows in Codex's own log. Bridge HTTP 200 alone is insufficient.

#### Scenario: Production corpus exposes undeclared loss
- **WHEN** the captured 233-turn gpt-5.6 corpus is replayed through current T1/T2
- **THEN** every inbound-to-outbound difference matches one explicit destination mutation
- **AND** removing the `reasoning.context`, message metadata, developer-position, or structured-output preservation code makes the corpus test fail.

#### Scenario: Every silent rewrite has paired backend evidence
- **WHEN** the destination drops, clamps, flattens, or coerces a native field
- **THEN** an ApiContract probe replays real captured client bytes with only that axis changed and proves the unmodified value is rejected by the current backend
- **AND** a catalog-versus-live check detects when that rejection disappears.

#### Scenario: Real Codex completes both tool paths
- **WHEN** real Codex 0.147.0-alpha.1.2 runs the xhigh reasoning fidelity case through a non-8765 bridge subprocess on `gpt-5.6-sol`
- **THEN** a namespaced function call and custom `exec` call both complete their call/output round trips
- **AND** the final canary is present with no abort, incompatible payload, missing namespace, polymorphism, or router fatal
- **AND** the trace satisfies the request mutation allowlist and response event-fidelity contract.

### Requirement: Standalone named function outputs preserve tool-tier authority

For a native Codex Responses request, the bridge SHALL accept both valid
`function_call_output` variants defined by Codex 0.153.3: a paired output carrying a
non-empty `call_id`, and a standalone external-tool output that omits `call_id` while
carrying a non-empty `name` and optional `namespace`. The bridge MUST preserve the
standalone item's absent `call_id`, name, namespace, output JSON kind and value,
optional identity, future sibling fields, and position when the selected destination
is Copilot Responses. It MUST NOT invent a call id or preceding tool call, lower the
item to an ordinary user/developer message, or silently drop it.

The Codex source adapter SHALL carry a standalone output through the OpenAI provider
extension namespace without requiring the frozen semantic IR to represent a fictitious
paired tool result. The Responses destination SHALL restore it according to the
existing source-push/destination-pull contract. An item with neither a usable
`call_id` nor a non-empty name SHALL fail with an actionable client error before
upstream I/O.

#### Scenario: Paired output retains existing behavior

- **WHEN** `function_call_output` contains a non-empty `call_id`
- **THEN** T1 translates it through the existing semantic tool-result path
- **AND** T2 preserves its call id, output value, metadata, and input position.

#### Scenario: Standalone named output round-trips without a call id

- **WHEN** Codex sends `function_call_output` with no `call_id`, a non-empty name,
  optional namespace, and an output value
- **THEN** the bridge accepts the request instead of returning a required-property 400
- **AND** Copilot receives the item at the same input position with `call_id` still
  absent and every other JSON value unchanged.

#### Scenario: Standalone output is not downgraded to a message

- **WHEN** an app-server heartbeat or other `thread/inject_items` event sends a
  standalone named function output
- **THEN** the Copilot request retains a `function_call_output` item
- **AND** no synthesized user/developer message or fictitious function call replaces it.

#### Scenario: Nameless unpaired output is rejected

- **WHEN** a `function_call_output` has neither a non-empty `call_id` nor a non-empty
  `name`
- **THEN** the bridge returns an actionable invalid-request response before contacting
  Copilot.

#### Scenario: Captured Desktop history remains processable

- **WHEN** a Codex 0.153.3 request contains one or more persisted standalone named
  outputs followed by a new user turn or compaction request
- **THEN** deserialization and T1/T2 complete without a missing-`call_id` failure
- **AND** all standalone items retain their relative order and values.

### Requirement: Standalone output behavior is proven at both backend and client edges

Acceptance SHALL include a direct per-model Copilot probe using the standalone named
output shape and a real Codex app-server run that injects such an item before a
multi-step tool turn on a supporting target. A bridge HTTP 200 alone MUST NOT be
treated as client success. A target that rejects the native shape MUST NOT cause the
bridge to invent a call id, preceding tool call, message, or model substitution.

#### Scenario: Supporting Copilot target accepts the native standalone shape

- **WHEN** a minimal and a captured-byte-derived standalone named output request are
  sent to a live-proven supporting Copilot Responses target
- **THEN** both complete without a schema rejection
- **AND** the trace proves the output was retained rather than normalized away.

#### Scenario: Rejecting target does not trigger a fabricated call

- **WHEN** the selected Copilot model rejects a standalone named output because it
  requires a paired `call_id`
- **THEN** the bridge preserves the requested model and original input rather than
  inventing an id, tool call, message conversion, or hidden model route
- **AND** the backend rejection remains visible to the client.

#### Scenario: Real Codex continues after an injected output

- **WHEN** real current Codex app-server injects a standalone named output and starts
  a tool-using turn through a bridge subprocess targeting `gpt-5.6-sol-fast`
- **THEN** the client consumes the response, executes a real tool, returns its matching
  output, and completes with the expected canary
- **AND** Codex's own dispatch log contains no missing-property, incompatible-payload,
  namespace, polymorphism, abort, or router fatal for the run.
