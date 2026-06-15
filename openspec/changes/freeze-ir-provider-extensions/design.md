## Context

Change 1 of 3 in the Codex-support effort. The full, reviewed-and-approved design
is **[`docs/ir-definition-design.md`](../../../docs/ir-definition-design.md)** —
this file is a thin pointer so the OpenSpec change validates; the reasoning,
diagrams, research basis, the per-field fidelity bar (§7.1), and the A/B test
philosophy (§7.0) live there. This change implements only the **A (invariant) +
escape-hatch + freeze** portion; the live-contract/drift work (B) is change 2 and
the Codex client (T1–T4) is change 3.

## Goals / Non-Goals

**Goals:** add the `ProviderExtensions` namespaced escape-hatch (request + part
level, AOT-safe `JsonElement` bag); freeze the IR contract per
`docs/ir-definition-design.md` §6; build the A-invariant test framework
(field-diff harness + A1–A4); prove the hot path is byte-identical (H1/H2).

**Non-Goals (changes 2 & 3):** no Codex endpoint, no `/responses`, no T1–T4
translators, no contract snapshots, no drift detection, no `ModelProfileProbe`
promotion. No live Copilot assertions of any kind here.

## Decisions

Settled with the user this session; full rationale in
`docs/ir-definition-design.md` §1–§3:

- **IR = our hand-rolled Anthropic Messages DTOs**, not MEAI, not a neutral IR.
  Research (MEAI/LiteLLM/Vercel) showed Anthropic-shape is most expressive and
  neutral IRs are lossy for Anthropic.
- **Escape hatch = Vercel's `providerOptions` pattern**, as `ProviderExtensions =
  Dictionary<string, JsonElement>` keyed by provider name, opaque, copied
  verbatim, source-gen serialized (no per-provider reflection DTO).
- **Captured traces are reference input samples, never ground truth**
  (`docs/ir-definition-design.md` §7.0). A-invariant goldens encode only our own
  transforms; nothing here asserts Copilot's current behavior.
- **Hot path is sacrosanct** — the bag is additive and inert when empty; `/cc`
  output must be byte-identical.

Open conventions (decided during implementation, low-stakes): explicit
`Dictionary<string,JsonElement>` property vs `[JsonExtensionData]` (lean:
explicit, source-gen-registered); whether `ProviderExtensions` lives under
`Models/Anthropic/Request/` or `Models/Common/` (lean: Common, since it's
IR-wide, not Anthropic-specific).

## Risks / Trade-offs

- **[Bag leaks into the hot path]** → H1 byte-equality test is the gate; if a CC
  request serializes differently, the bag isn't inert — that's a bug, not a
  tolerance.
- **[AOT regression from the bag]** → values are `JsonElement` (already AOT-clean
  in this codebase); register explicitly in `JsonContext`; eyeball binary size.
- **[Over-freezing the IR]** → the freeze is deliberately "body shape fixed,
  un-modeled fields go to the bag" — the bag is the pressure-release, so freezing
  the body doesn't block future providers.
- **[Fixtures treated as oracles]** → the spec + §7.0 explicitly scope them as
  input samples; the diff harness compares against our-transform goldens, not
  against a frozen Copilot response.
