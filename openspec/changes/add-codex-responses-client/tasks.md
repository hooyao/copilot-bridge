# Implementation Tasks

> Change 3 of 3. Designs: `docs/codex-implementation-design.md`,
> `docs/ir-definition-design.md` §4/§7, `docs/codex-protocol-research.md`.
> Depends on change 1 (IR + escape-hatch + A-harness) and change 2 (Responses
> snapshot + drift). Ships only the Codex→`/responses` cell. Keep `/cc`
> byte-identical; AOT; English-only internal/tool prompts; never bind 8765.

## 1. Responses DTOs (fully typed)

- [x] 1.1 `Models/Responses/ResponsesRequest.cs` — the 13 verified fields (research §3.2); `include` as array; `store`/`stream`/`tool_choice`/`parallel_tool_calls`/`prompt_cache_key`/`service_tier`/`client_metadata`.
- [x] 1.2 `input[]` item types (message / function_call / function_call_output / reasoning) + content parts (input_text / input_image / output_text), fully typed; `developer` role modeled.
- [x] 1.3 `tools[]` variants by `type` (function / custom / web_search / namespace / tool_search / image_generation); `Reasoning { effort,summary,context }`; `TextControls { verbosity,format }`.
- [x] 1.4 SSE event DTOs T3/T4 need (response.created / output_item.* / output_text.delta / function_call_arguments.delta+done / reasoning* / response.completed / failed / incomplete) per research §2.5/§3.3.
- [x] 1.5 Register all in `Models/JsonContext.cs`; AOT build clean; grounded in `references/openai-sdk-pkg/`.

## 2. Routing + profile catalog

- [x] 2.1 Wire `CopilotModelRegistry.Resolve` to return `BackendVendor.CopilotResponses` + `/responses` for the Codex ids (gpt-5.3-codex/gpt-5.4/gpt-5.4-mini/gpt-5.5/gpt-5-mini/mai-code-1-flash-internal). Verify `Normalize` no-ops on `gpt-5.3-codex` (unit test).
- [x] 2.2 `CodexModelProfile` + `CodexModelProfileCatalog` — two effort profiles (large/small) row-by-row from change 2's `docs/copilot-responses-contract-snapshot.json`; `RejectsCustomTools` flag on `mai-code-1-flash-internal`; the 2 uniform coercions (strip service_tier, drop image_generation).
- [x] 2.3 Unknown-model error path (mirror `UnknownModelException` → clear error in Responses error shape).

## 3. Translators T1–T4 + strategy + client

- [x] 3.1 T1 `ResponsesToIrInboundAdapter : IClientInboundAdapter<ResponsesRequest, MessagesRequest>` — map per design §4 table; stash un-modeled knobs into request-level `ProviderExtensions["openai"]` verbatim; `encrypted_content` → Thinking/RedactedThinking where it maps else part-level bag.
- [x] 3.2 T4 `IrToResponsesOutboundAdapter : IClientOutboundAdapter<MessagesRequest>` — IR response stream → Responses SSE (stateful).
- [x] 3.3 `ICopilotClient.PostResponsesAsync` + impl — POST `{baseUrl}/responses`, bearer + official VS Code Copilot headers (reuse `CopilotHeaderFactory`); set `Copilot-Vision-Request` if `input_image`; set `x-initiator` per last input item.
- [x] 3.4 `CopilotResponsesStrategy : IUpstreamStrategy<MessagesRequest>` — `Matches(target.Vendor==CopilotResponses)`; T2 (rebuild ResponsesRequest from IR → re-apply bag verbatim → apply coercions) + T3 (Responses SSE → IR stream events, stateful); no `[DONE]` filter.

## 4. Endpoint + host wiring

- [x] 4.1 `Endpoints/Codex/CodexResponsesEndpoint.cs` — `POST /codex/responses`; read+audit → T1 → IR → shared `Pipeline<MessagesRequest>` → T4 → write SSE/buffered; same audit/summary as `/cc`, leaner (no count_tokens/models).
- [x] 4.2 DI in `BridgeServiceCollectionExtensions`: register T1/T4 adapters, `CopilotResponsesStrategy`, `CodexModelProfileCatalog`; add the strategy to the **shared** `Pipeline<MessagesRequest>` registry (selected by vendor). No new pipeline instance.
- [x] 4.3 `app.MapCodexResponses()` in `ServeCommand`.

## 5. Fixtures (real, de-identified, committed)

> ⚠️ **CONTEXT-LOSS RISK — read before starting.** The 6 real captures in
> `docs/scratch/codex-capture/*.txt` are **gitignored** (scratch) and were
> produced by a one-time setup that is NOT trivially reproducible: a throwaway
> HTTP capture server (`docs/scratch/codex-capture-server.ps1`, also gitignored)
> driven by the local `codex.exe` (`C:\Users\yahu2\AppData\Local\OpenAI\Codex\bin\f1c7ee7a13db5fed\codex.exe`,
> `codex-cli 0.140.0-alpha.2`, not on PATH). If these files are gone (new
> machine / clean checkout / scratch cleared), **regenerate them first** by
> re-running the Track-B capture procedure documented in
> `docs/codex-protocol-research.md` §3.4 before doing 5.1. Do NOT fabricate
> fixtures — they must be real wire captures. Task 5.1's whole point is to
> promote these ephemeral captures into **committed** fixtures so this risk
> disappears for future sessions.

- [x] 5.1 De-identify the 6 real captures in `docs/scratch/codex-capture/*.txt` → committed `codex-request-*.json` (plain / tool-call / multi-turn), version+date stamped; strip session/thread/git ids. **If the scratch captures are missing, regenerate per the warning above first.**
- [x] 5.2 Capture + commit `responses-sse-*.txt` (text + tool-call streams) from a live `ResponsesProbe` run (live Copilot — regenerable any time, unlike 5.1).

## 6. Validation — A invariant (CI, reuse change-1 harness)

- [x] 6.1 A1 round-trip self-inverse: real codex-request fixtures `T1→IR→T2` under the §7.1 fidelity bar (diffs classified; only documented coercions allowed).
- [x] 6.2 A2 opaque byte-passthrough: `function_call` args, `apply_patch` body, `encrypted_content` byte-identical.
- [x] 6.3 A3 bag canary + A4 bag transport (store/include/prompt_cache_key/text.verbosity).
- [x] 6.4 A5 tool-pairing integrity (multi-turn call_id linkage + ordering).
- [x] 6.5 A6 stream round-trip: `responses-sse` fixtures `T3→IR→T4` — event order, delta concatenation identical, fragments byte-identical, terminal `response.completed` preserved, no `[DONE]`.

## 7. Validation — B/H/E (live + hot-path)

- [x] 7.1 B3 coercion-vs-live: assert each coercion against the live `ResponsesProbe` (strip service_tier BECAUSE still 400; clamp xhigh→high for gpt-5-mini BECAUSE still rejected). `[Trait("Category","Integration")]`.
- [x] 7.2 H1 hot-path byte-equality: real cc-request fixtures through `Pipeline<MessagesRequest>` byte-identical before/after registering the Codex strategy. H2 existing Anthropic suite unchanged.
- [x] 7.3 E1 live `codex.exe` e2e: `codex exec --json -c model_provider=<id> -c model_providers.<id>.base_url=http://127.0.0.1:<ephemeral>/codex -c ...wire_api=responses -c ...env_key=<dummy>` (ephemeral non-8765, `~/.codex/config.toml` untouched); assert full turn (text + forced tool call) reaches Codex stdout JSONL; save the four-file audit and diff post-hoc against the A goldens. `[Trait("Category","Integration")]`.

## 8. Docs + wrap-up

- [x] 8.1 `dotnet test CopilotBridge.slnx --filter "Category!=Integration"` green (A-invariant + unit are CI-safe; B/E Integration-tagged skip).
- [x] 8.2 Finalize `docs/pipeline-design.md` §3 as-built (Codex = native-`/responses` via the shared IR, the dated callout replaced).
- [x] 8.3 `README.md` / `AGENTS.md` roadmap: Codex `/codex` → implemented. Eyeball AOT size → `docs/size-history.md`.

## Outcome (as-built 2026-06-15)

- **Codex works end-to-end through the bridge.** Live `codex.exe`
  (0.140.0-alpha.2) → `/codex/responses` → Copilot `/responses` → back: both E1
  turns pass — a plain turn ("pong" reaches Codex stdout) and a tool turn (shell
  `echo` canary round-trips through the function_call loop). Provider injected via
  `codex exec -c` overrides; `~/.codex/config.toml` untouched; ephemeral port.
- **Hub-IR, not passthrough.** Every Codex request transits the shared Anthropic
  IR (T1 → `Pipeline<MessagesRequest>` → T2 → Copilot, T3 → IR → T4 back). The
  `CopilotResponsesStrategy` is registered in the SAME pipeline registry as the
  Anthropic passthrough; routing picks by `target.Vendor`.
- **Validation:** A-invariant A0–A6 on real de-identified captures (DTO parse,
  round-trip fidelity, tool byte-passthrough, bag canary/transport, tool-pairing,
  SSE T3→IR→T4); B3 coercion-vs-live (service_tier strip + effort clamps still
  necessary on live Copilot); H1/H2 hot-path (the `/cc` byte-equality + existing
  suite unchanged after registering the Codex strategy); E1 live e2e. CI-safe
  suite 202/202 green; B3/E1 Integration-tagged.
- **AOT 0 warnings**, +442 KB → 11.769 MB win-x64 (`docs/size-history.md`).

### Real bugs found by E1 (offline tests could not catch these)

- **`ModelRouterStage` ran the Anthropic `ModelProfileCatalog` for gpt-* models**
  → `UnknownModelException`. Fixed: resolve vendor first; a `CopilotResponses`
  target skips the Anthropic profile/`ProfileAdjuster` (Codex effort coercion is
  in T2). `/cc` unaffected (claude-* still runs the full path — H1 green).
- **T4 emitted too-thin Responses events** — Codex's parser ignored them
  (`output_tokens:0`, empty text). Fixed by matching the live Copilot event shape:
  stable `item_id`/`content_index`, `content_part.added/done`,
  `output_text.done` with accumulated text, `output_item.done` with full `item`
  content, and `response.completed` carrying the assembled `output[]`. Grounded in
  the real `responses-sse-*.txt` captures.
- **T3 clobbered a `tool_use` stop_reason** at `response.completed` (reset to
  end_turn) — found by A6; fixed to preserve the latched tool_use.

### Known follow-ups (not blocking)

- A5 uses a synthetic (real-shaped) tool-flow request; the captures are pure
  message turns. The live E1 tool turn exercises the real function_call path
  end-to-end, so the path IS covered — promoting an E1-harvested tool capture to
  a committed A5 fixture is a nice-to-have.
- Codex→Opus and ClaudeCode→GPT5 cells are architecture-ready (the routing seam
  is vendor-agnostic) but not wired/tested this change, as scoped.
