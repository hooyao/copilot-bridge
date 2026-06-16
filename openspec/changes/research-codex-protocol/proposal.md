## Why

The bridge will add OpenAI Codex CLI as its second client, but **we cannot yet design that support** — we have no verified facts about either side of the wire. Estimating the implementation now would be guesswork (拍脑袋). The Anthropic path avoided exactly this: it became complete because research and live verification *preceded* design. The first deliverable for Codex must therefore be a **thorough, evidence-producing research change**, not implementation.

This change produces the facts. A *separate, follow-up* change will carry the implementation — and its design/specs/tasks can only be written honestly **after** this research lands, scoped to "as much work as the findings actually require."

## What Changes

This change builds the **research harness** and produces the **research report**. It writes no production/`/codex` code. Research has two tracks whose intersection determines the implementation shape:

- **Track A — backend wire-truth (the probe).** A live-probe harness that hits Copilot's `/responses` directly (via the existing `PlaygroundClient`, no `/codex` endpoint needed) to establish, as playground-derived facts: which models advertise `/responses`; which `reasoning.effort` values 200 vs 400 per model; whether Responses-specific fields (`reasoning.summary`, encrypted-reasoning echo, `store`, `prompt_cache_*`) are accepted; tool acceptance (`function` / `apply_patch` / `web_search` / `image_generation`); and the actual streaming SSE event sequence (incl. any non-spec terminator).
- **Track B — client behavior (what Codex actually sends).** Codex is **open source** (`github.com/openai/codex`, Rust under `codex-rs/` — `core/src/client.rs`, `client_common.rs`, `codex-api/src/`, `tools/src/responses_api.rs`). Research reads that source to establish which sub-paths Codex calls, the exact request body it builds, and the reasoning/tools/model-id shape it emits — then **cross-checks against real captured bytes** using the bridge's existing request-traces capture mode and a throwaway capture run of the local `codex.exe`. This mirrors how the Anthropic work used the decompiled Claude Code source (§16 "what claude.exe actually calls").
- **Reference setup.** Add `references/openai-sdk-pkg/` (unpacked official `openai` SDK) and a read-only checkout of `references/codex/` (the Rust source) as type/behavior references — parallels of `references/anthropic-sdk-pkg/`. `agent-maestro/docs/openai-responses-api-design.md` is reference-only and lower quality; not load-bearing.
- **Research report.** Findings written into `docs/` (extending `copilot-api-research.md` and/or a new `docs/codex-protocol-research.md`), each fact cited (`file:line` for source, request id / probe output for live results), with an explicit **A ∩ B → implementation-shape** section that the follow-up change consumes.
- **`docs/pipeline-design.md` §3 correction.** Record that §3's assumption (the OpenAI/Codex client needs OpenAI-**Chat**↔IR translation) is contradicted by Track A/B evidence (Codex is Responses-only; Copilot has native `/responses`). State the corrected framing; leave the final strategy to the report.

## Capabilities

### New Capabilities
- `codex-backend-probe`: A live-probe harness + recorded findings establishing Copilot `/responses` wire-truth (model availability, effort acceptance, Responses-field acceptance, tool acceptance, SSE event sequence) as playground-derived facts. (Track A.)
- `codex-client-behavior`: A source-grounded + capture-verified account of what the Codex CLI actually sends and expects on the wire — sub-paths, request body shape, reasoning/tool/model-id emission, streaming expectations. (Track B.)
- `codex-protocol-report`: The synthesized research report intersecting Tracks A and B into a documented implementation-shape recommendation (passthrough vs translation, required coercions, endpoint sub-routes, open risks) that a follow-up implementation change consumes.

### Modified Capabilities
<!-- None. No existing OpenSpec specs in openspec/specs/. The pipeline-design.md §3 correction is a docs edit captured under codex-protocol-report, not a spec-level requirement change. -->

## Impact

- **Reference checkouts** (gitignored, read-only, never edited): `references/openai-sdk-pkg/`, `references/codex/`.
- **Test project** (`tests/CopilotBridge.Playground`): `PlaygroundClient` gains `/responses` methods; new `ResponsesProbe.cs`; all live-Copilot tests tagged `[Trait("Category","Integration")]` (CI skips); never bind port 8765. A throwaway Track-B capture run uses the bridge's existing request-traces mode — not committed test code unless it proves reusable.
- **Docs**: `docs/copilot-api-research.md` (+ likely `docs/codex-protocol-research.md`), `docs/pipeline-design.md` §3.
- **No production code, no `/codex` endpoint, no DTOs, no profile catalog** — those belong to the follow-up implementation change, which is deliberately *not* scoped until this research completes.
- **No breaking changes**; the `/cc` Anthropic path is untouched.
- **Local tooling**: reads `codex.exe` at `C:\Users\yahu2\AppData\Local\OpenAI\Codex\bin\f1c7ee7a13db5fed\codex.exe` (`0.140.0-alpha.2`, not on PATH) only for the Track-B capture cross-check.
