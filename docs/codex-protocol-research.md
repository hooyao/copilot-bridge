# Codex / Responses protocol research

> Status: research **complete** (probe + source) · 2026-06-12 · Codex `0.140.0-alpha.2`
>
> Goal: establish, with evidence, what the Codex CLI sends and what GitHub
> Copilot's `/responses` endpoint accepts — the two-track ground truth a
> follow-up change needs before any `/codex` implementation can be designed.
> This is the Codex parallel of `docs/copilot-api-research.md` (the Anthropic
> path's research doc).
>
> **Nothing here is implementation.** Findings feed the
> `research-codex-protocol` OpenSpec change's synthesis report (§4), which a
> separate implementation change consumes.

## TL;DR — verdict for the follow-up implementation change

**Codex support = near-passthrough, structurally identical to the `/cc` Claude
Code path** (parse → coerce → inject official Copilot headers → forward to native
`/responses` → stream back). **No** translation layer, **no** streaming state
machine, **no** `[DONE]` filter, **no** tool rewriting, **no** WebSocket handling.
Coercions, grounded in the full 6-model matrix (§2):

1. **`reasoning.effort` — per-model profile** (§2.2): large models (`gpt-5.3-codex`,
   `gpt-5.4-mini`, `gpt-5.4`, `gpt-5.5`) reject `minimal`; small models
   (`gpt-5-mini`, `mai-code-1-flash-internal`) reject `none` + `xhigh`. Two
   inverted profiles → a per-model effort table, NOT one global rule.
2. **strip `service_tier`** (uniform 400; §2.3)
3. **drop `tools[].type == "image_generation"`** (uniform 400; §2.4)

Everything else passes through on all 6: `function`/`apply_patch`/`web_search`
tools, `input_image` vision, encrypted-reasoning echo, summaries, the SSE stream.
Per-model edge: `mai-code-1-flash-internal` 500s on custom/`apply_patch` tools.
6 models expose `/responses`; the Codex model is **`gpt-5.3-codex`** (§2.1). Full
work-item list: §4.3. This overturns `pipeline-design.md` §3's "Codex needs
OpenAI-Chat translation" assumption (§4.4).

> **Addendum — 2026-07 model-set update (the tables below are a 2026-06-12 snapshot).**
> The live `/responses` model set and effort profiles have since moved; the §2.1 /
> §2.2 / §4 tables reflect the original 2026-06-12 probe and are NOT re-probed here.
> Current wire-truth (see `codex-implementation-design.md` §5–§6 and the live
> `CodexModelProfileCatalog` / `CopilotModelRegistry.ResponsesModelIds`):
> - **Model set:** `mai-code-1-flash-internal` was retired → **`mai-code-1-flash-picker`**;
>   the three **`gpt-5.6` codenames** (`gpt-5.6-luna` / `gpt-5.6-sol` / `gpt-5.6-terra`)
>   were added. So it is no longer "6 models".
> - **Effort profiles: now THREE, not two.** The large/small split still holds, plus
>   a new **`xlarge`** profile for the gpt-5.6 codenames: they accept
>   `none/low/medium/high/xhigh/**max**` and reject `minimal` — the **first Codex
>   models to accept `max`** (`ResponsesProbe.Gpt56_Effort_ReProbe`). The §2.2 table's
>   effort vocabulary omits `max` precisely because no 2026-06-12 model accepted it.
>   Probe trap: the 400 body for `minimal` on these ids lists supported values
>   WITHOUT `max`, yet `max` live-probes 200 — the advertised list lies.



## 0. Provenance (stamp every finding against this)

| Artifact | Version / ref | Location |
| --- | --- | --- |
| Local Codex CLI (Track-B capture target) | `codex-cli 0.140.0-alpha.2` | `C:\Users\yahu2\AppData\Local\OpenAI\Codex\bin\f1c7ee7a13db5fed\codex.exe` (not on PATH) |
| Codex source (Track-B source read) | git `bf667c7` (2026-06-12), reports `0.0.0-dev` (release version injected at build) | `references/codex/` (shallow clone, read-only) |
| OpenAI SDK (Responses type source) | `openai@6.42.0` | `references/openai-sdk-pkg/package/resources/responses/` |
| Copilot account (Track-A probe target) | Enterprise (`api.enterprise.githubcopilot.com`) | live |

> ⚠ The source clone (`main`, `bf667c7`) may be slightly ahead of the local
> `0.140.0-alpha.2` exe. Where source and captured bytes disagree, the
> **capture** is authoritative for what *this* exe sends; note the delta.

## 1. Entry facts (verified before probing)

### 1.1 Codex CLI speaks the Responses API only
- `wire_api` accepts a single value, `responses`; Chat Completions (`wire_api="chat"`) was removed Feb 2026. Custom provider via `[model_providers.<id>]` with `base_url`, `env_key` (→ `Authorization: Bearer`), `http_headers`, `query_params`. Effort vocabulary: `minimal | low | medium | high | xhigh`.
- Source: `developers.openai.com/codex/config-reference` (fetched 2026-06-12).

### 1.2 Copilot exposes a native `/responses` endpoint
- Official `@vscode/copilot-api`: `DomainService.capiResponsesURL` → `${capiBaseURL}/responses`. Cite: `references/vscode-copilot-api-pkg/package/dist/index.js` (`DomainService` getters, ~offset 2300; see `docs/copilot-api-research.md` §3.0.1).
- So Codex(Responses) → Copilot(`/responses`) is structurally parallel to Claude Code(Anthropic) → `/v1/messages`.

### 1.3 Codex calls TWO response paths (source finding, to confirm by capture)
- `references/codex/codex-rs/core/src/client.rs:150-151`:
  `const RESPONSES_ENDPOINT: &str = "/responses";`
  `const RESPONSES_COMPACT_ENDPOINT: &str = "/responses/compact";`
- The `/responses/compact` path was **not** in the prior reference reading (`copilot-api-anthropic` only handles `/responses`). Whether Copilot implements `/responses/compact` is an open question for Track A.

### 1.4 Behavior reference (not type source)
- `references/copilot-api-anthropic/src/routes/responses/handler.ts` + `services/copilot/create-responses.ts`: near-passthrough (parse `ResponsesPayload` → drop `image_generation`, rewrite `apply_patch` custom→function, optional `web_search` strip → forward → stream SSE, drop `[DONE]`). Hand-rolls its Responses types — **not** a reliable type oracle; the official SDK (§0) is.

### 1.5 Lower-quality reference
- `references/agent-maestro/docs/openai-responses-api-design.md` — reference only; its Anthropic work is far worse than this project's. Not load-bearing.

## 2. Track A — Copilot `/responses` backend wire-truth

> Populated by `tests/CopilotBridge.Playground/ResponsesProbe.cs`. Each
> subsection records raw status lines / request ids / event lists.

### 2.1 Models advertising `/responses`
Probed live via `ResponsesProbe.DiscoverResponsesModels` (2026-06-12, Enterprise).
**6 models carry `/responses`** in `supported_endpoints`:

| Model | endpoints | max ctx | max out | `reasoning_effort` (advertised) | vision |
| --- | --- | --- | --- | --- | --- |
| `gpt-5.3-codex` | `/responses`, `ws:/responses` | 400k | 128k | `low, medium, high, xhigh` | yes |
| `gpt-5.4-mini` | `/responses`, `ws:/responses` | 400k | 128k | `none, low, medium, high, xhigh` | yes |
| `gpt-5.4` | `/responses`, `ws:/responses`, `/chat/completions` | 1.05M | 128k | `none, low, medium, high, xhigh` | yes |
| `gpt-5.5` | `/responses`, `ws:/responses` | 1.05M | 128k | `none, low, medium, high, xhigh` | yes |
| `mai-code-1-flash-internal` | `/responses` only | 256k | 128k | `none, low, medium, high, xhigh` | no |
| `gpt-5-mini` | `/responses`, `ws:/responses` | 264k | 64k | `low, medium, high` | yes |

Findings:
- **The Codex model on this account is `gpt-5.3-codex`** (not `gpt-5-codex`/`gpt-5-codex`-style ids from docs). This is the id a Codex CLI default would most plausibly target.
- A **`ws:/responses`** endpoint variant is advertised alongside `/responses` (the WebSocket transport, §3.1). HTTP `/responses` is present on all 6, so the bridge's HTTP path is universally available.
- `gpt-5.4` additionally exposes `/chat/completions` (legacy); irrelevant since Codex is Responses-only.
- `mai-code-1-flash-internal` is `/responses`-only (no `ws:`, no vision) — an internal model.
- Advertised `reasoning_effort` arrays differ per model and **do not match actual acceptance** — see the live matrix in §2.2 (e.g. `gpt-5.3-codex` advertises no `none` but accepts it; the small models advertise only `[low,medium,high]` but accept `minimal`). Treat `/models` capability arrays as a hint, never as truth.
- Raw dump: `docs/scratch/responses-models-dump.txt`.

### 2.2 `reasoning.effort` acceptance per model
Probed `ResponsesProbe.Effort_ProbeAcceptance` (2026-06-12) across **all 6
models** × 7 effort values. **Effort acceptance is per-model — two inverted
profiles** (this is NOT uniform; an earlier 2-model probe wrongly concluded
"only minimal rejected"):

| effort | gpt-5.3-codex | gpt-5.4-mini | gpt-5.4 | gpt-5.5 | gpt-5-mini | mai-code-1-flash |
| --- | --- | --- | --- | --- | --- | --- |
| _absent_ | 200 | 200 | 200 | 200 | 200 | 200 |
| `minimal` | **400** | **400** | **400** | **400** | **200** | **200** |
| `none` | 200 | 200 | 200 | 200 | **400** | **400** |
| `low` | 200 | 200 | 200 | 200 | 200 | 200 |
| `medium` | 200 | 200 | 200 | 200 | 200 | 200 |
| `high` | 200 | 200 | 200 | 200 | 200 | 200 |
| `xhigh` | 200 | 200 | 200 | 200 | **400** | **400** |

**Two effort profiles:**
- **"large"** (`gpt-5.3-codex`, `gpt-5.4-mini`, `gpt-5.4`, `gpt-5.5`): accept `none, low, medium, high, xhigh`; reject `minimal`.
- **"small"** (`gpt-5-mini`, `mai-code-1-flash-internal`): accept `minimal, low, medium, high`; reject `none` AND `xhigh` — **the inverse of the large set at the boundaries**.

- `minimal` 400 body: `{"error":{"message":"Unsupported value: 'minimal' is not supported with the 'gpt-5.5' model. Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'.","code":"invalid_request_body"}}`.
- **Advertised ≠ actual** (again): `gpt-5.3-codex` advertises no `none` yet accepts it; both small models advertise only `[low,medium,high]` yet accept `minimal`. → The bridge MUST use a **per-model effort profile** (the `ModelProfileCatalog` lesson), not one global effort rule.

### 2.2b reasoning-effort: per-model coercion the bridge needs
- A request with `minimal` to a **large** model → drop or map to `low`.
- A request with `none`/`xhigh` to a **small** model → drop (`none`) or clamp `xhigh`→`high`.
- low/medium/high + absent are universally safe.

### 2.3 Responses-specific field acceptance
Probed `ResponsesProbe.Field_ProbeAcceptance` (2026-06-12) across **all 6
models**, each field in isolation. **Uniform across all models** (gateway-level,
not per-model):

| field | result (all 6) | note |
| --- | --- | --- |
| `reasoning.summary:"auto"` | **200** | accepted |
| `include:["reasoning.encrypted_content"]` | **200** | **accepted — Codex multi-turn reasoning works** (Codex sends this whenever reasoning is on, §3.2) |
| `prompt_cache_key` | **200** | accepted (Codex always sends it) |
| `store:true` | **400** | `{"message":"store is not supported","code":"unsupported_value","param":"store",...}` — Codex sends `store=false` for non-Azure anyway (§3.2), harmless |
| `service_tier` | **400** | `{"message":"service_tier is not supported","param":"service_tier",...}` — **must strip** (same as Anthropic path stripped service_tier) |

### 2.4 Tool acceptance
Probed `ResponsesProbe.Tool_ProbeAcceptance` (2026-06-12) across **all 6 models**,
Responses-native tool shapes (top-level name + `type` discriminant):

| tool `type` | result | note |
| --- | --- | --- |
| `function` | **200** all 6 | accepted as-is (Responses-native shape) |
| `custom` (`apply_patch`) | **200** on 5; **500** on `mai-code-1-flash-internal` | **accepted as-is — NO rewrite needed** (contradicts `copilot-api-anthropic` which rewrites custom→function). The flash model 500s on custom/grammar tools — a per-model gap to note. |
| `web_search` | **200** all 6 | accepted — differs from `/v1/messages` (which 400s Anthropic web search) |
| `image_generation` | **400** all 6 | `{"message":"The requested tool image_generation is not supported.","param":"tools",...}` — **must drop** |

### 2.6 Vision (`input_image`)
Probed `ResponsesProbe.Vision_ProbeAcceptance` (2026-06-12), 100×100 PNG as a
`data:image/png;base64,…` URL in an `input_image` content part (Responses shape,
not Anthropic's `image`/`source`):

| model | result |
| --- | --- |
| gpt-5.3-codex, gpt-5.4-mini, gpt-5.4, gpt-5.5, gpt-5-mini | **200** |
| mai-code-1-flash-internal | not vision-capable (skipped) |

`input_image` passes through unchanged on all vision-capable models. The bridge
should still set `Copilot-Vision-Request: true` when an `input_image` is present
(mirrors the Anthropic path's vision header).

### 2.5 Streaming event sequence
Probed `ResponsesProbe.Streaming_CaptureEventSequence` (2026-06-12,
`gpt-5.3-codex`, `stream:true`, **with a forced tool call**). Ordered events:

```
text turn:                         tool-call turn (forced):
response.created                   response.created
response.in_progress               response.in_progress
response.output_item.added         response.output_item.added       (message)
response.content_part.added        response.output_item.done
response.output_text.delta (×N)    response.output_item.added       (function_call)
response.output_text.done          response.function_call_arguments.delta
response.content_part.done         response.function_call_arguments.done
response.output_item.done          response.output_item.done
response.completed                 response.completed
```

- Tool calls stream as **`response.function_call_arguments.delta` / `.done`**
  (the events Codex's parser consumes for the tool loop, §3.3) — confirmed live,
  not just text deltas.
- **NO `[DONE]` terminator** on either turn — `/responses` ends cleanly at
  `response.completed`, unlike Copilot's `/v1/messages` (which appends `[DONE]`).
  So the bridge does **not** need a DONE-filter on this path.
- Terminal `response.completed` is exactly what Codex's SSE parser requires
  (§3.3) → clean event-stream passthrough is viable.
- Non-stream responses also carry the `copilot_usage` extension (same as the
  Anthropic path); Codex tolerates unknown fields (§3.3).
### 2.3 Responses-specific field acceptance _(pending — Task 2.5)_
### 2.4 Tool acceptance _(pending — Task 2.6)_
### 2.5 Streaming event sequence _(pending — Task 2.7)_

## 3. Track B — what Codex CLI actually sends

> Source read from `references/codex/` (§0 ref), confirmed against captured
> bytes from the local exe (§0).

### 3.1 API surface (sub-paths)
Source (`references/codex/codex-rs/core/src/client.rs`, git `bf667c7`):
- `RESPONSES_ENDPOINT = "/responses"` (`:150`) — the main turn path.
- `RESPONSES_COMPACT_ENDPOINT = "/responses/compact"` (`:151`) — unary compaction; **not** in the `copilot-api-anthropic` reference. Copilot support unknown (Track-A open Q).
- `MEMORIES_SUMMARIZE_ENDPOINT = "/memories/trace_summarize"` (`:155`) — memory feature; almost certainly not on Copilot.

> ⚠ Codex also has a **Responses-over-WebSocket v2** transport (`response.create`,
> beta `responses_websockets=2026-02-06`, `client.rs` WS machinery). **Gated off
> for custom providers**: `ModelProviderInfo.supports_websockets` defaults to
> `false` (`model-provider-info/src/lib.rs:135-136`), and
> `responses_websocket_enabled()` returns false unless it's set
> (`client.rs:765-773`). So a bridge provider that does NOT set
> `supports_websockets=true` → `stream()` takes the HTTP branch
> `stream_responses_api` (`client.rs:1556-1607`). **The bridge sees plain HTTP
> `POST /responses` SSE.** Confirm by capture (Task 3.4).

### 3.2 Request body shape
`ResponsesApiRequest` (`codex-api/src/common.rs:182-203`), built by
`build_responses_request` (`core/src/client.rs:706-760`). Fields on the wire:

| Field | Source value | Bridge note |
| --- | --- | --- |
| `model` | `model_info.slug` (`:744`) | exact id form Codex sends — reconcile with Copilot ids (Track-A) |
| `instructions` | `prompt.base_instructions.text` (`:716`); omitted if empty | top-level system prompt (not in `input`) |
| `input` | `Vec<ResponseItem>` (`:717`) | conversation items; image `detail` stripped under responses-lite |
| `tools` | `create_tools_json_for_responses_api` (`:718`) | shape TBD (Task 3.x) |
| `tool_choice` | hardcoded `"auto"` (`:748`) | |
| `parallel_tool_calls` | `prompt.parallel_tool_calls && !use_responses_lite` (`:749`) | |
| `reasoning` | `build_reasoning(model_info, effort, summary)` (`:719`) | model-gated; shape TBD |
| `store` | `provider.is_azure_responses_endpoint()` (`:751`) | **`false` for Copilot/bridge** (non-Azure) — no server persistence |
| `stream` | hardcoded `true` (`:752`) | Codex always streams |
| `include` | `["reasoning.encrypted_content"]` iff `reasoning.is_some()` else `[]` (`:720-724`) | **Codex expects encrypted reasoning echoed back** — Track-A must-probe |
| `service_tier` | `model_info.service_tier_for_request(...)` (`:742`); omit if None | Anthropic path showed Copilot strips `service_tier` — likely reject/ignore |
| `prompt_cache_key` | always `Some(self.prompt_cache_key())` (`:741,755`) | |
| `text` | verbosity + output_schema (`:736`); omit if None | `output_schema` → `text.format` JSON-schema |
| `client_metadata` | `responses_metadata.client_metadata()` (`:757`) | Codex-internal metadata map |

Codex-specific request **headers** (`build_responses_headers`, `client.rs:1649-1667`,
+ consts `:133-149`): `x-codex-beta-features`, `x-codex-turn-state`,
`x-codex-turn-metadata`, `x-codex-installation-id`, `OpenAI-Beta`, etc. Whether
Copilot tolerates/ignores these is a Track-A/■capture question.

### 3.3 Streaming expectations
HTTP SSE parser: `process_sse` + `process_responses_event`
(`codex-api/src/sse/responses.rs:276-410, 412+`). Events Codex consumes:

| SSE `type` | Codex action |
| --- | --- |
| `response.created` | start |
| `response.output_item.added` / `.done` | item lifecycle (message, function_call, reasoning, …) |
| `response.output_text.delta` | assistant text delta |
| `response.custom_tool_call_input.delta` | tool-call argument delta (by `item_id`/`call_id`) |
| `response.reasoning_text.delta` | reasoning content delta (`content_index`) |
| `response.reasoning_summary_text.delta` / `response.reasoning_summary_part.added` | reasoning summary |
| `response.completed` | terminal — carries `id`, `usage`, `end_turn` |
| `response.failed` | terminal error — message pattern-matched into context-window / quota / invalid-prompt / overloaded / retryable |
| `response.incomplete` | terminal — `incomplete_details.reason` |

Parser robustness (matters for the bridge):
- **Unknown event types are ignored** (`_ => trace!("unhandled responses event")`, `:404-405`) — extension fields/events won't break Codex.
- **Unparseable `data:` lines are skipped** (`continue`, `:452-457`).
- **A terminal event is REQUIRED**: if the stream closes with no `response.completed`/`failed`/`incomplete`, Codex errors `"stream closed before response.completed"` (`:435-441`). The bridge must forward the terminal event.
- **No `[DONE]` handling** — Codex keys off `response.completed`, so a trailing OpenAI-style `[DONE]` would just be an "unhandled event" (harmless), unlike the Anthropic SDK which choked on it (Anthropic path §8.6). Filtering it is optional, not required, for Codex. _(Confirm Copilot even emits it on `/responses` — Track-A 2.5.)_

Request-side reasoning/text structs (`codex-api/src/common.rs:124-161`):
- `Reasoning { effort?, summary?, context? }` — all omitted-if-None; `effort` ∈ `ReasoningEffortConfig`.
- `TextControls { verbosity?, format? }`; `format` = JSON-schema (`output_schema`).
- `reasoning` only emitted when `model_info.supports_reasoning_summaries` (`client.rs:686`); else the whole field is `None`.

### 3.4 Source-vs-capture deltas
**Live capture done** (2026-06-12). Drove the real `codex.exe`
(`0.140.0-alpha.2`) via `codex exec --json` at a throwaway HTTP capture server
(`docs/scratch/codex-capture-server.ps1`), custom provider injected with `-c`
(`base_url=http://127.0.0.1:18799/codex`, `wire_api=responses`, `env_key=CAPI_KEY`)
— `~/.codex/config.toml` untouched. 6 real `POST /codex/responses` captured
(retries appending turn history). Source read **confirmed**; deltas/additions:

**Confirmed exactly (source == wire):**
- Path `POST {base_url}/responses` (= `/codex/responses`), per `url_for_path` (§3.1).
- Auth `Authorization: Bearer dummy-key-123` (env_key → Bearer).
- `Accept: text/event-stream`, `Content-Type: application/json`, **HTTP not WebSocket** (custom provider, `supports_websockets` unset — §3.1 confirmed live).
- Body fields = the 13 `ResponsesApiRequest` fields (§3.2): `model`, `instructions`, `input`, `tools`, `tool_choice:"auto"`, `parallel_tool_calls:true`, `reasoning`, `store:false`, `stream:true`, `include`, `prompt_cache_key`, `text`, `client_metadata`.
- `store:false` ✅, **`service_tier` NOT sent** ✅ (absent in all 6 captures), `include` **is an array** `["reasoning.encrypted_content"]` ✅ (matches `Vec<String>` source — an earlier guess that it might be scalar was wrong).

**New facts only the capture gave:**
- **Default `reasoning.effort = "medium"`**, `text.verbosity = "low"` for `gpt-5.3-codex` (CLI default, no flags).
- **Codex's default toolset = 14 tools**, types: `function` (×10, incl. `shell_command`, `apply_patch` sibling utilities, `update_plan`), `custom` (`apply_patch`), `namespace` (`mcp__node_repl`), `tool_search`, **`web_search`**. So `apply_patch`(custom) + `web_search` are sent **by default** → both 200 on Copilot (§2.4) → clean. **`image_generation` is NOT in the default set** → the "drop image_generation" coercion (§4.2) is defensive, rarely exercised.
- First input item is **`role:"developer"`** (not `system`) — Codex uses the developer role for its preamble; `instructions` (~12KB) carries the system prompt separately.
- Codex-specific **headers on the wire**: `x-codex-beta-features`, `x-codex-turn-metadata` (rich JSON: session/thread/turn ids, workspace git origin + commit + dirty flag), `x-codex-window-id`, `session-id`, `thread-id`, `x-client-request-id`, `originator: codex_exec`, `User-Agent: codex_exec/0.140.0-alpha.2 (...) WindowsTerminal`. The bridge will **replace** these with the official VS Code Copilot header set (as `/cc` does) — Copilot won't recognize `x-codex-*`.

**Caveat:** the capture server returned a minimal stub SSE; codex retried 6× and
exited non-zero (the stub didn't fully satisfy its turn loop). That does **not**
affect request-shape capture (the requests are what Track B needs) — but a true
end-to-end success belongs to the follow-up change's headless harness against the
real bridge. Captured under git `bf667c7` source / `0.140.0-alpha.2` exe.

### 3.5 Tool shapes Codex emits
`ToolSpec` enum, serialized `#[serde(tag="type")]` (`tools/src/tool_spec.rs:15-64`).
`create_tools_json_for_responses_api` just `serde_json::to_value`s each (`:78-89`).
Tool `type` discriminants on the wire:

| `type` | Variant | Note |
| --- | --- | --- |
| `function` | `Function(ResponsesApiTool)` | standard function tool — `{type:"function", name, description, parameters, strict}` (top-level name, Responses shape) |
| `namespace` | `Namespace` | grouped tools |
| `tool_search` | `ToolSearch{execution,description,parameters}` | server-side tool search |
| `image_generation` | `ImageGeneration{output_format}` | `copilot-api-anthropic` **drops** this for Copilot |
| `web_search` | `WebSearch{…}` | even OpenAI's own code has a TODO: "we get an error on web_search although the API docs say it's supported" (`:30-32`). Anthropic path: Copilot rejects web search. Track-A must-probe. |
| `custom` | `Freeform(FreeformTool)` | freeform/grammar tools incl. **`apply_patch`** — `{type:"custom", name, description, format:{type,syntax,definition}}`; `copilot-api-anthropic` **rewrites** `apply_patch` custom→function |

Track-A must establish which of these Copilot's `/responses` accepts vs rejects vs needs-rewritten (Task 2.6). Codex sends Responses-native function shape (top-level `name`/`parameters`), **not** the Chat `{type:function, function:{…}}` wrapper.

## 4. Synthesis — A ∩ B

### 4.1 Intersection table
What Codex sends (Track B) × what Copilot `/responses` accepts (Track A) → bridge action:

| Element | Codex sends (B) | Copilot accepts (A) | Bridge action |
| --- | --- | --- | --- |
| transport | HTTP `POST /responses` SSE (WS opt-in only, off for custom provider, §3.1) | HTTP `/responses` on all 6 models (§2.1) | **passthrough** — no WS to worry about |
| `model` | `model_info.slug` (§3.2); live: `gpt-5.3-codex` (§3.4) | `gpt-5.3-codex`, `gpt-5.4*`, `gpt-5.5`, `gpt-5-mini`, `mai-code-1-flash-internal` (§2.1) | map/validate slug → known model (profile catalog) |
| `input[].role` | `developer` for preamble + `user`/`assistant` (§3.4) | accepted (live capture turns 200 in probe shape) | passthrough |
| `reasoning.effort` | from `none/low/medium/high/xhigh` config (§3.2); `minimal` possible | **per-model** (§2.2): large models reject `minimal`; small models reject `none`+`xhigh` | **per-model coercion** (profile catalog) — not one global rule |
| `reasoning.summary` | sent when reasoning on (§3.2) | 200 (§2.3) | passthrough |
| `include:["reasoning.encrypted_content"]` | sent whenever reasoning on (§3.2) | 200 (§2.3) | **passthrough — multi-turn reasoning works** |
| `store` | `false` for non-Azure (§3.2) | `true`→400, but Codex sends `false` | passthrough (Codex's value is fine); strip only if ever `true` |
| `service_tier` | model-gated (§3.2) | **400** (§2.3) | **strip** |
| `prompt_cache_key` | always (§3.2) | 200 (§2.3) | passthrough |
| `tools: function` | Responses-native shape (§3.5) | 200 (§2.4) | passthrough |
| `tools: custom`/`apply_patch` | freeform (§3.5) | **200** on 5; **500** on flash (§2.4) | passthrough (no rewrite); flash can't do custom tools — profile note |
| `tools: web_search` | (§3.5) | 200 all 6 (§2.4) | passthrough |
| `tools: image_generation` | (§3.5) | **400** all 6 (§2.4) | **drop** |
| vision `input_image` | data-URL image part | 200 on 5 vision models (§2.6) | passthrough; set `Copilot-Vision-Request: true` |
| `tool_choice` | `"auto"` (§3.2) | 200 | passthrough |
| `stream` | always `true` (§3.2) | streams cleanly (§2.5) | passthrough |
| SSE response | parser tolerant, needs terminal `response.completed`, no `[DONE]` handling (§3.3) | ends at `response.completed`, **no `[DONE]`** (§2.5) | **passthrough — no DONE-filter needed** |
| headers `x-codex-*` | sent (§3.2) | not probed for rejection; Copilot generally ignores unknowns | passthrough; bridge adds its own official Copilot headers (replace, like `/cc`) |

### 4.2 Implementation-shape recommendation
**Codex support is a near-passthrough — structurally identical to the Claude Code
`/cc` path** (parse → coerce → inject official Copilot headers → forward to native
`/responses` → stream back). The hub-IR/translation machinery is NOT needed.
Coercions, all evidence-grounded:

1. **`reasoning.effort` — per-model profile** (§2.2), NOT one global rule:
   - large (`gpt-5.3-codex`, `gpt-5.4-mini`, `gpt-5.4`, `gpt-5.5`): reject `minimal` → map to `low`/drop.
   - small (`gpt-5-mini`, `mai-code-1-flash-internal`): reject `none` (drop) and `xhigh` (clamp `high`).
   This is the single biggest correction from full-matrix coverage — a 2-model probe wrongly read it as "drop minimal globally."
2. **strip `service_tier`** (§2.3; uniform 400 across all 6).
3. **drop `tools[].type == "image_generation"`** (§2.4; uniform 400 across all 6).

Everything else is byte-passthrough across all 6 models: `function`/`apply_patch`/`web_search`
tools, `input_image` vision (§2.6), `include:reasoning.encrypted_content`, `reasoning.summary`,
`prompt_cache_key`, `store:false`, the full SSE stream (incl. `function_call_arguments.*`).
**No `[DONE]` filter** (§2.5) and **no tool rewriting** (§2.4) — both of which the
`copilot-api-anthropic` reference does; probes show neither is necessary here.

**Per-model edge:** `mai-code-1-flash-internal` returns **500** on custom/`apply_patch`
tools (§2.4) — a server-side fault the bridge can't fix; the profile should flag it
as not-custom-tool-capable.

This is the verdict the loop produced — "passthrough + a per-model effort profile +
2 uniform strips," grounded in the full 6-model matrix, not extrapolated.

### 4.3 Follow-up implementation change — work items (sized to findings)
A separate change implements (each tied to a finding):

- **`/codex/v1/responses` endpoint** under `Endpoints/Codex/`, per-client prefix (parallels `/cc`). Sub-route only `/responses` for now; `/responses/compact` + `/memories/*` (§3.1) are **out of scope** unless a later capture shows Codex hits them against a custom provider (likely not — they're OpenAI-backend features).
- **DTOs** `Models/Responses/` from `references/openai-sdk-pkg/` (§0), registered in `Models/JsonContext.cs` (AOT). Minimal set: the `ResponsesApiRequest` fields (§3.2) + the SSE event types (§2.5/§3.3).
- **Per-model effort profile catalog** (the real work — §4.2 item 1): two profiles (large/small) keyed by model id from §2.2, plus the uniform strips (`service_tier`, `image_generation`) and the flash-no-custom-tools flag. Still far simpler than the Anthropic `ProfileAdjuster` (no thinking-shape coercion, no mid-conv-system fold).
- **Header build**: reuse the existing endpoint-agnostic `CopilotHeaderFactory` (no `/responses`-specific header beyond the official set; confirmed §1.4 + reuse in probe). Set `Copilot-Vision-Request:true` when `input_image` present (§2.6); set `x-initiator` per last input item (reference `responses/utils.ts`).
- **Streaming**: plain SSE passthrough (§2.5) — no DONE-filter, no transform; forward `function_call_arguments.*` verbatim.
- **Headless harness**: drive real `codex.exe` via `codex exec --json -c model_providers.<id>.base_url=.../codex` (Track-B capture, Task 3.4, still to run live).

**Explicitly NOT needed** (findings rule out): OpenAI-Chat↔IR translation; a streaming state machine; `[DONE]` filtering; `apply_patch` custom→function rewrite; WebSocket transport handling.

### 4.4 `pipeline-design.md` §3 correction
§3 assumed the OpenAI/Codex client needs OpenAI-**Chat**↔IR translation (its
6-translator hub-IR matrix). **Evidence refutes this**: Codex is Responses-only
(§1.1, confirmed in source — `WireApi` has one variant), Copilot exposes native
`/responses` (§1.2), and the probe shows near-passthrough (§4.2). The Codex
client is a **native-`/responses` passthrough path**, structurally parallel to
Claude Code → `/v1/messages`, not a translation target. `docs/pipeline-design.md`
§3 should be updated accordingly (tracked as a task in the follow-up change).
