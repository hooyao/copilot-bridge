## Why

Codex treats GitHub Copilot models used through the bridge as ordinary custom-provider models, so it never discovers their live metadata and clamps `model_context_window` to the bundled 272,000-token ceiling. GitHub Copilot now advertises 1,050,000-token context windows for several Responses models; the bridge should expose those backend facts in Codex's native model-catalog shape so Codex can use the capacity it is already entitled to.

## What Changes

- Add `GET /codex/models?client_version=...`, returning a Codex-compatible model catalog rather than GitHub Copilot's incompatible `/models` payload.
- Seed each returned entry from the Codex catalog appropriate for the requesting client version, preserving Codex-owned behavior such as instructions, tool mode, picker metadata, reasoning levels, and `auto_review_model_override`.
- Overlay only live, relevant GitHub Copilot facts—including supported Responses model membership and safe context/compaction limits—onto matching Codex entries.
- Make `config codex` opt the bridge provider into Codex's remote-model discovery using command-backed authentication, without exposing the user's GitHub or Copilot token.
- Cache and refresh upstream model metadata without turning a transient GitHub Copilot `/models` failure into a Codex startup failure; serve a safe bundled fallback when live data is unavailable.
- Add contract tests for the Codex catalog schema, version routing, limit mapping, fallback behavior, configuration fidelity, and Native AOT serialization, followed by a real `codex.exe` long-context run whose client-side evidence confirms the larger window is active.

## Capabilities

### New Capabilities

- `codex-model-catalog`: Codex-compatible model discovery, client-version-aware catalog construction, GitHub Copilot capability overlays, caching, and failure behavior at `GET /codex/models`.

### Modified Capabilities

- `client-autoconfiguration`: `config codex` additionally configures command-backed provider authentication so Codex requests the bridge's remote model catalog, while preserving unrelated user configuration and never disclosing the real Copilot credential.

## Impact

- **HTTP surface:** new `GET /codex/models` endpoint under the existing Codex prefix.
- **Codex configuration:** the managed `[model_providers.copilot-bridge]` block gains an `auth` subtable; status and drift reporting must understand it.
- **Model metadata:** new source-generated JSON DTOs/catalog assets and a projection layer joining Codex client metadata with GitHub Copilot `/models` limits.
- **Authentication:** a small noninteractive CLI command supplies a non-secret bridge-local bearer value required only to activate Codex discovery; bridge request authentication remains unchanged.
- **Runtime:** live Copilot model discovery, bounded caching, fail-open fallback, and optional catalog ETag support.
- **Documentation/testing:** architecture, Codex setup, context-window guidance, unit/API-contract tests, and real-client behavior verification.
