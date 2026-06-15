# Implementation Tasks

> Change 2 of 3. Design: `docs/ir-definition-design.md` §7.B (approved). Scope:
> promote both probes to assert, snapshot both Copilot backends, add drift
> detection. All live tests `[Trait("Category","Integration")]`, never bind 8765.

## 1. Snapshot model + diff helper

- [ ] 1.1 Define the contract-snapshot JSON shape: per-backend, per-model acceptance facts only (acceptance booleans, rejected values, SSE event-type set) — stable/decision-relevant fields, NOT volatile ones (ids, token counts, latencies). Capture date + account type stamped.
- [ ] 1.2 Write a shared snapshot-diff helper that compares a live probe result against a committed snapshot and produces a readable diff (added/removed/changed facts), failing the test with the diff message.

## 2. Anthropic backend (/v1/messages) — promote + snapshot + drift

- [ ] 2.1 B1: promote `ModelProfileProbe` from print-only to ASSERTING the live per-model facts (effort/thinking/mid-conv-system/beta acceptance across the 11 Claude models). Emit a structured result object (not just `_output.WriteLine`).
- [ ] 2.2 Generate + commit `docs/copilot-anthropic-contract-snapshot.json` from a live run.
- [ ] 2.3 B2: drift test — diff the live `ModelProfileProbe` result against the committed Anthropic snapshot; fail with a diff on any difference.
- [ ] 2.4 B3: assert `ModelProfileCatalog` against the live probe result (each per-model claim — e.g. "opus accepts only medium" — confirmed live; mismatch fails naming the catalog row).

## 3. Responses backend (/responses) — promote + snapshot + drift

- [ ] 3.1 B1: promote `ResponsesProbe` from print-only to ASSERTING the live facts: per-model effort accept/reject (6 models), `service_tier`/`store`/`image_generation` rejection, the live `/responses` SSE event set.
- [ ] 3.2 Generate + commit `docs/copilot-responses-contract-snapshot.json` from a live run.
- [ ] 3.3 B2: drift test — diff the live `ResponsesProbe` result against the committed Responses snapshot; fail with a diff on any difference.
- [ ] 3.4 (No B3 here — the Responses-side catalog/coercions are change 3. This change only records the snapshot for change 3 to build against; note that explicitly in the snapshot file header.)

## 4. Verification

- [ ] 4.1 Run both probe suites live once to seed the two snapshots; confirm B2 passes against the freshly-committed snapshots (no spurious drift).
- [ ] 4.2 Negative check: hand-edit a snapshot fact, confirm the B2 drift test goes RED with a clear diff, then revert — proving the alarm actually fires.
- [ ] 4.3 `dotnet test --filter "Category!=Integration"` unaffected/green (these B-tests are Integration-tagged and excluded from CI).
- [ ] 4.4 Document the drift workflow (red → snapshot one-liner update → catalog/coercion review) in the snapshot file headers or a short `docs/` note.
