## ADDED Requirements

### Requirement: Per-backend contract snapshots

The system SHALL maintain a committed contract snapshot for each Copilot backend the bridge uses — `/v1/messages` (Anthropic) and `/responses` (Responses) — recording the live wire-truth that backend exhibits, version- and date-stamped. The snapshots SHALL be produced from the asserting probes, not hand-written.

#### Scenario: Both backends have a snapshot

- **WHEN** the contract snapshots are generated
- **THEN** there is a committed `docs/copilot-anthropic-contract-snapshot.json` (from the `/v1/messages` probe) and a committed `docs/copilot-responses-contract-snapshot.json` (from the `/responses` probe), each stamped with the capture date

#### Scenario: Snapshot records the live wire facts, not assumptions

- **WHEN** a snapshot is produced
- **THEN** its contents come from actual live probe results (e.g. which effort values each model accepted/rejected, which fields/tools were rejected, the observed SSE event set) — never from `/models` capability metadata or family-name extrapolation

### Requirement: Probes assert live wire facts

Both wire probes SHALL be promoted from print-only to asserting the live facts of their backend. The `/v1/messages` probe asserts per-model effort/thinking/mid-conversation-system/beta acceptance; the `/responses` probe asserts per-model effort accept/reject, `service_tier`/`store`/`image_generation` rejection, and the live SSE event set.

#### Scenario: Anthropic probe asserts

- **WHEN** the `/v1/messages` probe runs against live Copilot
- **THEN** it asserts the per-model acceptance facts (not merely prints them) and produces the Anthropic snapshot

#### Scenario: Responses probe asserts

- **WHEN** the `/responses` probe runs against live Copilot
- **THEN** it asserts the per-model acceptance facts and produces the Responses snapshot
