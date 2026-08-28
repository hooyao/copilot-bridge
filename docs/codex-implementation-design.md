# Codex client — implementation design (for review)

> Status: **IMPLEMENTED** · 2026-08-05 (v3 — adds the model-metadata control plane)
>
> Implements the verdict of `docs/codex-protocol-research.md` (Track A 6-model
> live probe + Track B source-read + real `codex.exe` capture). OpenSpec change:
> `openspec/changes/add-codex-client/`. The inference path is shipped; the v3
> metadata addendum is implemented by
> `openspec/changes/add-codex-model-catalog/`. Companion to
> `docs/pipeline-design.md` (the framework contract).
>
> **v3 addendum:** the original Q6 conclusion (no `/codex/models`) became stale
> when Copilot began advertising context beyond Codex 0.144.x's bundled ceiling.
> Codex requests a provider catalog only for first-party/command-auth providers,
> so the bridge now exposes a separate `GET /codex/models?client_version=...`
> control plane and `config codex` writes command-backed discovery auth. This
> does not alter the T1–T4 inference architecture below.
>
> **v2 correction:** an earlier draft treated Codex as a Responses→Responses
> near-passthrough that skips the IR. **That was wrong** and violated the
> project's core architecture (`pipeline-design.md` §2.8, §3): **every inbound
> request is translated to the single IR (Anthropic Messages shape)**, runs the
> one shared IR pipeline, then is translated to whatever backend its model
> resolves to. This is not ceremony — it is the only thing that delivers the
> hard requirement below.

---

## 1. The requirement that dictates the architecture

**Cross-model substitution must work in both directions:**
- **Claude Code must be able to use GPT-5** (Anthropic-shape client → Copilot `/responses` backend).
- **Codex must be able to use Opus** (Responses-shape client → Copilot `/v1/messages` backend).

> These two are the **architectural driver** — the reason the IR cannot be
> skipped — but they are **not shipped in this change** (Q-B, §2). This change
> builds the translators + routing seam so they're reachable, and ships+verifies
> only the Codex→`/responses` cell. Naming them here keeps the architecture
> honest: the design must not foreclose them, even though we don't wire them yet.

A Codex request therefore **cannot** short-circuit straight to `/responses` — if
it did, you could never route it to an Anthropic backend. The request must land
in a backend-agnostic representation first, get routed by model, and only then
be shaped for the destination. That representation is the **IR = Anthropic
Messages shape** (`pipeline-design.md` §3.2). The "near-passthrough" the research
found is only true for the *one* path Codex(Responses)→Copilot(Responses); the
bridge is built to support all four cells (they share machinery through the IR),
even though only that one path ships now.

```
                         ┌─────────────── the single IR pipeline ───────────────┐
 Claude Code ─T:id──►    │                                                       │   ──T:id──► Copilot /v1/messages
 (/cc, Anthropic)        │   Pipeline<MessagesRequest>  (ModelRouterStage etc.)  │
                         │   routes by MODEL, not by client:                     │
 Codex ─T:Resp→IR──►     │     claude-* → CopilotAnthropic                       │   ──T:IR→Resp──► Copilot /responses
 (/codex, Responses)     │     gpt-*    → CopilotResponses                       │
                         └───────────────────────────────────────────────────────┘
        ▲                                                                                    │
        └────────────────────────── T:IR→Resp (out) ◄── T:Resp→IR (backend resp) ◄──────────┘
```

The IR is **always Anthropic shape**, regardless of which client came in or
which backend goes out. Codex is simply a new pair of edge translators plus a
backend strategy — all of which the framework already reserved slots for
(`pipeline-design.md` §3.3, the `OpenAiToBridge*` / `BridgeToOpenAi*` rows).

---

## 2. The translators this change adds (the real scope)

`pipeline-design.md` §3.3 inventories 6 non-identity translators. This change
implements the **OpenAI/Responses** ones — 2 on the client edge, 2 on the
backend edge:

| # | Translator | Direction | When it runs | Stateful? |
| --- | --- | --- | --- | --- |
| T1 | `ResponsesToIrInboundAdapter` | Codex `ResponsesRequest` → IR `MessagesRequest` | inbound, before the pipeline | no |
| T4 | `IrToResponsesOutboundAdapter` | IR response stream/body → Responses stream/body | outbound, after the pipeline | **yes** (SSE) |
| T2 | `IrToResponsesRequest` (inside the Responses strategy) | IR `MessagesRequest` → `ResponsesRequest` on the wire | upstream call to Copilot `/responses` | no |
| T3 | `ResponsesToIrResponse` (inside the Responses strategy) | Copilot `/responses` SSE/body → IR | upstream response | **yes** (SSE) |

- **T1/T4** are `IClientInboundAdapter<ResponsesRequest, MessagesRequest>` /
  `IClientOutboundAdapter<MessagesRequest>` — the Codex client edge. (Contrast
  Claude Code, whose adapters are identity because its shape *is* the IR.)
- **T2/T3** live **inside** `CopilotResponsesStrategy` (a new
  `IUpstreamStrategy<MessagesRequest>`), per design principle §4 ("translation
  between shapes happens at strategy boundaries"). The Anthropic backend strategy
  stays identity.

Four cells, who does what:

| Client → Backend | Inbound (client→IR) | Strategy (IR→backend→IR) | Outbound (IR→client) | This change? |
| --- | --- | --- | --- | --- |
| Claude Code → `/v1/messages` (exists) | identity | identity passthrough | identity | shipped (M1) |
| **Codex → `/responses`** (Codex's default) | **T1** | **T2/T3** | **T4** | ✅ **BUILD + VERIFY** |
| Codex → `/v1/messages` (Codex uses Opus) | T1 | identity passthrough | T4 | architecture-ready, **NOT built/tested this change** |
| Claude Code → `/responses` (CC uses GPT-5) | identity | T2/T3 | identity | architecture-ready in this change; **built + live-verified by a LATER change** (CC→gpt-5.5 tool translation — `CcOnGpt5HeadlessTests`, `docs/cc-to-gpt5-translation-verification.md`) |

**Scope decision (Q-B):** this change implements and verifies **only the Codex →
`/responses` cell** — Codex with its own gpt-5.x models. It builds the full T1–T4
translator set and routes by model in the shared IR pipeline, so the other two
cells (**Codex→Opus**, **CC→GPT5**) become a later "wire-up + test" step, **not a
rework**. We do not wire, test, or claim those cells now. *(Update: the **CC→GPT5**
cell was subsequently completed by the CC→gpt-5.5 tool-translation change — see the
`This change?` note on that row and `docs/cc-to-gpt5-translation-verification.md`;
that is a different change from the Codex one this document describes.)*

**Consequence to be honest about (drives Q-A verification):** because routing
happens *on the IR* (the router must see a backend-agnostic body to choose a
backend), even the common Codex→`/responses` path **round-trips through the
Anthropic IR**: `Responses →T1→ IR(Anthropic) →T2→ Responses`. A request that is
Responses-shaped end to end is translated to Anthropic and back. This is the
deliberate hub-IR cost (uniform stages + routing + cross-backend-for-free in
exchange for double-translation on same-shape paths). Its risk is **fidelity
loss** on Responses-specific constructs (encrypted reasoning echo,
`function_call.call_id`, tool-result structure). That risk is the #1 thing the
empirical verification (Q-A) must close — see §10.

---

## 3. Why the research still matters (it's the translator contract)

The research wasn't wasted by this correction — it *is* the spec for T1–T4:

- **§3 (what Codex sends)** = T1's input contract: the 13 `ResponsesRequest`
  fields, `input[]` items (incl. `developer` role, `input_image`), the 14 tool
  types, `reasoning.effort`/`text.verbosity`.
- **§2 (what Copilot `/responses` accepts)** = T2's output contract: per-model
  effort acceptance, `service_tier`/`store`/`image_generation` rejections, the
  SSE event sequence T3 must parse.
- **The per-model effort table** feeds T2 (IR effort → Responses effort, clamped
  to what the resolved model accepts).
- **§2.5 SSE sequence** = T3/T4's event grammar (`response.output_text.delta`,
  `response.function_call_arguments.*`, terminal `response.completed`, no `[DONE]`).

---

## 4. The hard part: shape translation (T1–T4 mappings)

This is where the work actually is. The IR is Anthropic Messages; Responses is
OpenAI-shaped. Mapping table (extends `pipeline-design.md` §3.4 with the
Responses-specific specifics the research nailed down):

| Concept | IR (Anthropic) | Responses (Codex/Copilot) | Translation notes |
| --- | --- | --- | --- |
| System prompt | top-level `system` | top-level `instructions` | direct move |
| Messages | `messages[].role/content[]` + provider provenance | `input[]` items | semantic role/content map; OpenAI provider records preserve valid id/phase/status, future siblings, and original developer/system positions. Source pushes provenance; destination pulls it. |
| Text content | `{type:text}` | `{type:input_text\|output_text}` | direct |
| Image | `{type:image,source:{base64\|url}}` | `{type:input_image,image_url:"data:…"}` | base64 → data URL |
| Tool def | `tools:[{name,input_schema}]` | `tools:[{type:function,name,parameters}]` | unwrap/rewrap; `input_schema`→`parameters`. **Source depends on client:** a Codex request carries tools in the `openai` bag (re-emitted verbatim + drops); a **Claude Code** request carries them as typed `ir.Tools` (no bag) → T2 emits `{type:function,name,description,parameters,strict:false}` from each, skipping `web_search_*` server tools. Bag wins when present, so the Codex path is byte-identical. |
| Tool call | content `{type:tool_use,id,name,input}` | `{type:function_call,call_id,name,arguments}` | id↔call_id; input(obj)↔arguments(json string) |
| Tool result | user content `{type:tool_result,tool_use_id,content}` + provider extras | `{type:function_call_output,call_id,output}` | Native Codex output is opaque and preserves string/object/array kind plus id/status/future siblings. Claude semantic text/image blocks use the separate capability-driven translation. T2 reads only what the IR states and never asks who sourced it. |
| Reasoning | `thinking:{type,budget_tokens}` + provider extras | `reasoning:{effort,summary,context,…}` + `include:[encrypted_content]` | effort stays semantic and profile-controlled; context/future siblings and complete reasoning input items ride provider extensions unchanged. |
| Stop reason | `end_turn\|max_tokens\|tool_use…` | `response.completed.status` / `incomplete_details` | lookup table |
| Streaming | `message_start`→`content_block_*`→`message_delta`→`message_stop` | `response.created`→`output_item/_text/_fn_args.*`→`response.completed` | **stateful** event-by-event state machine (both T3 and T4) |
| Hosted web search | *(no Anthropic equivalent)* | `web_search_call` output item + `response.web_search_call.in_progress/.searching/.completed` | Copilot runs web search SERVER-SIDE (a non-responses-lite gpt model emits a `{type:web_search}` tool; gpt-5.6 family is `use_responses_lite=true` and suppresses it client-side). T3 has no Anthropic block for it, so it rides the IR as a **bridge-internal marker** on a text block (`bridge_web_search_call` on content_block_start; `bridge_web_search_call_result`, the completed item with its `action`, on content_block_stop). T4 rebuilds the `web_search_call` output item + lifecycle from the markers; the CC→gpt edge scrubs them. Without this, T3 mis-mapped the item to an empty text block and swallowed the lifecycle — codex saw the answer but NOT the search (invisible, uncited). Verified end-to-end by `CodexWebSearchHeadlessTests` (ApiContract — real codex gpt-5.5 through a live bridge, asserting the relay from the four-file audit) + `CodexWebSearchRoundTripTests` (offline fixture round-trip). `url_citation` annotations are NOT carried — no real codex run has been observed emitting them. |

**This is genuinely the OpenAI↔Anthropic streaming translation `pipeline-design.md`
§3.1 always said was needed** — the same class of work as the reference impls'
`stream-translation.ts` / `responses-translation.ts`
(`references/copilot-api-anthropic/src/routes/messages/responses-translation.ts`
+ `responses-stream-translation.ts` do Anthropic↔Responses).

> **Q-A RESOLVED — reference is reference-only; our implementation must be
> empirically verified.** We may *read* the reference's mapping logic as prior
> art, but we do **not** trust it or port it blind. Every mapping rule in T1–T4
> ships with a verification against ground truth: unit fixtures built from our
> own captures/probes (research §2.5/§3.2/§3.4), plus a live `codex.exe`
> end-to-end run that proves the Responses→IR→Responses round-trip preserves
> what Codex needs. A reference behavior we can't reproduce/verify on live
> Copilot does not go in. See §10.

---

## 5. Per-model effort (research §2.2) — now a translation concern

The inverted profiles (large models reject `minimal`; small models reject
`none`+`xhigh`) are applied **inside T2** (IR→Responses request), after the IR
`thinking`/effort has been mapped to a Responses `reasoning.effort`. The
`CodexModelProfileCatalog` (table-driven, probe-sourced, never family-name
guessed) clamps the mapped effort to what the resolved model accepts. The former
`mai-code-1-flash-picker` custom-tool 500 quirk was removed after a 2026-08-28
live re-probe returned 200. `service_tier` / `store:true` strip plus the
`image_generation` drop also live in T2 (uniform across models).

> **2026-07/08 update — a third "xlarge" profile.** The `gpt-5.6` codename slots
> (`gpt-5.6-luna`/`-sol`/`-sol-fast`/`-terra`) are the first Codex models to
> accept the `max`
> effort tier: `xlarge` = large + `max` (`none/low/medium/high/xhigh/max`, reject
> `minimal`), live-probed in `ResponsesProbe.Gpt56_Effort_ReProbe`. Because `max`
> is accepted, Anthropic's top tier passes through verbatim on these instead of
> being clamped to `xhigh` (which is what the large profile does). Note the
> probe TRAP the sync skill warns of: the 400 body for `minimal` lists supported
> = `[none,low,medium,high,xhigh]` — omitting `max` — yet `max` live-probes 200;
> the advertised list lies, the probe is ground truth.

---

## 6. Routing / vendor dispatch

`BackendVendor.CopilotResponses` **already exists** (`RouteTarget.cs:24`) but is
unused. `CopilotModelRegistry.Resolve` (`CopilotModelRegistry.cs:25-31`) today
routes all `gpt-*`/`o3-*`/`o4-*` to `CopilotOpenAi` + `/chat/completions`.
Change: route the Codex/Responses model ids (`gpt-5.3-codex`, `gpt-5.4`,
`gpt-5.5`, `gpt-5-mini`, `gpt-5.4-mini`, `mai-code-1-flash-internal`) to
**`CopilotResponses` + `/responses`**.

> **Current routed set (the id list above is change-3's original set).** The 2026
> reconciliation retired `mai-code-1-flash-internal` (→ `mai-code-1-flash-picker`)
> and the 2026-07/08 reconciliations added the four `gpt-5.6` codenames
> (`gpt-5.6-luna` / `gpt-5.6-sol` / `gpt-5.6-sol-fast` / `gpt-5.6-terra`). The live
> `CopilotModelRegistry.ResponsesModelIds` allowlist is the source of truth;
> membership is still an explicit list, never a `gpt-` prefix takeover.

`gpt-5.6-sol-fast` is Copilot-only and marked "Internal only" in live metadata.
It is supported for explicit model selection, but is not synthesized into
`GET /codex/models` while the exact official Codex catalog omits the slug; the
metadata control plane deliberately never invents Codex-owned instructions.

The router runs once, on the IR, in the shared `Pipeline<MessagesRequest>`. It is
**backend-agnostic by construction** — it keys off the resolved model id, not the
client. That's *why* CC→GPT5 and Codex→Opus become free later: the router already
sends any `gpt-*` to `/responses` and any `claude-*` to `/v1/messages` regardless
of which client's adapter produced the IR. **This change only exercises the
Codex→gpt path**; we don't add tests or claims for the claude-from-codex or
gpt-from-claude-code directions, but we also don't special-case against them —
the routing seam treats them uniformly.

`Normalize` must no-op on `gpt-5.3-codex` (already dotted, no date suffix) —
verified during implementation (was Q5).

---

## 7. Endpoint + host wiring

- `Endpoints/Codex/CodexResponsesEndpoint.cs` — `POST /codex/responses`. Reads +
  audits body → deserializes `ResponsesRequest` → **`T1.AdaptAsync` → IR** →
  builds `BridgeContext<MessagesRequest>` → runs the **existing shared**
  `Pipeline<MessagesRequest>` → **`T4` adapts the IR response → Responses** →
  writes SSE/buffered back. Same audit/summary plumbing as `/cc`.
- `Endpoints/Codex/CodexModelsEndpoint.cs` —
  `GET /codex/models?client_version=...`. This is not an inference adapter and
  never enters the IR pipeline. It resolves the complete official catalog for
  the exact stable or prerelease client version—corroborated from the whole-version
  query and recognized Codex user-agent version—through process memory, a
  self-validating per-user disk record, then the fixed `openai/codex`
  `rust-v{version}` source. It overlays exact-slug live Copilot Responses
  availability plus validated context limits and returns a stable projected ETag.
  A cold exact-source failure returns non-2xx so Codex retains its bundled catalog.
  `Codex.ModelCatalog.Enabled` defaults to `true`; setting it to `false` omits
  only this route while leaving `POST /codex/responses` available.
- No count-tokens endpoint is required for Codex.
- The Codex strategy `CopilotResponsesStrategy` registers in the **same**
  `Pipeline<MessagesRequest>` strategy registry alongside
  `CopilotMessagesPassthroughStrategy`; the registry picks by
  `target.Vendor` (`CopilotAnthropic` vs `CopilotResponses`).
- `app.MapCodexResponses()` in `ServeCommand`; DI registers T1/T4 (Codex
  adapters), `CopilotResponsesStrategy`, `CodexModelProfileCatalog`, plus
  `app.MapCodexModels()` and the singleton source-cache/disk/overlay/projector services.
  The runner, the IR pipeline, and the Anthropic stages are reused only for
  inference; metadata remains outside that graph.

### 7.1 Catalog ownership and command auth

- Complete Codex-owned behavior comes from the exact canonical source URL
  `https://raw.githubusercontent.com/openai/codex/rust-v{client_version}/codex-rs/models-manager/models.json`;
  the bridge treats fast-moving entries as opaque `JsonElement` values and
  rewrites only the backend allow-list. It never tries a neighboring or latest tag.
- **Bundled fallback on confirmed absence.** OpenAI ships Codex clients before
  tagging the matching release, so a brand-new client's exact tag can
  legitimately not exist — Codex desktop reported `0.147.0` while
  `rust-v0.147.0` returned 404 (its `0.146.0` and `0.147.0-alpha.*` neighbours
  all resolved). That made the newest client, the one that most needs the
  calibrated limits, the only one guaranteed to get a metadata error. On a
  **definitive 404** with no validated entry, the bridge therefore projects one
  compile-time snapshot embedded in the executable, gated on
  `Codex.ModelCatalog.BuiltinFallbackEnabled` (default true). This does **not**
  relax the rule above: the snapshot is a single fixed reviewed artifact, and
  nothing searches or compares tags. Every non-404 outcome — timeout, transport
  failure, throttling, server error — still fails closed, because a 404 is
  positive information while a timeout is the absence of it.
- **The snapshot is never cached; the 404 is.** A bundled resolution carries no
  cache entry and is refused by the cacheability gate, so it can never be
  promoted to disk or replayed as a fresh hit — otherwise a compile-time
  snapshot would shadow the real tag once published. What is cached is the
  absence itself: a process-local, never-persisted observation with its own
  `AbsenceTtlHours` (default 6, required to be below `SourceTtlHours`). That
  collapses the re-fetch storm while keeping the fallback self-healing, since
  the real catalog is picked up within one TTL of being published. A validated
  entry, even a stale one, always outranks the snapshot, and the bundled
  outcome is logged distinctly with both the requested and captured versions.

- Codex prerelease builds intentionally truncate the `/models` query to three
  components but retain their complete version in `Codex Desktop/{version}`,
  `codex_cli_rs/{version}`, or headless `codex_exec/{version}` User-Agent. The bridge requires matching cores before
  using that complete identity; contradictory recognized identity fails before I/O.
- Copilot `/models` owns backend membership and limits. Current 1M-class entries
  report 1,050,000 total / 922,000 maximum prompt / 128,000 maximum output;
  Codex receives 1,050,000 total and an explicit 892,000 auto-compact threshold
  (85% of total context, rounded down, while retaining the lower prompt guard).
- `config codex` manages `[model_providers.copilot-bridge.auth]`. Its hidden
  `auth provider-token` command prints a stable public sentinel; the real
  GitHub/Copilot credential remains exclusively inside `AuthService`.
- Official source freshness defaults to 24 hours. TTL means “check whether the
  source changed”; it is not an expiry. After the TTL, ETag revalidation refreshes
  metadata on 304 and validates/atomically promotes changed bytes on 200. If
  GitHub is unavailable or a replacement is invalid, an exact-version validated
  memory/disk LKG is served stale. A cold miss returns a metadata error unless
  the source confirmed a 404 and the bundled fallback is enabled. The live Copilot
  overlay keeps a separate five-minute successful-result TTL. A failed live
  refresh establishes the exact process-local
  `Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds` deadline (default 300):
  polls before it serve the stale overlay or reviewed baseline without another
  upstream call/warning, and callers at expiry share one retry. Neither path turns
  a metadata outage into an inference outage.
- **Refresh the bundled snapshot at release time.** It lives at
  `src/CopilotBridge.Cli/Catalogs/Codex/Bundled/` with a `capture.json`
  recording the version, source URL, upstream ETag, and SHA-256; the loader
  re-checks that digest against the embedded bytes and fails at startup on a
  mismatch, so a hand-edited snapshot cannot masquerade as official. Re-vendor
  it from the newest official tag when cutting a release — it only applies to
  versions with no upstream tag, but a stale snapshot means a newer client gets
  an older baseline than necessary.

### 7.2 Source cache concurrency and persistence

- Microsoft `HybridCache` owns the process-memory level and combines same-version
  misses/revalidations into one factory execution while any caller still waits.
  Failed resolutions are not cached. Different versions may perform network,
  hashing, and validation work concurrently. Catalog operations disable
  `IDistributedCache`; the persistent file remains inside the factory so expired
  exact-version bytes are still available for stale-if-error. A trim-safe local-only
  serializer satisfies HybridCache's pre-L1 serialization step without introducing
  a second persisted representation; deserialization fails closed because L2 is
  forbidden for this cache.
- All persistent mutations across all versions use one process-wide async writer
  lock. The lock covers temporary-file creation/removal, atomic promotion,
  freshness-only replacement, and retention deletion. A waiter rechecks the
  destination or protected state after entering the lock.
- One binary record binds schema version, canonical client version, canonical
  source URL, optional ETag, exact-byte SHA-256, and UTC timestamps to the exact
  upstream bytes. Every restart revalidates framing, bounded lengths, identity,
  digest, and catalog schema before use.
- Defaults: 24-hour source TTL, 10-second fetch timeout, 4 MiB source limit,
  90-day inactive retention, and 32 inactive versions. `CacheDirectory` may be
  set only to an absolute path; otherwise the OS per-user cache directory is used.
  Cleanup protects the version that triggered it; HybridCache's private L1 key set
  is not mirrored solely to protect other inactive disk records.

---

## 8. DTOs (`Models/Responses/`) — typed semantic core plus opaque tail

Q2 **RESOLVED: fields T1/T2 interpret are fully typed; the open-ended provider
tail is opaque `JsonElement`.** T1/T2 actively read model, role/content, call
linkage and effort, so those remain source-generated typed properties. Responses
objects are nevertheless open unions: future siblings on a known discriminator
must not disappear merely because this build does not interpret them. Known DTOs
therefore retain extension fields and T1 pushes them into the multi-level OpenAI
provider bag. All dictionaries/records remain explicit in `Models/JsonContext.cs`;
there is no reflection serialization.

`ResponsesRequest` (13 fields, §3.2), `input[]` items (message/function_call/
function_call_output/reasoning), content parts (`input_text`/`input_image`/
`output_text`), `tools[]` variants by `type`, `Reasoning`, `TextControls`, and
the streaming event DTOs T3/T4 need.

---

## 9. ICopilotClient

`PostResponsesAsync(body, vision, overrides, ct)` — POST `{baseUrl}/responses`,
bearer + official VS Code Copilot headers (reuse `CopilotHeaderFactory`; drop
Codex's `x-codex-*`), streaming response. Mirror of `PostMessagesAsync`.

---

## 10. Testing & empirical verification (Q-A gate)

The reference impl is prior art only; **every translator rule must be verified
against ground truth before it's trusted.** Three layers:

- **Unit (CI, `tests/CopilotBridge.UnitTests`):** T1 Responses→IR mapping (each
  content/tool/reasoning shape from real captures), T2 IR→Responses (effort
  clamp per model, field strips, tool drop/keep), T3/T4 streaming state machines
  (event sequences from research §2.5/§3.3 as fixtures), stop-reason lookup,
  unknown-model error, DTO round-trips.
- **Round-trip fidelity probe (the Q-A gate, Integration-tagged):** feed a real
  captured Codex `ResponsesRequest` through `T1 → T2` and compare the independent
  inbound body to the emitted body at full JSON-value and array-order fidelity.
  The only allowed differences are an explicit reviewed set of routing/profile/
  protocol mutations backed by paired current Copilot probes. A captured
  `upstream-req` is evidence of what an old build did, never the request oracle.
- **Live end-to-end (`tests/CopilotBridge.Playground/Headless/`, Integration,
  ephemeral non-8765 port):** real `codex.exe` → `/codex` → Copilot `/responses`
  (the one cell this change ships), driven via `codex exec --json -c
  base_url=.../codex`; `~/.codex/config.toml` untouched. Assert a full turn —
  text **and** a tool call — completes and reaches Codex's stdout. This is the
  true end-to-end the research's capture stub could not be.

The other two cells (Codex→Opus, CC→GPT5) get **no tests this change** — they're
not wired. A `[Fact(Skip="next change")]` placeholder may mark them so the gap
is visible.

---

## 11. What changed from v1 (so the reviewer sees the correction)

| v1 (wrong) | v2 (this doc) |
| --- | --- |
| Codex = Responses→Responses passthrough, skips IR | Codex → **IR (Anthropic)** → backend, like every client |
| New `Pipeline<ResponsesRequest>` | **Reuse the one `Pipeline<MessagesRequest>`** IR pipeline |
| Codex adapters are identity / skippable (Q7) | Codex adapters are **real translators T1/T4** (Q7 dissolved) |
| "3 coercions + passthrough" is the whole job | 3 coercions are part of **T2**; the job is **4 shape translators** (T1–T4) |
| Scope: 1 endpoint + tiny coercion | Scope: the OpenAI↔Anthropic translation `pipeline-design.md` §3.3 reserved, + endpoint |
| Codex-uses-Opus impossible | Codex-uses-Opus + CC-uses-GPT5 both fall out of T1–T4 |

---

## 12. What we are NOT building

- Gemini translators (separate future client).
- WebSocket transport (custom providers default `supports_websockets=false`, research §3.1).
- `/responses/compact`, `/memories/*` routes.
- Generic OpenAI `/v1/models` emulation. `/codex/models` is Codex's native
  `ModelsResponse` contract, selected by `client_version`.
- `[DONE]` filtering on the Responses backend (Copilot `/responses` emits none, §2.5).

---

## 13. Open questions for the reviewer

| # | Question | Status / decision |
| --- | --- | --- |
| Q-A | Port the reference translation logic, or verify our own? | **DECIDED: reference is reference-only; our implementation must be empirically verified** — read it as prior art, but every T1–T4 rule ships with a capture/probe-backed test + live `codex.exe` round-trip. Unreproducible reference behavior is excluded. (§4, §10) |
| Q-B | All 4 cells, or stage it? | **DECIDED: build T1–T4 + the routing seam (architecture-ready for all cells), but implement & verify ONLY Codex→`/responses` this change.** Codex→Opus and CC→GPT5 are a later wire-up+test step, not a rework, and are not claimed now. (§2) |
| Q1 | Adjust `text.verbosity` when coercing effort? | DECIDED: No (independent, universally accepted) |
| Q3 | Strip `store` always or only when `true`? | DECIDED: only when `true` (Codex sends `false`) |
| Q5 | `Normalize` no-op on `gpt-5.3-codex`? | DECIDED: verify in code (my task, not a review item) |
| Q6 | `/codex/models` route? | **SUPERSEDED 2026-08-05: Yes, as a separate command-auth metadata control plane.** Required to raise Codex's bundled context ceiling while preserving complete client-owned model entries. |
| ~~Q7~~ | ~~identity adapters for Codex?~~ | **DISSOLVED** — Codex adapters are real translators T1/T4, not identity |

All review questions are now resolved. The remaining judgment for the reviewer
is whether the **scope line** (§2: build-for-all, ship-one) and the **round-trip
fidelity gate** (§10) are the right bar. If yes, this is ready to code.
