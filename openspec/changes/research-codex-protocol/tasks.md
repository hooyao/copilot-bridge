# Research Tasks

> This change is **research only** — it produces facts + a report. No `/codex`
> endpoint, no DTOs, no profile catalog. Implementation is a **separate
> follow-up change** scoped from this report (Stage 5). Tasks are detailed here
> because the research plan is fully knowable now; the implementation plan is
> not, and writing it now would be a guess.

## 1. Reference & doc setup

- [x] 1.1 Clone the official `openai` JS/TS SDK and unpack it into `references/openai-sdk-pkg/` (the `resources/responses/*` types); confirm `references/` is gitignored so it is never committed or edited.
- [x] 1.2 Clone `github.com/openai/codex` into `references/codex/` (read-only); confirm the Rust tree is present (`codex-rs/core/src/client.rs`, `client_common.rs`, `codex-api/src/`, `tools/src/responses_api.rs`, `apply-patch/`).
- [x] 1.3 Create `docs/codex-protocol-research.md` with a header stamping: Codex CLI version (`codex --version`), the local `codex.exe` path, the Copilot account type, and the date. This file accumulates all findings.
- [x] 1.4 In the new doc, record the already-verified entry facts with citations: Codex `wire_api=responses`-only + custom-provider config keys (`developers.openai.com/codex/config-reference`); Copilot native `/responses` (`@vscode/copilot-api` `DomainService.capiResponsesURL`, cite `references/vscode-copilot-api-pkg` file:line); reference-behavior pointer to `copilot-api-anthropic/src/routes/responses/handler.ts` + `create-responses.ts`.
- [x] 1.5 Note `agent-maestro/docs/openai-responses-api-design.md` is reference-only / lower quality.

## 2. Track A — Copilot `/responses` backend probe

- [x] 2.1 Extend `tests/CopilotBridge.Playground/PlaygroundClient.cs` with `POST /responses` helpers (non-streaming returning `(status, body)`; streaming returning the ordered raw SSE events), mirroring its existing `/v1/messages` helpers. Reuse the existing auth/header path.
- [x] 2.2 Add `tests/CopilotBridge.Playground/ResponsesProbe.cs` tagged `[Trait("Category","Integration")]`, structured like `ModelProfileProbe.cs` (single-axis matrices, `ITestOutputHelper` logging, no port binding).
- [x] 2.3 **Model availability**: probe `GET /models`, log every model id whose `supported_endpoints` contains `/responses`, dumping each `capabilities` block. Record the list in the research doc. (spec: discover Responses-capable models) → **6 models; codex = `gpt-5.3-codex`** (doc §2.1)
- [x] 2.4 **Effort acceptance**: for each Responses-capable model, send a minimal `/responses` request per `reasoning.effort` ∈ {absent, minimal, low, medium, high, xhigh}; log status + preview; record the per-model accepted/rejected matrix and flag any `/models` metadata-vs-live mismatch. (spec: establish reasoning-effort acceptance) → **only `minimal` rejected** (doc §2.2)
- [x] 2.5 **Responses-field acceptance**: probe `reasoning.summary`, encrypted-reasoning echo (`reasoning` input item), `store`, `prompt_cache_*` each in isolation on a representative model; record accept/reject + verbatim rejection text. (spec: establish Responses-field acceptance) → **`service_tier`/`store:true` rejected; `include:reasoning.encrypted_content` OK** (doc §2.3)
- [x] 2.6 **Tool acceptance**: probe `function`, `apply_patch`, `web_search`, `image_generation` tool entries; record accept / reject / rewrite-required per type. (spec: establish tool acceptance) → **only `image_generation` rejected; `apply_patch` needs NO rewrite** (doc §2.4)
- [x] 2.7 **Streaming sequence**: issue a streaming `/responses` request (text + a tool call), read to completion, record the ordered event types with a representative payload each, and flag any non-spec terminator (e.g. trailing `[DONE]`). (spec: capture the /responses streaming event sequence) → **ends at `response.completed`, NO `[DONE]`** (doc §2.5)
- [x] 2.8 Run 2.3–2.7 against live Copilot; paste raw outputs (status lines, request ids, event lists) into the research doc as the Track-A findings section.

## 3. Track B — what Codex CLI actually sends (source + capture)

- [x] 3.1 Read the Codex request layer in `references/codex/` and document, with `file:line` citations: which provider-relative sub-paths Codex calls; how it builds the `/responses` request body (model id form, `input` structure, `instructions`, `reasoning` effort/summary, `tools` incl. `apply_patch`, `store`, caching fields, `stream`). (spec: identify the Codex CLI API surface; establish the Codex request body shape) → doc §3.1, §3.2, §3.5
- [x] 3.2 Read the Codex stream-parsing code and document which SSE events it consumes, which it requires, and any terminator handling. (spec: establish Codex streaming expectations) → doc §3.3
- [x] 3.3 Stand up a throwaway capturing endpoint to record real Codex bytes: enable the bridge's existing request-traces mode (`Tracing.Enabled`) on an ephemeral non-8765 port, OR a minimal local catch-all that logs the request — whichever is faster. Do not commit throwaway scaffolding unless it proves reusable. → `docs/scratch/codex-capture-server.ps1` (PowerShell HttpListener on :18799, scratch/uncommitted)
- [x] 3.4 Drive the real `codex.exe` at the capturing endpoint hermetically: `codex exec --json -c model_provider=<id> -c model_providers.<id>.base_url=http://127.0.0.1:<ephemeral>/... --skip-git-repo-check --ephemeral "<tiny prompt>"`, with `env_key` pointed at a dummy. **Do not edit `~/.codex/config.toml`.** Capture at least: a plain turn, a turn that triggers a tool/`apply_patch`, and (if reachable) a model-list call. (spec: capture cross-check does not disturb the environment) → 6 real `POST /codex/responses` captured; config untouched
- [x] 3.5 Diff captured request bytes against the 3.1 source reading; record divergences (alpha drift) in the research doc as the Track-B findings section, with the Codex version stamped. → doc §3.4: source confirmed; new facts (default effort=medium, 14-tool set, developer role, x-codex-* headers, include=array)

> **3.3–3.5 DONE (2026-06-12).** Live capture confirms the source read with no
> drift; surfaced default effort/verbosity, the 14-tool default set (apply_patch
> + web_search sent by default, image_generation NOT), the `developer` input
> role, and the real `x-codex-*` header set. The capture stub didn't complete
> Codex's turn loop (6 retries, non-zero exit) — a true green end-to-end run is
> the follow-up change's headless harness job, but request-shape truth is locked.

## 4. Synthesis — the report (A ∩ B)

- [x] 4.1 Build the intersection table: for each field / tool / effort value / sub-path, cross Track B (does Codex send it?) with Track A (does Copilot accept it?) → classify as passthrough / coerce / translate / drop, each citing the backing finding. (spec: synthesize Tracks A and B) → doc §4.1
- [x] 4.2 State the implementation-shape recommendation (forward-as-is vs transform, and where), grounded only in 4.1 — no ungrounded claims. (spec: synthesize Tracks A and B; no unjustified claims) → doc §4.2 (**near-passthrough + 3 coercions**)
- [x] 4.3 Enumerate the follow-up change's work items sized to the findings (DTOs needed, `/codex` sub-routes, required coercions, header set delta vs `/v1/messages`, streaming handling, e2e harness), and explicitly list anything the findings rule out. (spec: scope the follow-up implementation change) → doc §4.3
- [x] 4.4 Update `docs/pipeline-design.md` §3: state whether its OpenAI-Chat↔IR translation assumption survives the evidence, flag the stale assumption, and record the corrected framing. (spec: correct the pipeline-design assumption) → §3 dated callout added
- [x] 4.5 Ensure every recommendation in the report carries its citation (source `file:line`, request id / probe output, Codex version). (spec: findings are durable and cited)

## 5. Handoff

- [x] 5.1 Confirm `dotnet test CopilotBridge.slnx --filter "Category!=Integration"` stays green (research additions must not break CI). → **137 passed, 0 failed**; probes correctly excluded (Integration-tagged)
- [x] 5.2 Summarize the report's recommendation + work-item list at the top of the research doc as the explicit input for the follow-up implementation change; do not create that change here. → doc TL;DR added
