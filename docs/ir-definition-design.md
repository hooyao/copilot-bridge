# IR definition — final design (FROZEN)

> Status: **FROZEN 2026-06-15** (change `freeze-ir-provider-extensions`) ·
> drafted 2026-06-14 · see §10 for as-built notes
>
> Decision (yours, this session): keep our **hand-rolled Anthropic Messages DTOs
> as the IR**, and **steal Vercel AI SDK's `providerOptions` namespaced
> escape-hatch** to cover the one thing Anthropic-shape can't type: the
> provider-specific knobs OpenAI/Responses (and later Gemini) send. Then
> implement the Codex/Responses path and **prove, with real `codex.exe`
> end-to-end, that both Anthropic and Codex work perfectly on this IR.**
>
> Grounded in three parallel research reports this session (MEAI, LiteLLM,
> Vercel AI SDK) — see `docs/codex-protocol-research.md` for the Codex wire
> facts. This doc, once approved, is the contract `docs/pipeline-design.md` §3
> points to for the IR.

---

## 1. The decision and why (research-backed)

**IR = our hand-rolled Anthropic Messages shape + a namespaced provider-extensions bag.**

Three independent research tracks converged on this:

| Source | Finding that drove the decision |
| --- | --- |
| **MEAI** (`Microsoft.Extensions.AI` v10.7.0) | A *normalizing* IR modeled on **OpenAI-Chat semantics**. Every provider-specific knob (cache_control/ttl, thinking budget_tokens, store/service_tier/include, **all headers/beta/auth**) falls out of the typed model into stringly-typed `AdditionalProperties`. Tool args are a parsed `IDictionary`, **not byte-faithful**. Verdict: cannot *completely* replace a lossless Anthropic IR; same hand-mapping work, minus type safety, plus a 1.38 MB dep + AOT schema-gen warnings (IL2026/IL3050, dotnet/extensions #5626). |
| **LiteLLM** (OpenAI-shape as canonical IR) | Years of *structural* fidelity bugs **specifically because Anthropic doesn't fit OpenAI shape**: cache_control silently dropped (#27950/#23873/#30293…), thinking signatures lost across turns → hard 400s (#24985/#29518…), tool-call id format clashes (#21114/#22317…), tool_use/tool_result pairing corrupted by the round-trip (#23105/#26167…). Their own conclusion: a byte-level Anthropic passthrough **sidesteps all five bug classes** — only translate where unavoidable. |
| **Vercel AI SDK** | The reusable gem: **`providerOptions` / `providerMetadata`** — a `provider-name → opaque-JSON` bag attached at **request, message, part, tool, and stream-part** levels. Each emitter reads only its own key; unknown data is never dropped or mutated. This is the lossless-passthrough mechanism, and under .NET AOT it's just `Dictionary<string, JsonElement>`. |

**Why Anthropic-shape and not neutral:** Anthropic is the most expressive of the three (per-block cache_control with ttl, structured thinking with signature, multipolar content). Our M1 already models it as an AOT-clean `[JsonPolymorphic]` tagged-union with `JsonElement` tool inputs — i.e. the LiteLLM-recommended byte-faithful shape is **already built and shipped**. Keeping it means the **Claude Code hot path (≈99% of traffic) stays identity — zero translation, zero regression, no M1 rewrite.** A neutral or MEAI IR would impose a lossy translation pair on that hot path and force rewriting shipped, verified M1 stages — for no fidelity gain (the research shows neutral IRs are *less* faithful to Anthropic, not more).

**The one gap Anthropic-shape has, and the fix:** our `MessagesRequest` explicitly drops `service_tier`/`top_k`/`top_p`/etc. "because Claude Code doesn't send them." But **Codex/Responses does** send `store`/`service_tier`/`include`/`prompt_cache_key`/`reasoning.effort`/`text.verbosity`. Those have no typed home in the Anthropic IR → they'd be lost across `Responses →IR→ Responses`. **The `providerOptions` bag closes exactly this gap.** It's a small additive field on existing DTOs, not a new foundation.

---

## 2. What we already have (so the bag is a patch, not a rebuild)

From the current code (`Models/Anthropic/Request/`):

- `ContentBlockParam` is `[JsonPolymorphic(TypeDiscriminatorPropertyName="type")]` with `text`/`image`/`document`/`tool_use`/`tool_result`/`thinking`/`redacted_thinking` variants — **the tagged-union the research says to build.**
- `ToolUseBlockParam.Input` is `JsonElement` — **byte-faithful tool input** (LiteLLM's #1 lesson, already done).
- `ThinkingBlockParam { Thinking, Signature }` + `RedactedThinkingBlockParam { Data }` — **reasoning + opaque signature already modeled** (the thing MEAI needed `ProtectedData` for; we already have typed fields).
- `cache_control` is typed per-block (`CacheControl?` on every block).

So reasoning fidelity, tool-input fidelity, and cache_control are **already lossless on the IR**. The bag only has to carry the *non-Anthropic* knobs.

---

## 3. The escape hatch — `ProviderExtensions` (stolen from Vercel, AOT-shaped)

### 3.1 Shape

A single reusable type, attached at the levels that need it:

```csharp
// provider-name  →  opaque JSON object the bridge never interprets.
// AOT-safe: JsonElement copied verbatim, never a typed-DTO-per-provider.
internal sealed record ProviderExtensions
{
    // e.g. ProviderExtensions["openai"] = { "store": false, "service_tier": "default",
    //                                       "include": ["reasoning.encrypted_content"],
    //                                       "text": {"verbosity":"low"} }
    [JsonExtensionData] // or a plain Dictionary<string, JsonElement>, see §3.3
    public Dictionary<string, JsonElement> ByProvider { get; init; } = new();
}
```

### 3.2 Where it attaches (mirror Vercel's per-level placement)

| Level | Carries | Example |
| --- | --- | --- |
| **Request** (`MessagesRequest`) | request-wide provider knobs | `openai`: `{store, service_tier, include, prompt_cache_key, text.verbosity, reasoning.summary, additional_tools}` |
| **Message** (`MessageParam`) | per-message provider data | rarely needed; reserved |
| **Content part** (`ContentBlockParam`) | per-part provider data | `openai`: a Responses item's `id`/`encrypted_content` when not expressible as a thinking block |
| **Tool def** (`Tool`) | per-tool provider extras | future |

Request-level is the one this change actually needs; the others are defined for symmetry and future Gemini, but may ship empty.

### 3.3 Rules (copied from Vercel, adapted)

1. **Namespaced by provider** — a single IR can hold `openai` *and* `anthropic` keys; each emitter reads only its own and **ignores the rest**. No cross-contamination.
2. **Opaque to the core** — the pipeline/stages never parse the inner JSON. It's `JsonElement`, copied verbatim. (AOT-safe — no per-provider `[JsonSerializable]`.)
3. **Round-trip channel** — inbound adapter (T1) stashes un-modeled Responses fields into `ProviderExtensions["openai"]`; the Responses strategy (T2) reads them back out when building the wire request. Response-side metadata (encrypted reasoning ids etc.) ride back the same way.
4. **Every converter must copy it through** — Vercel's known bug (#5942/#9731: the bag got dropped in a layer converter) becomes our **mandatory round-trip test**: an unknown provider key must survive `inbound → IR → outbound` (§7).

> **AOT note:** prefer an explicit `Dictionary<string, JsonElement> ByProvider`
> property (registered in `JsonContext`) over `[JsonExtensionData]` magic, so the
> serialization is source-gen explicit. Decide during implementation; both are
> AOT-clean with `JsonElement` values.

---

## 4. How Codex/Responses uses the IR + bag (the validation target)

The four translators from `docs/codex-implementation-design.md` (T1 inbound, T2/T3 strategy, T4 outbound) now have a precise contract for the un-modeled tail:

**T1 — Codex `ResponsesRequest` → IR (Anthropic `MessagesRequest`):**

| Responses field | → IR home |
| --- | --- |
| `model` | `Model` |
| `instructions` | `System` (top-level) |
| `input[]` messages | `Messages[]` (developer→system, role map) |
| `input_text`/`input_image` parts | `TextBlockParam` / `ImageBlockParam` |
| `function_call` / `function_call_output` | `ToolUseBlockParam` / `ToolResultBlockParam` (call_id ↔ Id, byte-faithful via `JsonElement`) |
| `reasoning.effort` | `OutputConfig.Effort` (existing) |
| `reasoning` item + `encrypted_content` | `ThinkingBlockParam`/`RedactedThinkingBlockParam` where it maps; else part-level bag |
| `tools[]` (function/custom/web_search/…) | `Tools[]`; un-Anthropic tool shapes → preserved in bag |
| `input[]` `additional_tools` item (gpt-5.6 harness preamble) | **`ProviderExtensions["openai"].additional_tools`** (verbatim; T2 re-emits it into `input[]`, ahead of messages) |
| **`store`, `service_tier`, `include`, `prompt_cache_key`, `text.verbosity`, `parallel_tool_calls`** | **`ProviderExtensions["openai"]`** (verbatim) |

**T2 — IR → Responses wire (inside the strategy):** rebuild a `ResponsesRequest` from the IR, then **re-apply `ProviderExtensions["openai"]`** verbatim, then apply the probe-derived coercions (per-model effort clamp, strip `service_tier`, drop `image_generation` — `docs/codex-protocol-research.md` §4). The bag is what makes `store=false`/`include`/`prompt_cache_key` survive the Anthropic round-trip.

**T3/T4 — response side:** Copilot `/responses` SSE → IR stream events → Responses SSE back to Codex; opaque reasoning ids/encrypted content ride the part-level bag.

**This is the perfect-fidelity claim we must prove:** a real Codex request, run `Responses →T1→ IR →T2→ Responses` (even when it stays on the Responses backend), comes out carrying everything Codex needs — because what the Anthropic IR can't type, the bag carried verbatim.

---

## 5. Hot-path guarantee (Claude Code stays identity)

The bag is **additive and optional**. Claude Code's path:
- T(in) = identity (Anthropic shape *is* the IR), as today.
- `ProviderExtensions` is `null`/empty for CC requests → serializes to nothing → **byte-identical to today's output**.
- No M1 stage changes behavior; `ModelRouterStage`, sanitizers, `ProfileAdjuster` all operate on the same `MessagesRequest`.

**Regression gate:** the existing playground/headless Anthropic suite must pass **unchanged** after adding the bag. If any CC request serializes differently, the bag leaked into the hot path — that's a bug.

---

## 6. What freezes (the IR contract)

On approval, these are **frozen** as the IR:

1. `MessagesRequest` + `MessageParam` + `ContentBlockParam` tagged-union (existing) = the IR body shape.
2. `ProviderExtensions` bag (new) at request + content-part levels = the lossless tail.
3. Reasoning = `ThinkingBlockParam{Thinking,Signature}` / `RedactedThinkingBlockParam{Data}` + `OutputConfig.Effort` (existing); opaque blobs (Responses `encrypted_content`) ride `Signature`/`Data` where shaped, else the bag.
4. Tool input/result = `JsonElement` (byte-faithful, existing).
5. Streaming IR = the existing Anthropic SSE event model (`message_start`→`content_block_*`→`message_delta`→`message_stop`); translators map provider SSE to/from it.

Anything a future provider sends that doesn't fit 1/3/4 goes in the bag (2). The IR body shape itself does not grow per-provider fields.

---

## 7. Validation plan — parity & round-trip fidelity (the heart of this design)

The IR is only justified if **two double-translations preserve fidelity**:

```
 REQUEST parity:    client request  →T1→ IR →T2→ Copilot request
 RESPONSE parity:   Copilot response →T3→ IR →T4→ client response
```

If either loses something the client/backend needs, the IR is wrong. This
section is exhaustive because it is the make-or-break of the whole approach.

### 7.0 Test philosophy — captured traces are REFERENCE, never ground truth

**A captured trace is a snapshot of one moment, not a contract.** Copilot's
`/responses`, the alpha `codex.exe` (0.140), and even Anthropic all keep
changing — `service_tier` rejected today, `[DONE]` absent today, the SSE event
names today are *current facts, not eternal ones*. If we froze a captured trace
as a golden "ground truth", green unit tests would only prove **"our code still
matches the 2026-06-12 capture"**, not **"our code works against Copilot now."**
That is the most dangerous false positive: Copilot adds a field or renames an
event, the suite stays green, the live bridge breaks.

So the suite splits into **two kinds with different sources of truth**:

```
A. INVARIANT tests (trace-driven, offline, CI)
   Ground truth = mathematical properties of OUR translators, NOT any capture.
   A trace is only "a real-shaped input sample." Asserts T2∘T1 ≡ identity,
   byte-passthrough of opaque fields, bag survival, hot-path byte-equality.
   These hold no matter how Copilot changes → traces never expire as inputs.

B. CONTRACT tests (live-driven, Integration, re-runnable + drift-detecting)
   Ground truth = the REAL Copilot + REAL codex.exe, right now.
   Asserts what Copilot /responses currently accepts/rejects, and that a real
   codex turn completes. These SHOULD go red when upstream changes — that red
   is the signal. Captured traces do NOT participate here.
```

The split is the correction: **invariant tests must not encode "what Copilot
currently does"; only live contract tests answer that, and they must be able to
go red.** A "golden" below encodes ONLY our own translation rules (self-inverse),
never Copilot's current behavior. The §7.1 "expected transform" goldens version
*our* coercion code; whether that coercion is still *needed* is re-checked live
in §7.4 (B), not frozen.

Mapping of the subsections below to A vs B:
**A (invariant):** §7.1 bar, §7.3 (A1–A6), §7.5 (H1–H2).
**B (live contract):** §7.4 (B1 acceptance, B2 **drift detection**, B3
coercion-still-needed), §7.6 (E1/E2 live e2e).
§7.2 is the **input-sample corpus** for A — real-shaped bytes to push through
our translators, NOT an oracle of Copilot correctness.

### 7.1 The fidelity bar — what "preserved" means (per field class)

Not every field must be byte-identical (some legitimately change — model id
normalization, our 3 probe-derived coercions). So the bar is **per field class**:

| Field class | Bar | Rationale |
| --- | --- | --- |
| Tool-call `input`/`arguments`, tool-result content | **byte-identical** | held as `JsonElement`; reparse-reserialize must not reorder keys or reformat numbers (LiteLLM's #1 bug class) |
| Reasoning opaque blobs (`signature`, `encrypted_content`) | **byte-identical** | opaque tokens; any mutation → upstream 400 |
| `instructions`/`system`, message text, roles, image data | **semantically equal** | structural move (system↔instructions, developer→system) allowed; content bytes preserved |
| `tool_call_id`/`call_id` linkage | **referential integrity** | the id may be reformatted IF every reference is remapped consistently; pairing tool_use↔tool_result must survive |
| Bag-carried knobs (`store`, `service_tier`, `include`, `prompt_cache_key`, `text.verbosity`) | **byte-identical through the bag, THEN documented coercion** | must arrive at T2 intact; T2 may then strip/clamp per `codex-protocol-research.md` §4 — and the test asserts the *coercion*, not loss |
| Intentional transforms (model normalize, effort clamp, `service_tier` strip, `image_generation` drop) | **asserted as the expected transform** | a golden expectation, not "unchanged" |
| Copilot extension fields on responses (`copilot_usage`, bedrock metrics) | **tolerated, passed through** | client SDKs ignore unknown fields |

A **field-diff harness** (extend the existing `ApiComparisonTests` JsonNode
differ, `tests/CopilotBridge.Playground/ApiComparisonTests.cs`) classifies every
diff into: `identical | allowed-transform | VIOLATION`. Only VIOLATIONs fail.
The allowed-transform list is an explicit, reviewed allowlist — anything not on
it that differs is a failure (no silent tolerance).

### 7.2 Real fixture corpus (committed, de-identified)

Tests need real bytes checked into the repo. Source → fixture pipeline:

| Fixture | Captured from | De-identification |
| --- | --- | --- |
| `codex-request-*.json` (plain turn, tool-call turn, multi-turn) | the 6 real captures in `docs/scratch/codex-capture/*.txt` (Track B, `codex_exec/0.140.0-alpha.2`) | strip `session-id`/`thread-id`/`x-codex-turn-metadata` git paths → placeholders; keep body shape verbatim |
| `responses-sse-*.txt` (text stream, tool-call stream) | `ResponsesProbe.Streaming_CaptureEventSequence` raw SSE (Track A, live `gpt-5.3-codex`) | none needed (no PII); trim to representative event set |
| `cc-request-*.json` + `cc-anthropic-sse-*.txt` | a real `claude.exe` run through `BridgeFixture` → `request-traces/` four-file audit | redact auth headers (already `<redacted>` by the sink) |

A small `tests/.../Fixtures/` dir holds these as **committed** assets, plus a
documented `refresh` procedure (re-capture + re-de-identify) for when Codex/CC
versions bump. The capture scripts stay in `docs/scratch/` (gitignored); the
de-identified fixtures are committed test data. **Fixtures are stamped with the
client version + capture date** so a stale fixture is visible.

### 7.3 Request-parity tests (client → IR → Copilot)

`CodexRequestParityTests` (unit, CI — pure functions, no network):

- **R1 round-trip golden.** For each `codex-request-*.json`: run `T1 → IR → T2`,
  compare the emitted Copilot `/responses` body against a **golden expectation
  file** (committed). The golden is the input with ONLY the documented
  transforms applied (effort clamp, `service_tier` strip, `image_generation`
  drop). Any other diff = VIOLATION.
- **R2 byte-fidelity of opaque fields.** Assert `function_call` arguments,
  `apply_patch` custom tool body, and any `reasoning.encrypted_content` are
  **byte-identical** input→output (JsonElement raw-text compare, not value
  compare). This is the LiteLLM-bug guard.
- **R3 bag survival (canary).** Inject `ProviderExtensions["openai"]["__canary__"] = {nested:[1,2]}`
  in the IR mid-pipeline; assert it emerges byte-identical at T2. Guards Vercel's
  drop-the-bag bug (#5942/#9731).
- **R4 bag-carried knobs.** Assert `store`/`include`/`prompt_cache_key`/`text.verbosity`
  ride the bag through the Anthropic IR and reappear at T2 (then `service_tier`
  is stripped *by the documented coercion*, asserted explicitly).
- **R5 tool-pairing integrity.** Multi-turn fixture with `function_call` +
  `function_call_output`: assert call_id linkage and ordering survive the round
  trip (LiteLLM bug class #4).

### 7.4 Response-parity tests (Copilot → IR → client)

`CodexResponseParityTests` (unit, CI — SSE fixtures, no network):

- **P1 stream round-trip golden.** Feed `responses-sse-*.txt` through `T3 → IR →
  T4`; assert the re-emitted Responses SSE event sequence matches a golden
  (event types in order, deltas concatenate to the same text, terminal
  `response.completed` present, no spurious `[DONE]`). Built from the real event
  grammar in `codex-protocol-research.md` §2.5.
- **P2 tool-call arg fragment fidelity.** The tool-call stream fixture's
  `function_call_arguments.delta` fragments must concatenate to **byte-identical**
  JSON after the round trip (streaming fragments are where parsed-dictionary IRs
  lose fidelity — we hold raw).
- **P3 reasoning blob survival.** A response carrying `encrypted_content` must
  round-trip it byte-identical (this empirically answers F4: `Data` slot vs bag).
- **P4 usage/extension passthrough.** `copilot_usage` and other extension fields
  survive to the client (tolerated-passthrough class).

### 7.5 Hot-path no-regression (Claude Code must be untouched)

- **H1 byte-identical CC output.** Replay `cc-request-*.json` through the full
  `Pipeline<MessagesRequest>` **before and after** adding the bag; assert the
  serialized upstream body is **byte-identical**. Proves the bag is inert on the
  hot path (empty bag → emits nothing).
- **H2 existing suite green.** The entire current Anthropic playground + unit
  suite passes **unchanged**. No test edits permitted to make them pass.

### 7.B Live contract + drift detection (B-tests) — ground truth = Copilot NOW

R1–P4 and H1–H2 above are **A-invariant** tests: they prove our translators are
self-consistent, using traces only as input shapes. They deliberately say
nothing about whether Copilot still behaves the way it did when we captured. That
question is answered **only** here, live, every run — and these tests **must be
able to go red when upstream changes.** Captured data is not used.

**Symmetry — Copilot is the drift source, BOTH backends need a snapshot.**
Copilot is the same evolving upstream behind *both* clients: it serves Claude
Code via `/v1/messages` **and** Codex via `/responses`. Drift detection must
therefore be **per-backend, not per-client.** This is a real gap in the current
codebase that this change fixes: today `ModelProfileProbe` (the `/v1/messages`
probe) is **print-only** — exactly like `ResponsesProbe` was — so the Anthropic
side has **no drift alarm either**, even though Copilot has already drifted there
repeatedly (see `docs/bug-mid-conversation-system-messages-dropped.md`,
`docs/copilot-upstream-toolcall-bug-report.md`, and the catalog's own note that
effort acceptance was *manually* re-probed 2026-06-05 after "Copilot widened it").
Each of those was a drift caught by luck, not by a test. We make it symmetric:

| Backend | Probe (promote print → assert) | Contract snapshot | Guards |
| --- | --- | --- | --- |
| Copilot `/v1/messages` (Anthropic) | `ModelProfileProbe` | `docs/copilot-anthropic-contract-snapshot.json` | `ModelProfileCatalog` (effort/thinking/mid-conv-system/betas) |
| Copilot `/responses` (Codex) | `ResponsesProbe` | `docs/copilot-responses-contract-snapshot.json` | Codex effort profiles + the 3 coercions |

- **B1 acceptance contract (promote both probes from print → assert).** Today
  *both* probes only `_output.WriteLine`. Promote each to **assert** its live
  facts and persist a committed snapshot per the table. The snapshot is "what
  Copilot did on date X" for that backend.
- **B2 drift detection — the mechanism your correction demands, for BOTH
  backends.** Diff each live B1 result against its committed snapshot; **any
  difference FAILS with a diff** ("Copilot now ACCEPTS `service_tier`",
  "`claude-haiku-4.5` now accepts adaptive thinking", "new event `response.foo`",
  "opus effort widened to include `max`"). This converts "Copilot changed" from a
  *silent* breakage into a **red test → a one-line snapshot update → a code
  review of whether `ModelProfileCatalog` / the Codex profiles / the coercions
  must change.** The 2026-06-05 "Copilot widened effort" episode would have been
  an automatic red instead of a lucky manual catch.
- **B3 catalog-still-correct (both sides).** The wire-truth our code bakes in —
  `ModelProfileCatalog` on the Anthropic side, the Codex effort profiles + 3
  coercions on the Responses side — is asserted against the **live** B1 result,
  not a frozen golden: "strip `service_tier` BECAUSE the live probe still 400s";
  "opus accepts only `medium` BECAUSE the live probe says so." If B2 shows the
  live truth moved, B3 turns red and names exactly which catalog row / coercion
  to reconcile.

Run cadence: B-tests are `[Trait("Category","Integration")]` (skipped in CI,
which has no Copilot creds), run on demand + on a schedule (the user re-runs when
they suspect upstream drift, or periodically). A green A-suite with a stale
B-snapshot is the *expected* state between drift checks; B2 going red is the
prompt to reconcile. Promoting `ModelProfileProbe` is **in scope for this change**
even though it guards the existing Anthropic path — because the drift machinery
is shared, and leaving the hot path without a drift alarm while adding one for
Codex would be the wrong asymmetry.



- **E1 Codex round-trip live.** Real `codex.exe` → `/codex/responses` → Copilot
  `/responses`, via `codex exec --json -c base_url=.../codex` (ephemeral
  non-8765, `~/.codex/config.toml` untouched), `[Trait("Category","Integration")]`.
  Assert a full turn — text **and** a forced tool call — completes and reaches
  Codex's stdout JSONL. The bridge's own IO audit (four-file capture) is saved so
  the **actual** client→IR→Copilot and Copilot→IR→client bytes can be diffed
  post-hoc against the unit golden — closing the loop between unit fixtures and
  live behavior.
- **E2 CC live unchanged.** A real `claude.exe` turn through `/cc` still works
  (smoke), confirming H1/H2 hold against the live client, not just fixtures.

### 7.7 What this proves — and how it stays true as Copilot changes

Two claims, two sources of truth, so neither rots:

- **A-invariant (R1–P4, H1–H2)** proves *our translators are internally
  faithful* — self-inverse round-trips, byte-passthrough of opaque/tool fields,
  the bag carries the un-modeled tail, the hot path is byte-unchanged. This
  **stays valid forever as Copilot evolves**, because A never asserted Copilot
  behavior — only the math of our own code, fed real-shaped samples.
- **B-contract (B1–B3) + live e2e (E1–E2)** proves *we match the live upstream
  right now*, **for both Copilot backends** (`/v1/messages` and `/responses`),
  and **B2 drift detection guarantees that when Copilot changes — on either
  backend — we get a red test, not a silent lie.** This closes a gap that
  predates Codex: the Anthropic hot path also gets its first drift alarm.

That division is the answer to "你不能守着某一时刻的抓包当 ground truth": we
don't. Captures seed A as input *shapes*; the live backend is the only oracle for
B; and B2 alarms when the live truth moves. "Both Anthropic and Codex work
perfectly on this IR" is therefore evidenced two ways — invariantly (A) and
against the moving target (B) — not asserted, and not pinned to a dead snapshot.

---

## 8. Open questions for the reviewer

| # | Question | Leaning |
| --- | --- | --- |
| F1 | `ProviderExtensions` as explicit `Dictionary<string,JsonElement>` property vs `[JsonExtensionData]`? | Explicit property in `JsonContext` (source-gen explicit, AOT-clean) |
| F2 | Bag at content-part level this change, or request-level only (parts deferred to Gemini)? | Request-level now; define part-level type but ship it only where Codex needs it (encrypted reasoning) |
| F3 | Do we rename the IR conceptually (docs say "IR", code says `MessagesRequest`) to avoid confusion, or keep `MessagesRequest` as-is and just document "this IS the IR"? | Keep `MessagesRequest`; document it's the IR (renaming churns shipped code for no behavior change) |
| F4 | Reasoning `encrypted_content` from Responses: force-fit into `RedactedThinkingBlockParam.Data`, or carry in the part-level bag? | Try `Data` first (it's literally an opaque blob slot); fall back to bag if round-trip loses structure — decided by the §7 round-trip test |

F4 is the one with real fidelity stakes; it's answered empirically by the round-trip test, not by guess. The rest are conventions.

---

## 9. Relationship to the other docs

- `docs/codex-protocol-research.md` — the Codex/Copilot wire facts (frozen, verified). Unchanged.
- `docs/codex-implementation-design.md` — the T1–T4 translator + endpoint design. **This doc supersedes its IR/DTO sections** (§5, §8): the IR is Anthropic-shape + bag, not a new shape; reasoning/tool fidelity comes from existing DTOs + bag.
- `docs/pipeline-design.md` §3 — updated to name Anthropic-shape-as-IR + the `ProviderExtensions` bag as the official IR contract (done with `freeze-ir-provider-extensions`).

---

## 10. As-built notes (FROZEN 2026-06-15 — change `freeze-ir-provider-extensions`)

The IR is frozen. Open questions resolved as built:

- **F1 resolved → explicit property, and it was forced.** `ProviderExtensions`
  is `Models/Common/ProviderExtensions.cs`, a record with an explicit
  `Dictionary<string,JsonElement> ByProvider` property serializing as
  `"provider_extensions":{"by_provider":{"<provider>":{…}}}`. `[JsonExtensionData]`
  (the flat-shape alternative) is not merely dispreferred but **impossible** here:
  STJ throws `ExtensionDataCannotBindToCtorParam` when the extension-data
  dictionary also binds a record primary-constructor parameter. The wrapper
  segment is harmless — this field never reaches a real upstream wire (null on
  `/cc`; consumed and discarded by Codex's T2).
- **F2 resolved → shipped at both levels.** Request-level (`MessagesRequest`) and
  part-level (`ContentBlockParam` base record). Part-level ships unused by Claude
  Code; Codex/Gemini populate it later.
- **AOT** — publish is **0 warnings**; the `JsonElement` bag is source-gen-clean.
  Binary delta **+26 KB** (`docs/size-history.md`).
- **Hot path** — H1 proves `/cc` upstream bytes are byte-identical (the bag is
  inert when null). H2: the full existing suite passes unchanged.
- F3/F4 unchanged from §8 (F3: keep `MessagesRequest` as the IR; F4: decided
  empirically by Codex's change-3 round-trip tests, not here).

### 10.1 Known pre-existing fidelity gap surfaced by A1 — `input_schema` (NOT fixed here)

The A1 round-trip self-inverse test surfaced a **pre-existing** lossy mapping
(not introduced by this change): `InputSchema`
(`Models/Anthropic/Request/Tool.cs`) models only `type` / `properties` /
`required`, so other JSON-Schema keywords a real Claude Code tool definition
carries — `$schema`, `additionalProperties`, … — are dropped when the IR is
serialized upstream. This was already true in production: committed
`request-traces/*-upstream-req.json` captures show the bridge has always sent
`input_schema` with keys `[type, properties, required]` only, and Copilot accepts
the reduced schema.

It is **out of scope for this change** because making `input_schema`
byte-faithful (hold it as a whole `JsonElement`, like `ToolUseBlockParam.Input`
already is) would **change the `/cc` upstream bytes** — directly violating this
change's H1 byte-equality gate. So A1 classifies the dropped keywords as a
documented *allowed-transform* (keeping A1 honest about what we actually do), and
the fix is logged here as a **follow-up**: a future change should make tool
`input_schema` lossless, since byte-faithful tool schemas are exactly the
LiteLLM #1 lesson (§1) this IR design is built around. Tracked in memory
(`reference_input_schema_lossy_modeling`).
