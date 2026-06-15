## Context

This is a **research-only** change. Its output is facts + a report, not production code. It exists because we currently cannot honestly design Codex support — we have verified neither what the Codex CLI sends nor what Copilot's `/responses` accepts on this account. The Anthropic path set the bar: research and live verification came *before* design, which is why that path is complete. We copy that workflow.

The implementation (the `/codex` endpoint, DTOs, profile catalog, e2e harness) is **deliberately out of scope here** and will be a separate follow-up change whose design/specs/tasks are written *on top of this change's report* — sized to what the findings actually require, not to a guess.

Verified entry facts (this session):
- Codex CLI speaks the Responses API only (`wire_api=responses`; Chat removed Feb 2026); custom provider via `[model_providers.<id>]` (`base_url`, `env_key`→Bearer, `http_headers`, `query_params`); effort vocabulary `minimal|low|medium|high|xhigh`. Source: `developers.openai.com/codex/config-reference`.
- Copilot exposes a native `/responses` endpoint (`@vscode/copilot-api` `DomainService.capiResponsesURL`).
- **Codex is open source** — `github.com/openai/codex`, Rust under `codex-rs/` (`core/src/client.rs`, `client_common.rs`, `codex-api/src/`, `tools/src/responses_api.rs`). So client behavior can be read from source, then confirmed by capture — not inferred.
- `codex.exe` is installed locally (`0.140.0-alpha.2`, not on PATH); `codex exec --json` is the headless driver; the bridge already has a request-traces capture mode for the Track-B cross-check.

## Goals / Non-Goals

**Goals:**
- Produce evidence for **both** sides of the wire: Track A (Copilot `/responses` acceptance) and Track B (what Codex actually sends).
- Land Track A as a reusable probe harness (parallel to `ModelProfileProbe.cs`); land Track B as a source-read confirmed by a capture cross-check.
- Synthesize A ∩ B into a cited implementation-shape recommendation + a findings-sized work-item list for the follow-up change.
- Set up the missing references (`openai-sdk-pkg`, `codex` source) and correct `pipeline-design.md` §3.

**Non-Goals:**
- **Any implementation**: no `/codex` endpoint, DTOs, profile catalog, or production code. Those are the follow-up change.
- **Deciding passthrough vs translation before the evidence**: that verdict is the report's *output*.
- Estimating implementation effort now — the report produces that estimate.
- Gemini support; Codex features that don't route through the provider `base_url` (ChatGPT-account auth, Codex Cloud, internal MCP).

## Decisions

### D1 — One research change first; implementation is a separate change built on its report

The earlier draft of this work mixed research with implementation-shaped specs/tasks for the endpoint — committing to a shape with zero verified facts. That was the mistake this change corrects. **Why:** the implementation's true scope is unknowable until A ∩ B is established; writing its design now is 拍脑袋. The follow-up change reads this report and is sized to it.

### D2 — Two tracks, because neither alone determines the shape

```
 Track A (backend)                Track B (client)
 Copilot /responses accepts?      Codex actually sends?
   • models w/ /responses           • sub-paths called
   • effort 200/400 per model       • request body shape
   • reasoning/store/cache fields   • reasoning/tools/model-id
   • tool accept/reject/rewrite     • streaming expectations
   • SSE event sequence
          └──────────┬──────────┘
                     ▼
            A ∩ B ⇒ passthrough? coercions? routes?
```

A field Copilot accepts is irrelevant if Codex never sends it; a field Codex sends that Copilot rejects is exactly where work lives. Only the intersection is actionable.

### D3 — Track A probes Copilot directly via `PlaygroundClient`; no `/codex` endpoint

Same approach as `ModelProfileProbe.cs`: minimal requests straight to Copilot, single-axis matrix (not full cartesian — exhaustive products burn premium quota for low marginal info), log 200/400 + previews. Removes the circular dependency (endpoint would need probe results).

### D4 — Track B reads source first, then confirms with capture

Codex being open source makes the request builder readable directly (the parallel to the decompiled Claude Code source behind research §16). Source tells intent; a captured real `codex exec` request tells reality (alpha builds drift from docs). The bridge's existing request-traces mode captures the bytes; the capture run is hermetic (per-invocation `-c` override, ephemeral non-8765 port, no edit to `~/.codex/config.toml`).

### D5 — Two reference roles, mirroring the Anthropic split

Type source = official `openai` SDK (`references/openai-sdk-pkg/`, parallel to `anthropic-sdk-pkg/`). Behavior/source = `references/codex/` Rust + `copilot-api-anthropic`'s `responses/` handler. `agent-maestro` is reference-only and lower quality — not load-bearing.

## Risks / Trade-offs

- **[Codex alpha (0.140.0-alpha.2) drifts]** → stamp the version on every Track-B finding; prefer source + capture over docs; the report stays auditable.
- **[Probe burns premium Copilot quota]** → single-axis matrix, minimal payloads, integration-tagged (never CI), run deliberately.
- **[Track A and Track B findings conflict or are incomplete]** → that is itself a report finding (an open risk for the follow-up), not a blocker; the report records gaps honestly rather than papering over them.
- **[Capture run touches user state]** → hermetic via `-c` overrides + ephemeral port (D4); explicitly required by the `codex-client-behavior` spec.
- **[Scope creep back into implementation]** → Non-Goals + D1 are explicit; if implementation work is attempted here, it belongs in the follow-up change.

## Migration Plan

Additive, docs/tests-only; nothing to roll back in production.

1. Set up references (`openai-sdk-pkg`, `codex` source); correct `pipeline-design.md` §3 framing.
2. Track A: extend `PlaygroundClient` with `/responses`; add `ResponsesProbe.cs`; run; record findings.
3. Track B: read Codex source; capture a real request via the bridge's tracing mode; record findings.
4. Synthesize the report (A ∩ B → recommendation + findings-sized work-item list for the follow-up change).

Rollback: revert the docs/test commits; `/cc` is never touched.

## Open Questions

These are the questions the research *answers*; listed so the report has a checklist:
- Which models expose `/responses` on this Enterprise account, and with what `capabilities`?
- Which `reasoning.effort` values does Copilot's `/responses` accept per model?
- Are `reasoning.summary` / encrypted-reasoning echo / `store` / `prompt_cache_*` accepted, or must they be stripped (as Anthropic's `service_tier` was)?
- How are `apply_patch` / `web_search` / `image_generation` treated?
- Does Copilot's `/responses` stream append a non-spec terminator the bridge must filter?
- Which provider-relative sub-paths does Codex actually call, and what exact body does it send?
- What does Codex expect back / how does it parse the stream?
- Does §3's translation assumption survive the evidence?
