# Design

## Context

See proposal.md for the production failure. The catalog projector already validates coherent total/prompt/output capacities and applies the lower of 85% total and 97.5% prompt, rounded down to 1,000. It also projects invariant routing targets and preserves a reviewed baseline when backend limits are unavailable.

## Goals / Non-Goals

**Goals:** Reserve space for one response to return as history without changing actual capacity or model visibility. Preserve existing behavior when that full reserve is not representable.

**Non-Goals:** Reasoning-output guards, failed-stream history isolation, fabricated usage, new inference fields, client configuration overrides, or deployment of the user's bridge.

## Decisions

- Add a third ceiling: `maxPrompt - maxOutput - 16_000`. The 16,000 tokens are an explicit bridge policy margin, not an asserted Copilot capability. Keep both existing percentage ceilings and whole-thousand flooring; use 64-bit arithmetic throughout.
- Apply the new ceiling only when it permits at least 1,000 tokens. Otherwise retain the existing threshold, rather than producing a non-positive value, hiding a valid alias, or pretending a full output reserve is available. Missing or incoherent capacities retain the existing reviewed fallback behavior.
- Use resolved-target capacities for aliases. Keep `context_window` and `max_context_window` equal to validated total capacity and leave the official captured resources unchanged.
- Verify actual catalog-driven automatic compaction with a real Codex app-server: seed authoritative usage above the new threshold but below the old threshold, fetch the bridge catalog without local context/compaction overrides, then observe compaction and a custom-exec tool round trip. A deterministic upstream controls usage only; a separate live-Copilot multi-tool run guards the real backend leg. Inspect the client's isolated SQLite dispatch log for both.

## Risks / Trade-offs

- Earlier compaction increases summary calls and can discard detailed history sooner. The reserve is derived from each model's published maximum output rather than a fixed 128,000-token assumption.
- An output that exceeds backend metadata, missing usage, and an already oversized session remain outside this prevention-only change.
- Tiny or output-heavy contexts cannot satisfy the full reserve. Preserve the existing positive threshold and document this limit explicitly.
- Codex may cache a model resource for a running turn. Deployment verification must confirm the new catalog was fetched; changing the bridge does not rewind an existing client's history.

## Migration Plan

Build and verify in an isolated subprocess. An eventual deployment requires restarting the bridge and refreshing Codex catalog state; rollback restores the previous projector. Do not change global client settings or replace the running installation as part of this implementation.
