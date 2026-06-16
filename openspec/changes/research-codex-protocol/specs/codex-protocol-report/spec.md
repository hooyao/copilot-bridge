## ADDED Requirements

### Requirement: Synthesize Tracks A and B into an implementation-shape recommendation

The research SHALL produce a report that intersects the backend wire-truth (Track A) with the client behavior (Track B) and states, with evidence, the recommended implementation shape: whether the bridge can forward Codex's Responses body to Copilot's `/responses` unchanged, where transformation/coercion is required, and which `/codex/...` sub-routes are needed. Every recommendation MUST cite the Track A and/or Track B finding that justifies it.

#### Scenario: Intersection yields an evidence-backed recommendation

- **WHEN** the report compares what Codex sends (Track B) against what Copilot accepts (Track A)
- **THEN** it states, per field/tool/effort/route, whether it passes through, needs coercion, or needs translation — each with the citing finding

#### Scenario: No unjustified claims

- **WHEN** the report makes any implementation-shape claim
- **THEN** that claim is traceable to a recorded probe result or source/capture observation, with no assertion left ungrounded

### Requirement: Scope the follow-up implementation change

The report SHALL enumerate the concrete work items the implementation requires, sized to the findings, so the follow-up change's tasks reflect actual scope rather than a guess. Items the findings prove unnecessary MUST be explicitly excluded.

#### Scenario: Work items are derived from findings

- **WHEN** the research is complete
- **THEN** the report lists the implementation work items (DTOs, endpoint routes, coercions, header set, streaming handling, harness) each tied to a finding, and names anything the findings rule out

### Requirement: Correct the pipeline-design assumption

The report SHALL record whether `docs/pipeline-design.md` §3's assumption — that the OpenAI/Codex client requires OpenAI-Chat↔IR translation — holds against the evidence, and update §3 to the corrected framing.

#### Scenario: §3 reconciled with evidence

- **WHEN** the research establishes Codex's wire protocol and Copilot's `/responses` behavior
- **THEN** `docs/pipeline-design.md` §3 is updated to reflect the evidence, with the stale assumption explicitly flagged

### Requirement: Findings are durable and cited

The report SHALL live in `docs/` (extending `copilot-api-research.md` and/or a new `docs/codex-protocol-research.md`), with the Codex CLI version and probe run context stamped, so the facts remain auditable as Codex (alpha) and Copilot evolve.

#### Scenario: Report is auditable

- **WHEN** a reader later questions a recommendation
- **THEN** the report provides the citing source `file:line`, request id / probe output, and the Codex version under which it was observed
