## ADDED Requirements

### Requirement: Drift detection per backend

For each Copilot backend, the system SHALL compare the live probe result against the committed contract snapshot and FAIL with a readable diff on any difference. This applies symmetrically to both backends — drift's source is Copilot, which serves both clients, so neither backend may be left without a drift alarm.

#### Scenario: Drift fails with a diff

- **WHEN** a live probe result differs from its committed snapshot in any asserted fact (a newly accepted field, a newly rejected value, a new or renamed SSE event, a widened/narrowed effort set)
- **THEN** the drift test fails and reports the specific difference (e.g. "Copilot now ACCEPTS `service_tier`"), so the operator knows exactly what moved

#### Scenario: Both backends are guarded

- **WHEN** the drift suite runs
- **THEN** both `/v1/messages` and `/responses` are checked against their respective snapshots; neither backend is exempt

#### Scenario: No drift passes quietly

- **WHEN** the live backend matches its snapshot
- **THEN** the drift test passes with no diff

### Requirement: Drift is the signal, snapshot update is the response

When drift is detected, the workflow SHALL be: red test → one-line snapshot update → code review of whether the dependent wire-truth (`ModelProfileCatalog`, the Codex coercions added in a later change) must change. Drift MUST NOT be a silent breakage discovered later in production.

#### Scenario: A widening is caught automatically

- **WHEN** Copilot widens an acceptance (e.g. an effort value previously rejected is now accepted), reproducing the kind of 2026-06-05 episode that was previously caught only by luck
- **THEN** the drift test goes red automatically, prompting a snapshot update and a review of whether the catalog/coercions should change

### Requirement: Catalog validated against live, not a frozen golden

The Anthropic-side wire-truth the bridge bakes in (`ModelProfileCatalog`) SHALL be asserted against the **live** probe result, so the catalog's correctness is tied to current reality rather than a captured moment.

#### Scenario: Catalog claim matches live behavior

- **WHEN** `ModelProfileCatalog` asserts a per-model fact (e.g. "opus accepts only `medium` effort")
- **THEN** a test confirms that claim against the live `/v1/messages` probe result; if the live result disagrees, the test fails and names the catalog row to reconcile

#### Scenario: Live contract tests are integration-tagged and never bind the default port

- **WHEN** these live drift/contract tests run
- **THEN** they are tagged `[Trait("Category","Integration")]` (skipped in CI without Copilot creds) and never bind the default bridge port
