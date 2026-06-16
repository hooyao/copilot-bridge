## Context

Change 2 of 3. Design: **[`docs/ir-definition-design.md`](../../../docs/ir-definition-design.md)
§7.0 / §7.B** (the test-philosophy split and the per-backend drift-detection
design, approved). Depends on change 1 (`freeze-ir-provider-extensions`, which
established the offline A-invariant framework). Feeds change 3
(`add-codex-responses-client`) — the Responses snapshot recorded here is the
wire-truth change 3's Codex profile catalog is built against.

## Goals / Non-Goals

**Goals:** make the live Copilot backends the ground truth for "what's accepted
now"; promote both probes (`ModelProfileProbe`, `ResponsesProbe`) from print →
assert (B1); commit a per-backend contract snapshot (B1); add drift detection
that fails with a diff when either backend moves (B2); tie `ModelProfileCatalog`
to the live Anthropic probe (B3).

**Non-Goals:** no IR changes (change 1); no Codex endpoint, T1–T4 translators, or
Codex profile catalog (change 3). The Responses snapshot is *recorded* here; the
catalog/coercions that consume it are change 3. No offline/invariant tests here —
this change is the live "B" half only.

## Decisions

Settled with the user; rationale in `docs/ir-definition-design.md` §7.B:

- **Captured traces are reference, not ground truth.** The only oracle for "what
  Copilot accepts now" is the live backend, re-derived every run. Snapshots
  record a moment; B2 alarms when reality moves past it.
- **Drift detection is per-backend, not per-client** — Copilot is the single
  evolving upstream behind both clients. The Anthropic hot path gets its **first**
  drift alarm here (it had none), because leaving it unguarded while guarding
  Codex would be the wrong asymmetry (the user's explicit ask: "anthropic 是不是
  也需要一个 contract snapshot?" → yes).
- **Snapshots are committed JSON under `docs/`**, version/date-stamped, updated by
  a one-liner when a reviewed drift is accepted.
- **B-tests are Integration-tagged**, run on demand/schedule, never bind 8765.

## Risks / Trade-offs

- **[Snapshot churn / flaky drift]** → assert only stable, decision-relevant
  facts (acceptance booleans, event-type sets), not volatile fields (token
  counts, ids, latencies). The snapshot diff compares the *contract*, not the
  full response.
- **[B3 reveals the catalog is already stale]** → that's a feature: it surfaces a
  pre-existing drift as a finding to reconcile, not a test to suppress.
- **[Quota cost of asserting probes]** → same single-axis, minimal-payload
  discipline as the existing probes; Integration-tagged so they run deliberately,
  not in CI.
- **[Snapshot drift between this change and change 3]** → the Responses snapshot
  is timestamped; change 3 reconciles its catalog against the then-current
  snapshot (and its own B-run), so a gap is visible, not silent.
