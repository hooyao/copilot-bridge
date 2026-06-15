## ADDED Requirements

### Requirement: Identify the Codex CLI API surface

The research SHALL establish which HTTP sub-paths the Codex CLI calls against its configured provider `base_url`, grounded in the open-source Codex Rust implementation and cross-checked against captured live traffic. The finding bounds the bridge's required `/codex/...` route surface.

#### Scenario: Sub-paths are enumerated from source and confirmed by capture

- **WHEN** the research reads the Codex request layer (e.g. `codex-rs/core/src/client.rs`, `codex-api/src/`) and captures a real `codex exec` run pointed at a capturing endpoint
- **THEN** the report lists every provider-relative path Codex calls, citing the source `file:line` and the corresponding captured request

### Requirement: Establish the Codex request body shape

The research SHALL document the exact request body the Codex CLI builds for a Responses-API turn — model id form, `input` structure, `reasoning` (effort/summary), `tools` (including `apply_patch`), `store`, caching fields, and streaming flag — grounded in source and confirmed against captured bytes.

#### Scenario: Request shape is documented and verified

- **WHEN** the research compares the Codex source request builder against a captured live request body
- **THEN** the report documents each significant field Codex emits, noting any divergence between source expectation and captured reality

#### Scenario: Model-id form is recorded

- **WHEN** Codex sends a model id
- **THEN** the report records the exact id form Codex emits, so a later change can reconcile it with Copilot's model ids

### Requirement: Establish Codex streaming expectations

The research SHALL document what SSE event sequence the Codex CLI expects back and how it parses the stream, so the eventual bridge response can be validated against the client's real tolerance (not assumptions).

#### Scenario: Client stream handling is documented

- **WHEN** the research reads the Codex stream-parsing code and observes a captured response stream
- **THEN** the report documents the events Codex consumes, which it requires, and any terminator handling, citing source and capture

### Requirement: Capture cross-check does not disturb the environment

The Track-B capture cross-check SHALL drive the real `codex.exe` without mutating the user's persistent Codex configuration, using per-invocation overrides and an ephemeral capturing endpoint on a non-default port.

#### Scenario: Capture run is hermetic

- **WHEN** the research captures a live Codex request
- **THEN** it points Codex at the capturing endpoint via per-invocation config override (not by editing `~/.codex/config.toml`), uses a non-8765 port, and leaves the user's Codex state unchanged
