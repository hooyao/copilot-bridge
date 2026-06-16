## ADDED Requirements

### Requirement: Per-model effort coercion grounded in the live snapshot

The bridge SHALL coerce a Codex request's `reasoning.effort` to a value the target model accepts, per a table-driven per-model profile sourced row-by-row from the live Responses contract snapshot (change 2), never family-name extrapolated. There are two inverted profiles: "large" models (`gpt-5.3-codex`, `gpt-5.4-mini`, `gpt-5.4`, `gpt-5.5`) reject `minimal`; "small" models (`gpt-5-mini`, `mai-code-1-flash-internal`) reject `none` and `xhigh`.

#### Scenario: minimal coerced for a large model

- **WHEN** a request targets a large-profile model with `reasoning.effort = "minimal"`
- **THEN** the bridge maps it to an accepted value (e.g. `low`) so Copilot does not 400

#### Scenario: none/xhigh coerced for a small model

- **WHEN** a request targets a small-profile model with `reasoning.effort = "none"` or `"xhigh"`
- **THEN** the bridge drops `none` and clamps `xhigh` to `high`

#### Scenario: Profile entries trace to the snapshot

- **WHEN** a profile asserts an accepted/rejected effort
- **THEN** that fact corresponds to a row in the live Responses contract snapshot, not a guess from the model family

### Requirement: Uniform field/tool coercions

The bridge SHALL strip `service_tier` and drop `tools[].type == "image_generation"` (both uniformly rejected per the snapshot). `store` SHALL be stripped only when `true` (Codex sends `false`). Tools Copilot accepts — `function`, `custom`/`apply_patch`, `web_search`, `namespace`, `tool_search` — SHALL pass through unchanged, with no custom→function rewrite.

#### Scenario: service_tier and image_generation removed

- **WHEN** a request includes `service_tier` and an `image_generation` tool
- **THEN** the bridge removes both before forwarding

#### Scenario: apply_patch passes through unrewritten

- **WHEN** a request includes a `custom`-typed `apply_patch` tool
- **THEN** the bridge forwards it as-is (no rewrite to `function`)

### Requirement: Coercions validated against the live probe, not a frozen golden

Each coercion SHALL be justified by an assertion against the live Responses probe result (B3): the bridge strips/clamps a value BECAUSE the live probe still shows it rejected. If the live contract drifts (change 2's B2 fires), the coercion's justification fails and must be reconciled.

#### Scenario: B3 ties a coercion to live behavior

- **WHEN** the coercion "strip `service_tier`" is tested
- **THEN** a live assertion confirms `service_tier` is still rejected by Copilot `/responses`; if it is now accepted, the test fails, signalling the coercion should be removed

### Requirement: Per-model tool capability flag

The catalog SHALL record that `mai-code-1-flash-internal` returns a server-side 500 on custom/`apply_patch` tools, so the bridge can flag rather than silently fail on that model. Unknown models SHALL produce a clear error (mirroring `UnknownModelException`), not a silent forward.

#### Scenario: Unknown model rejected clearly

- **WHEN** a Codex request names a model with no profile
- **THEN** the bridge returns a clear error naming the unknown model, not an opaque upstream failure

#### Scenario: Flash custom-tool limitation recorded

- **WHEN** `mai-code-1-flash-internal` is targeted with a custom tool
- **THEN** the catalog's flag for that model is available to surface the known 500 limitation rather than presenting it as a bridge bug
