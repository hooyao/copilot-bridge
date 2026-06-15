# Implementation Tasks

> Change 2 of 3. Design: `docs/ir-definition-design.md` §7.B (approved). Scope:
> promote both probes to assert, snapshot both Copilot backends, add drift
> detection. All live tests `[Trait("Category","Integration")]`, never bind 8765.

## 1. Snapshot model + diff helper

- [x] 1.1 Define the contract-snapshot JSON shape: per-backend, per-model acceptance facts only (acceptance booleans, rejected values, SSE event-type set) — stable/decision-relevant fields, NOT volatile ones (ids, token counts, latencies). Capture date + account type stamped.
- [x] 1.2 Write a shared snapshot-diff helper that compares a live probe result against a committed snapshot and produces a readable diff (added/removed/changed facts), failing the test with the diff message.

## 2. Anthropic backend (/v1/messages) — promote + snapshot + drift

- [x] 2.1 B1: promote `ModelProfileProbe` from print-only to ASSERTING the live per-model facts (effort/thinking/mid-conv-system/beta acceptance across the 11 Claude models). Emit a structured result object (not just `_output.WriteLine`).
- [x] 2.2 Generate + commit `docs/copilot-anthropic-contract-snapshot.json` from a live run.
- [x] 2.3 B2: drift test — diff the live `ModelProfileProbe` result against the committed Anthropic snapshot; fail with a diff on any difference.
- [x] 2.4 B3: assert `ModelProfileCatalog` against the live probe result (each per-model claim — e.g. "opus accepts only medium" — confirmed live; mismatch fails naming the catalog row).

## 3. Responses backend (/responses) — promote + snapshot + drift

- [x] 3.1 B1: promote `ResponsesProbe` from print-only to ASSERTING the live facts: per-model effort accept/reject (6 models), `service_tier`/`store`/`image_generation` rejection, the live `/responses` SSE event set.
- [x] 3.2 Generate + commit `docs/copilot-responses-contract-snapshot.json` from a live run.
- [x] 3.3 B2: drift test — diff the live `ResponsesProbe` result against the committed Responses snapshot; fail with a diff on any difference.
- [x] 3.4 (No B3 here — the Responses-side catalog/coercions are change 3. This change only records the snapshot for change 3 to build against; note that explicitly in the snapshot file header.)

## 4. Verification

- [x] 4.1 Run both probe suites live once to seed the two snapshots; confirm B2 passes against the freshly-committed snapshots (no spurious drift).
- [x] 4.2 Negative check: hand-edit a snapshot fact, confirm the B2 drift test goes RED with a clear diff, then revert — proving the alarm actually fires.
- [x] 4.3 `dotnet test --filter "Category!=Integration"` unaffected/green (these B-tests are Integration-tagged and excluded from CI).
- [x] 4.4 Document the drift workflow (red → snapshot one-liner update → catalog/coercion review) in the snapshot file headers or a short `docs/` note.

## Outcome (as-built 2026-06-15)

- **Both snapshots seeded from live Enterprise Copilot** and committed:
  `docs/copilot-anthropic-contract-snapshot.json` (11 Claude models) and
  `docs/copilot-responses-contract-snapshot.json` (6 Codex models). Each backend
  sweep is one aggregate `[Fact]` (~90–100 live calls), not a per-cell theory, so
  the snapshot is built atomically and quota stays bounded.
- **B2 verified both ways**: clean run → green (no spurious drift on either
  backend); the negative check (4.2) tampered `gpt-5.5.effort` and B2 went RED
  with the exact diff (`ADDED models.gpt-5.5.effort.rejected[] (live lists
  "minimal"…)`), proving the alarm fires. Reverted after.
- **B3 confirms `ModelProfileCatalog` is currently accurate** — every per-model
  effort/thinking/mid-conv claim matched the live `/v1/messages` result.
- **Responses snapshot matches the research §2 facts exactly**: two inverted
  effort profiles (large reject `minimal`; small reject `none`+`xhigh`),
  `store_true`/`service_tier` 400, `image_generation` 400 (+ `custom_apply_patch`
  500 on `mai-code-1-flash-internal`), SSE event set with `<no-done-terminator>`.
  This is the wire-truth change 3's Codex catalog builds against.

### Real findings / robustness fixes (not in the original plan)

- **Mid-conv-system probe placement bug** (found while seeding): the first draft
  probed `U·A·S` (system after an assistant) — an ILLEGAL placement even on
  opus-4.8 — which wrongly recorded 4.8 as rejecting mid-conv system (would have
  failed B3). Fixed to the legal `U·S` placement; 4.8 now correctly reads `true`.
- **`ProbeRetry` transport resilience** (found when a single TLS read error
  aborted the whole 99-call sweep): transport exceptions (socket/TLS/timeout) now
  retry up to 3×; HTTP 4xx/5xx flow through unchanged (a status code is a
  contract fact, never retried). A 100-call live sweep can't be hostage to one
  network hiccup.
- Shared machinery: `Contract/ContractSnapshot.cs` (seed-or-diff + strict
  set-aware JSON diff, ignores only `_meta`), `Contract/WireAcceptance.cs`
  (2xx=accept / 4xx=reject / 5xx throws-or-records), `Contract/ProbeRetry.cs`.
  Workflow doc: `docs/contract-drift.md`.
