## ADDED Requirements

### Requirement: Astra is an exact Copilot Responses target

The bridge SHALL resolve canonical `gpt-6-astra` to Copilot's native `/responses` endpoint through an explicit registry entry and an exact live-probed model profile. The profile SHALL accept only `low`, `medium`, `high`, `xhigh`, and `max` unchanged; SHALL use `low` when an unsupported effort must be coerced; SHALL retain custom grammar tools; and SHALL enable structured multimodal function output. The catalog-wide removals for `store:true`, `service_tier`, and image-generation tools SHALL apply to Astra because current live Copilot rejects those shapes.

#### Scenario: Direct Astra request resolves to Responses

- **WHEN** a request names `gpt-6-astra`
- **THEN** the bridge resolves `CopilotResponses:/responses` with an exact Astra profile rather than fuzzy borrowing or Chat Completions fallback

#### Scenario: Astra capability facts remain live-guarded

- **WHEN** the Responses contract sweep probes Astra's effort, field, tool, and structured multimodal-output axes
- **THEN** the committed snapshot and catalog comparison cover every rewrite-driving fact in both directions

### Requirement: GPT-5.6 Sol routes to Astra with explicit effort migration

The stock configuration SHALL contain an active exact-model Location that rewrites `gpt-5.6-sol` to `gpt-6-astra`. The rule SHALL map `none` and `minimal` to `low`; SHALL preserve `low`, `medium`, `high`, `xhigh`, and `max`; and SHALL NOT match `gpt-5.6-luna`, `gpt-5.6-terra`, or `gpt-5.6-sol-fast`. Config migration SHALL continue treating the complete Locations array as user-owned, so an existing installation's array is not overwritten by the new template.

#### Scenario: Flagship client identity uses Astra

- **WHEN** a Codex request names `gpt-5.6-sol`
- **THEN** the upstream request names `gpt-6-astra` on `/responses`
- **AND** the client-facing response retains the originally requested `gpt-5.6-sol` identity

#### Scenario: Unsupported source effort becomes low

- **WHEN** routed `gpt-5.6-sol` traffic carries `none` or `minimal`
- **THEN** the upstream Astra request carries `reasoning.effort: "low"` and does not receive Astra's live 400 rejection

#### Scenario: Other GPT-5.6 tiers are not collapsed

- **WHEN** a request names Luna, Terra, or Sol Fast
- **THEN** this Location does not match and their existing direct target behavior remains unchanged

### Requirement: Client catalog limits follow the resolved route target

When a Codex catalog source slug resolves through a configured model-only route, the bridge SHALL retain the source slug and official client instructions while deriving context-window and auto-compaction limits from the resolved live Copilot target. When the live overlay is validated, the routed source SHALL remain effective only when the source and resolved target both have exact bridge profiles and the target is currently advertised for `/responses`; an unavailable overlay SHALL retain the existing reviewed-baseline fallback behavior.

#### Scenario: GPT-5.6 Sol alias receives Astra limits

- **WHEN** the active Location maps `gpt-5.6-sol` to live `gpt-6-astra`
- **THEN** `/codex/models` keeps the `gpt-5.6-sol` entry and its client-owned instructions
- **AND** its projected context and compaction limits are computed from Copilot Astra's 1,000,000-token context and 872,000-token prompt ceiling

#### Scenario: Missing target hides an unsafe alias

- **WHEN** a configured target has no exact bridge profile or validated live `/responses` capability
- **THEN** the routed source is not advertised as an effective Codex model

### Requirement: Existing Codex Responses shapes remain native across the route

The bridge SHALL preserve the existing native Responses request items, tool declarations, response items, and SSE event families across the `gpt-5.6-sol` to Astra route except for the declared model and effort mutations. It MUST NOT invent an Astra-only translator when the live backend accepts the current Codex shape. Optional `async:true` tool metadata and the resulting `async` call-item field SHALL remain opaque native data when present.

#### Scenario: Real GPT-5.6 Codex request is accepted by Astra

- **WHEN** a captured real Codex 0.153.3 GPT-5.6 request changes only its model to `gpt-6-astra`
- **THEN** live Copilot accepts its `additional_tools`, function/custom tools, messages, metadata, and streaming shape

#### Scenario: Custom tool events preserve their established grammar

- **WHEN** Astra emits a synchronous or async custom grammar tool call
- **THEN** the response uses the native `custom_tool_call` item and `response.custom_tool_call_input.*` SSE family with all unknown fields preserved

### Requirement: Real Codex decides route acceptance

Acceptance SHALL drive real Codex through a non-8765 bridge subprocess on a complex multi-step tool task that resolves `gpt-5.6-sol` to Astra. The verdict SHALL require a matching tool call/output round-trip in that run's trace, completed canary output with no abort, and zero router or dispatch fatal rows in the exact manifest-selected Codex log. An upstream 200, unit test, or synthetic replay alone SHALL NOT constitute acceptance.

#### Scenario: Routed complex tool loop completes

- **WHEN** real Codex executes the route-specific behavior case
- **THEN** the trace proves `gpt-6-astra` on `/responses` and a matching tool call/output round-trip
- **AND** Codex's own stdout and SQLite dispatch evidence satisfy the real-client PASS rubric
