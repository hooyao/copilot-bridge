## ADDED Requirements

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
