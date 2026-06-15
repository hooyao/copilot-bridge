## ADDED Requirements

### Requirement: Discover Responses-capable models

The research SHALL establish, from the live Copilot account, the exact set of models whose `supported_endpoints` includes `/responses`, recording each model's raw `capabilities` block. The finding MUST be a recorded live observation, never an extrapolation from a model's family name.

#### Scenario: Model availability is recorded from live data

- **WHEN** the probe queries Copilot `GET /models` and inspects `supported_endpoints`
- **THEN** the research report lists every model id carrying `/responses`, with its `capabilities` block, and cites the run that produced it

### Requirement: Establish reasoning-effort acceptance per model

The research SHALL determine, per Responses-capable model, which `reasoning.effort` values Copilot's `/responses` accepts (HTTP 200) versus rejects (HTTP 400), across `minimal`, `low`, `medium`, `high`, `xhigh`, and the absent case. Any divergence between a model's `/models` capability metadata and its live behavior MUST be recorded as a finding.

#### Scenario: Per-model effort acceptance is recorded

- **WHEN** the probe sends a minimal `/responses` request for each (model, effort) pair
- **THEN** the report records the HTTP status and error/response preview per pair, distinguishing accepted from rejected values

#### Scenario: Metadata-vs-live mismatch is flagged

- **WHEN** capability metadata claims an effort value the live call rejects (or vice versa)
- **THEN** the report records the live behavior as authoritative and notes the discrepancy

### Requirement: Establish Responses-field acceptance

The research SHALL determine whether Copilot's `/responses` accepts the Responses-API-specific fields Codex may send — at minimum `reasoning.summary`, encrypted-reasoning echo, `store`, and `prompt_cache_*` — recording accept/reject per field with the verbatim rejection message when rejected.

#### Scenario: Field acceptance is recorded per field

- **WHEN** the probe sends `/responses` requests including each candidate field in isolation
- **THEN** the report records, per field, whether Copilot returned 200 or rejected it, with rejection text captured verbatim

### Requirement: Establish tool acceptance

The research SHALL determine how Copilot's `/responses` treats the tool types Codex emits — at minimum `function`, `apply_patch`, `web_search`, and `image_generation` — recording whether each is accepted, rejected, or requires rewriting.

#### Scenario: Tool acceptance is recorded per tool type

- **WHEN** the probe sends `/responses` requests carrying each tool type
- **THEN** the report records the acceptance result per tool type, sufficient for a later change to encode any required sanitization

### Requirement: Capture the /responses streaming event sequence

The research SHALL capture the actual SSE event sequence Copilot returns from a streaming `/responses` request — text deltas, reasoning/summary events, function-call argument deltas, terminal events, and any non-spec terminator (e.g. a trailing `[DONE]`) — recording the ordered event types and a representative payload for each.

#### Scenario: A streaming run is captured and documented

- **WHEN** the probe issues a streaming `/responses` request and reads it to completion
- **THEN** the report records the ordered list of event types with a representative payload each, and flags any event a client would need filtered or transformed

### Requirement: Probe harness isolation and safety

The probe harness SHALL reach Copilot directly through the existing playground client without requiring a `/codex` bridge endpoint, SHALL be excluded from the default CI run, and SHALL NOT bind the default bridge port.

#### Scenario: No endpoint dependency

- **WHEN** the probe runs
- **THEN** it issues requests via the playground client straight to Copilot `/responses` and depends on no bridge-side `/codex` route

#### Scenario: CI and port safety

- **WHEN** the test suite runs in CI
- **THEN** every probe test is tagged as an integration test so the default filter skips it, and no probe binds port 8765
