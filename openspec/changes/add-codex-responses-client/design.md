## Context

Change 3 of 3 — the change that actually serves Codex. Designs:
**[`docs/codex-implementation-design.md`](../../../docs/codex-implementation-design.md)**
(the T1–T4 + endpoint design, v2 hub-IR-corrected),
**[`docs/ir-definition-design.md`](../../../docs/ir-definition-design.md)** (§4
T1–T4 mapping table, §7 validation), and
**[`docs/codex-protocol-research.md`](../../../docs/codex-protocol-research.md)**
(verified Codex/Copilot wire facts). Depends on change 1 (frozen IR +
`ProviderExtensions` + A-invariant framework) and change 2 (Responses contract
snapshot + drift detection the coercions are validated against).

## Goals / Non-Goals

**Goals:** ship `POST /codex/responses`; the four translators T1–T4 routing Codex
through the shared Anthropic-shape IR pipeline; the per-model Codex profile
catalog (grounded in change 2's snapshot); fully-typed Responses DTOs; official
Copilot header fidelity; exhaustive A-invariant + B3 + hot-path + live `codex.exe`
validation on real data. Keep `/cc` byte-identical; keep AOT.

**Non-Goals:** Gemini; the Codex→Opus and ClaudeCode→GPT5 cells (architecture-ready
via the routing seam but not wired/tested here — only Codex→`/responses` ships);
`/responses/compact` + `/memories/*`; WebSocket transport.

## Decisions

Settled with the user; full rationale in the three design docs:

- **Hub-IR, routing on the IR by model.** Codex transits the frozen Anthropic IR
  and routes by resolved model id — not a Responses-passthrough. The driver is
  cross-model substitution (needs a backend-agnostic body to route on). The
  double-translation cost (`Responses →T1→ IR →T2→ Responses`) is deliberate and
  guarded by the round-trip tests.
- **T1/T4 are real translators**, not identity (Q7 dissolved). T2/T3 live inside
  `CopilotResponsesStrategy` per pipeline-design §4.
- **Reuse the existing `CopilotResponses` enum value** (Q4 — it exists, just wire
  `Resolve` to it; no new enum).
- **Fully-typed Responses DTOs** (Q2 — no `JsonElement` passthrough; T1/T2 rewrite
  the body and AOT needs source-gen).
- **Reference is reference-only** (Q-A) — the `responses-translation.ts` mapping
  is prior art; every T1–T4 rule ships with a capture/probe-backed test + the
  live `codex.exe` round-trip.
- Smaller settled items: Q1 don't touch `text.verbosity`; Q3 strip `store` only
  when `true`; Q5 verify `Normalize` no-ops on `gpt-5.3-codex`; Q6 no
  `/codex/models`.

## Risks / Trade-offs

- **[Double-translation loses Responses-specific fidelity]** → the entire §7
  validation exists for this: A1/A2/A6 byte-passthrough + round-trip on real
  captures, and E1 live `codex.exe` with a post-hoc audit diff. A lossy field is
  fixed before ship, not hand-waved.
- **[Coercions drift from Copilot reality]** → B3 ties each coercion to the live
  probe; change 2's B2 alarms when the snapshot drifts.
- **[Registry addition perturbs the hot path]** → H1 byte-equality on real
  `cc-request` fixtures before/after registering the Codex strategy is the gate.
- **[codex-cli is alpha (0.140)]** → DTOs/profiles are capture/snapshot-grounded
  and version-stamped; E1 re-verifies end-to-end; the reference is not trusted
  blind.
- **[AOT binary growth from Responses DTOs]** → source-gen only; eyeball size
  (`docs/size-history.md`).
- **[`mai-code-1-flash-internal` 500 on custom tools]** → recorded as a profile
  flag, not silently surfaced as a bridge bug.
