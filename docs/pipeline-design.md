# Bridge Pipeline Design

> Status: v0.6 · 2026-08-05
>
> This document is the architectural contract for the bridge's request/response
> transformation framework. New stages, new clients, new backends should
> conform to the abstractions defined here. Diverging requires updating this
> document first.
>
> **2026-08-05 (v0.6) — Codex metadata is a separate control plane**:
> `GET /codex/models?client_version=...` now serves complete, version-reviewed
> Codex `ModelInfo` entries. Codex-owned behavior (instructions, tools, picker
> metadata, review override) comes from an embedded upstream Codex baseline;
> backend-owned availability and safe context limits come from a bounded,
> process-local Copilot `/models` overlay. The endpoint does not enter the
> request/response IR pipeline. Command-backed provider auth makes Codex request
> the catalog without exposing the real Copilot token. See §4.9 and §7.6.
>
> **2026-06-01 (v0.5) — routing config redesigned nginx-style**: the flat
> `Routing.Rules` (`Match` → `Rewrite.Model`) list became `Routing.Locations`,
> each a self-contained `When` (a `MatchExpression` tree: `AllOf`/`AnyOf` +
> `Model`/`Effort`/`Header` leaves) → `Use` (change-set: `Model` swap,
> per-target `EffortMap`, whitelisted header `Set`/`Remove`). First-match-wins,
> no chain. Header overrides are limited to an allow-list
> (`anthropic-beta`, `Editor-Version`, `Editor-Plugin-Version`,
> `Copilot-Integration-Id`, `X-GitHub-Api-Version`). Also: per-request IO
> tracing is now **opt-in** (`Tracing.Enabled`, default off) and writes to
> `request-traces/` (renamed from `logs/`); the global `advisor-tool-*` beta
> strip keeps Claude Code 4.8 working against Copilot. See §7, §9.
>
> **2026-05-31 (v0.4) — routing redesigned around per-model profiles**:
> Wire-truth moved out of `CopilotModelRegistry.EffortAware` (deleted) and
> the live `/models` `CopilotModelCatalog` (deleted) into hand-curated
> `ModelProfileCatalog` entries grounded in playground probes. `Routing.Rules`
> in `appsettings.json` collapsed to model-redirect only; everything else
> (effort coercion, thinking shape coercion, mid-conv-system fold, beta
> strips) lives as data on the target profile and runs through the new
> `ProfileAdjuster`. Unknown models surface as `UnknownModelException` → 400
> + Anthropic error body instead of silent passthrough. See §7.
>
> **2026-05-08 (v0.3) — three migrations**:
> (a) Routing redesigned. Per-model effort behavior was C# capability data
>     in `CopilotModelRegistry.EffortAware`; `appsettings.json` kept
>     user preferences as `Routing.Rules` (Match → Rewrite). Superseded by
>     v0.4. (b) Logger swapped from the bespoke `DiagTracer`
>     (`[Conditional("BRIDGE_DIAG")]`) to Serilog 4.3.1 (AOT-clean since
>     PR #2175) with console + per-startup file sinks. See §9.
>     (c) `Program.cs` uses `System.CommandLine` 3.0-preview.3 for argument
>     parsing; subcommand handlers are typed entry points instead of
>     `string[]` parsers.
>
> **2026-05-07 (v0.2) — pipeline migration landed**: M1's `/v1/...` endpoints
> deleted; `/cc/v1/...` is the production path; six request stages + one
> response stage assembled in `Hosting/BridgePipelines.cs`.

## 1. Goals

The bridge is **not** "a Claude Code → Copilot proxy" — it's a generic LLM
reverse proxy with a fixed scope: 御三家 (the big three) clients fronting the
two backend shapes Copilot exposes. M1 ships with one client (Claude Code) and
one backend route (Copilot Anthropic), but the architecture is built for the
full set:

- **3 clients**, each on its own URL prefix:
  - Claude Code (Anthropic Messages API) on `/cc/...`
  - Codex (OpenAI Chat Completions / Responses) on `/codex/...`
  - Gemini CLI (Google Gemini API) on `/gemini/...`
- **2 effective backends on Copilot** (not 3 — Copilot does not expose a native
  Gemini-shape endpoint; Gemini models are served through the OpenAI shape):
  - Copilot Anthropic at `/v1/messages`
  - Copilot OpenAI at `/chat/completions` (and `/responses` for o-series)
- **Model substitution**: a client requesting a model the backend doesn't have
  reroutes to an equivalent (e.g. Claude Code asks for `claude-opus-4.8` but
  Copilot only has 4.7 → bridge maps without the client noticing).
- **Bidirectional transformation with feature preservation**: thinking, prompt
  caching, vision, tool calls — preserved across shape boundaries wherever the
  destination has an equivalent. Lossy mappings emit a diag warning, never fail
  silently.
- **Streaming SSE everywhere**: both directions are usually streaming. State-
  ful translators are unavoidable; we centralize where they live.

## 2. Design principles

1. **Strongly typed where possible** — bodies flow through pipelines as typed
   records (`MessagesRequest`, `ChatCompletionsRequest`, etc.), not `JsonNode` or
   `object`. AOT requires it; debuggability rewards it.
2. **One pipeline per (client shape, backend shape) pair** — not one universal
   pipeline. Each pipeline is a typed sequence of stages plus a strategy.
3. **Stages are pure, named, single-purpose** — each stage has a `Name`, mutates
   the context, and is composable in any order the assembly file specifies.
   Stages don't talk to each other except through the context.
4. **Translation between shapes happens at strategy boundaries**, not as a
   middle-of-pipeline stage. A pipeline always operates on one body shape
   end-to-end. If the backend requires a different shape, the strategy owns the
   request and response translators internally.
5. **Routing is data, not control flow** — a `RouteTarget` is selected by the
   `ModelRouterStage` based on the requested model + a registry; the
   `StrategyRegistry` then picks the matching `IUpstreamStrategy`. No `switch`
   statements scattered through the codebase.
6. **Two log channels** — per-request inbound/upstream IO is captured (when
   `Tracing.Enabled`, default off) as four discrete JSON artifacts
   (`request-traces/<utc>-<seq>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`)
   through a custom Serilog sink with a bounded back-pressured queue;
   runtime diagnostics go through Serilog (console + always-on per-startup
   file under `log/`), with per-category levels driven by appsettings.json's
   `Logging` section. See §9.
7. **Mirror the official VS Code Copilot Chat client** — for any wire-format
   detail (header set, beta header gating, etc.) follow `chatEndpoint.ts` and
   `dist/index.js` exactly. See `feedback_match_official_copilot_client.md`.
8. **Hub-and-spoke translation, single IR** — pipelines flow on one canonical
   shape (Anthropic Messages API), with adapters at the edges. Pair-wise
   translation between every (client, backend) combination would be a 3×2
   matrix with 4 stateful translators per direction = 8 total; hub-IR collapses
   it to **6 translators** that scale linearly when adding a new client or
   backend. See §3 below.

## 3. Hub-IR translation pattern

> **2026-06-15 — Codex shipped via the hub-IR (`add-codex-responses-client`).**
> An earlier 2026-06-12 note proposed treating Codex as a native-`/responses`
> *passthrough* that skips the IR. **That was superseded** (see
> `docs/codex-implementation-design.md` §1, v2): the hard requirement of
> cross-model substitution — Claude Code using GPT-5, Codex using Opus — means
> routing must happen on a backend-agnostic body, so **every client (Codex
> included) transits the single Anthropic-shape IR** and is routed by model.
> Codex is therefore a real pair of edge translators, not a passthrough:
> **T1** (`ResponsesToIrInboundAdapter`, Codex `ResponsesRequest` → IR) and
> **T4** (`IrToResponsesOutboundAdapter`, IR stream → Responses SSE) on the
> client edge, plus **T2/T3** inside `CopilotResponsesStrategy` (IR → Responses
> wire / Responses SSE → IR). They run on the **same shared
> `Pipeline<MessagesRequest>`**; the strategy registry picks by
> `target.Vendor` (`CopilotAnthropic` → `/v1/messages`, `CopilotResponses` →
> `/responses`). The research's three coercions (per-model `reasoning.effort`
> clamp, `service_tier` strip, `image_generation` drop) live **inside T2**,
> driven by the snapshot-grounded `CodexModelProfileCatalog`. The deliberate
> hub-IR cost — even Codex's own gpt path round-trips through the Anthropic IR
> (`Responses →T1→ IR →T2→ Responses`) — is guarded by the A-invariant
> round-trip tests and proven by a live `codex.exe` end-to-end run (plain +
> tool turns). The §3.3 inventory's `OpenAiToBridge*` rows are now realized as
> these Codex translators. Gemini (M4) remains genuinely IR-translated for the
> same reasons.

> **2026-08 — native Codex response fidelity.** The request still follows the
> architecture above (`Responses → T1 → IR → T2 → Responses`). The streaming
> response also still produces the T3 Anthropic-shaped semantic events because
> the shared response detectors must inspect text, tool input, block boundaries,
> usage and terminals. However, a clean Codex→Copilot-Responses response is no
> longer reconstructed from that necessarily lossy subset. T3 records each
> original Responses event plus its semantic expansion in a request-scoped
> `NativeResponsesEventLedger` and sends a constant, allocation-free carrier token immediately
> after the semantic events. Detectors never inspect the carrier itself. At T4,
> an exact match between the detector-approved semantic sequence and the recorded
> expansion authorizes the original event; any drop/rewrite/abort prevents blind
> restoration and uses the detector-owned replacement/failure path. This preserves
> reasoning summaries and encrypted content, message phases, response/item ids,
> full usage details, unknown event/field extensions and terminal metadata without
> growing the frozen Anthropic IR for every Responses feature. The ledger is
> allocated only for a native `/codex` streaming route, removes entries as T4
> consumes them. Source ordinals live in the FIFO ledger and are checked on add/take;
> the ledger is cleared on normal completion, fault and cancellation. The
> native `/cc` hot path pays no ledger allocation. `ClaudeCodeOutboundAdapter`
> drops any carrier defensively on `/cc→Responses`, so raw Responses data never
> crosses the Anthropic client edge.

> **2026-08-18 — native Codex reasoning-usage accounting.** HTTP response
> accounting is a separate client-edge contract from the SSE-event fidelity
> ledger above. Copilot `/responses` reports input usage that already charges
> replayed encrypted reasoning, but its raw HTTP response omits Codex's
> `X-Reasoning-Included` signal. Codex 0.147/0.148 interprets absence as
> “historical reasoning was not counted” and adds its own encrypted-reasoning
> estimate on top, so a Copilot-reported approximately 0.5–0.7M context can reach
> the client's approximately 0.9M auto-compact threshold. For a native
> `/codex/responses` request resolved to `CopilotResponses`, the endpoint therefore
> adds `X-Reasoning-Included: true` to a 2xx downstream HTTP response before any
> body/SSE bytes. This happens only AFTER the raw upstream header snapshot:
> `upstream-resp` continues to prove Copilot omitted the header, while
> `inbound-resp` and the actual ASP.NET response prove the compatibility signal
> reached Codex. The header is never sent upstream, synthesized on `/cc`, or added
> to a non-2xx response. Current Codex releases also clear their remembered flag
> before a later pre-turn compact check (`openai/codex#32483`); the bridge cannot
> reorder that client code, so real-client acceptance guards the bridge-owned
> continuous-turn leg and records the remaining client limitation explicitly.

> **2026-08-24 — native Codex context recovery.** Copilot rejects an oversized
> `/responses` request before streaming with HTTP 400,
> `error.code=invalid_request_body`, and a confirmed context-window sentence.
> Codex 0.147 and current upstream classify that HTTP shape as a generic bad
> request; only a native `response.failed` event whose code is
> `context_length_exceeded` enters Codex's context-recovery path. The native
> `/codex/responses` client edge therefore adapts only the exact bounded Copilot
> rejection for a streaming request into HTTP 200 `text/event-stream` with one
> such failed terminal. The raw `upstream-resp` retains status 400 and the exact
> body, while `inbound-resp` records the adapted status/event. Near-miss 400s,
> non-streaming calls, other routes/vendors, malformed or oversized error bodies,
> and the distinct Claude prompt-too-long translation remain unchanged. Because
> the production failure occurred during pre-turn compaction, the catalog safety
> policy also caps `auto_compact_token_limit` at 85% of total context (still
> subject to the lower prompt-derived guard and whole-thousand rounding).

> **2026-08 — native Codex request fidelity.** The request remains a real
> `Responses → T1 → IR → T2 → Responses` traversal; it does not bypass the IR.
> T1 pushes provider-native fields, source positions, and unmodeled siblings into
> `ProviderExtensions["openai"]` at request, message, and content-part level while
> also producing the semantic IR projection shared stages inspect. T1 never sees
> the selected destination. T2 pulls only the OpenAI namespace and merges those
> values into its current semantic Responses output; T2 never sees the originating
> client. Semantic fields from the post-stage IR win conflicts, then target-owned
> routing/profile/protocol coercions run last, so opaque carriage cannot resurrect
> a rejected field or undo a stage decision. A clean same-model route preserves
> every JSON value and `input[]` position; every departure is a narrow, live-proven
> and observable destination mutation. This is the request-side realization of
> the frozen source-push/destination-pull rule, not a raw-body side channel.

> In ordinary streaming, T4 consumes each FIFO entry as soon as its carrier
> arrives, so the ledger holds at most one source event. There are two deliberate
> buffering exceptions. With `BufferScannableBlocks=true`, the stage withholds a
> text/thinking block's semantic events **and their carriers** until
> `content_block_stop`, so the ledger retains that block's source events. With
> `PreserveStream=false`, the stage already buffers the entire semantic response
> before delivery, and the ledger retains the full response alongside it until the
> stage chooses replay or a real HTTP error. Both are opt-in and both clear their
> ledger entries before request completion.

### 3.1 The matrix problem

3 client shapes × 2 effective backend shapes = 6 (client, backend) pairs:

|              | Copilot Anthropic | Copilot OpenAI |
| ------------ | ----------------- | -------------- |
| Anthropic    | passthrough       | translate      |
| OpenAI       | translate         | passthrough    |
| Gemini       | translate         | translate      |

Pair-wise direct translators = 4 request directions × stateful streaming
machinery + 4 response directions = **8 stateful translators**, each non-
trivial. Adding a fourth client multiplies further.

### 3.2 Hub via Anthropic shape

The pipeline runs on a single **internal representation (IR) = Anthropic
Messages API shape**, materialized as the existing `MessagesRequest` /
`AnthropicMessage` / stream event records under `Models/Anthropic/`. All
adapters land at the IR boundary; all stages run in IR shape only.

Reasons:

- **Most expressive** of the three (cache_control with TTL, three thinking
  variants, multipolar content blocks, context_management edits). OpenAI and
  Gemini features mostly map in; reverse mapping loses fidelity.
- **Hot path is free** — Claude Code → Copilot Anthropic is M1's dominant
  traffic and stays passthrough on both directions.
- **DTOs already exist** from M1 step 3; IR is "free" — no separate IR types
  to design.

**Frozen IR contract (2026-06-15, `freeze-ir-provider-extensions`).** The IR is
now formally frozen — see **[`docs/ir-definition-design.md`](ir-definition-design.md)** §6
for the authoritative contract. In brief:

- IR body = `MessagesRequest` + `MessageParam` + the `ContentBlockParam`
  tagged-union (existing). Reasoning = `ThinkingBlockParam{Thinking,Signature}` /
  `RedactedThinkingBlockParam{Data}` + `OutputConfig.Effort`. Tool input/result =
  byte-faithful `JsonElement`. Streaming IR = the Anthropic SSE event model.
- **The IR body does NOT grow per-provider fields.** Anything a non-Anthropic
  client/backend sends that the body can't type (Codex's `store`, `service_tier`,
  `include`, `prompt_cache_key`, `text.verbosity`, …) rides a namespaced
  escape-hatch — `ProviderExtensions` (`Models/Common/ProviderExtensions.cs`), a
  `provider-name → opaque JsonElement` bag stolen from the Vercel AI SDK's
  `providerOptions` pattern — attached at request, message, and content-part level, opaque
  to the pipeline, copied verbatim. It is `null`/absent for Claude Code, so the
  hot path serializes byte-for-byte as before (proven by the H1 byte-equality
  test).
- The freeze is guarded by the **A-invariant suite**
  (`tests/CopilotBridge.UnitTests/Invariant/`): round-trip self-inverse,
  opaque-field byte-passthrough, bag survival/transport, and hot-path
  byte-equality — asserted on real, de-identified `claude.exe` captures used as
  input samples (never as oracles; `ir-definition-design.md` §7.0).

### 3.3 Translator inventory

Request side (client → IR → backend):

| Direction | Translator | Status |
| --- | --- | --- |
| Anthropic client → IR | identity | M1 |
| Codex/Responses client → IR | `ResponsesToIrInboundAdapter` (T1) | ✅ M3 |
| Gemini client → IR | `GeminiToBridgeRequestAdapter` | M4 |
| IR → Anthropic backend | identity | M1 |
| IR → Responses backend | T2 (in `CopilotResponsesStrategy`) | ✅ M3 |

Response side (backend → IR → client; all streaming variants are stateful):

| Direction | Translator | Status |
| --- | --- | --- |
| Anthropic backend → IR | identity | M1 |
| Responses backend → IR | T3 (in `CopilotResponsesStrategy`) | ✅ M3 |
| IR → Anthropic client | identity | M1 |
| IR → Codex/Responses client | `IrToResponsesOutboundAdapter` (T4) | ✅ M3 |
| IR → Gemini client | `BridgeToGeminiResponseAdapter` | M4 |

**Total non-identity translators: 6** (3 in each direction). Each request
transits at most 2. The Codex/Responses set (T1–T4) shipped in M3; Gemini (T?)
is M4. Adding a new client = 1 inbound + 1 outbound adapter; a new backend shape
= 1 of each. Linear growth.

### 3.4 Feature preservation across shapes

| Feature | Anthropic (IR) | OpenAI | Gemini |
| --- | --- | --- | --- |
| Reasoning | `thinking:{type:enabled\|adaptive,budget_tokens}` | `reasoning:{effort:low\|medium\|high}` (o-series, gpt-5) | `thinkingConfig:{thinkingBudget:N}` (2.5) |
| Prompt caching | per-block `cache_control:{type:ephemeral,ttl:5m\|1h}` | implicit prefix-match (no annotation) | explicit `cachedContent` resource (out-of-band) |
| Tool definition | `tools:[{name,input_schema,...}]` | `tools:[{type:function,function:{name,parameters}}]` | `tools:[{functionDeclarations:[...]}]` |
| Tool invocation | content `{type:tool_use,id,name,input}` | message-level `tool_calls:[{id,function:{name,arguments}}]` | parts `{functionCall:{name,args}}` |
| Tool result | content `{type:tool_result,tool_use_id,content}` (in user msg) | dedicated `tool` role message with `tool_call_id` | parts `{functionResponse:{name,response}}` |
| Vision | content `{type:image,source:{base64\|url}}` | content `{type:image_url,image_url:{url}}` | parts `{inlineData:{mimeType,data}}` |
| Stop reason | `end_turn \| max_tokens \| tool_use \| stop_sequence \| refusal \| pause_turn \| compaction` | `stop \| length \| tool_calls \| content_filter` | `STOP \| MAX_TOKENS \| SAFETY \| RECITATION` |

Translation rules:

- **Thinking budget ↔ effort**: bidirectional table (`low=4096`,
  `medium=16384`, `high=32768`); `adaptive` ↔ `effort=medium` when no specific
  budget is named.
- **Prompt caching IR → OpenAI**: drop `cache_control` (OpenAI handles
  prefix matching implicitly).
- **Prompt caching IR → Gemini**: M3 drops; M4 may use Gemini's `cachedContent`
  API out-of-band.
- **Prompt caching OpenAI/Gemini → IR**: heuristic — attach `cache_control` at
  end of system block + last block of first user message (Anthropic best
  practice).
- **Stop reason**: simple lookup; unmapped values fall back to `end_turn` with
  a `Log.Warning` diagnostic.
- **Tool result message structure**: Anthropic puts tool_result as a content
  block inside the next user message; OpenAI uses a dedicated `tool` role
  message. Translation maintains the `assistant tool_use → tool_result → user`
  ordering.

Every lossy mapping emits a `Log.Warning(...)` diagnostic so the operator can
surface what was dropped or approximated by tailing the runtime log.

## 4. Core abstractions

### 4.1 BridgeContext

```csharp
internal sealed class BridgeContext<TBody> where TBody : class
{
    public required BridgeRequest<TBody> Request { get; init; }
    public required BridgeResponse Response { get; init; }
    public required RouteTarget Target { get; set; }
    public required CancellationToken Ct { get; init; }

    // Response stages push here when they drop / rewrite individual SSE
    // events; the endpoint merges these into the inbound-resp audit so
    // operators see what was filtered.
    public List<DroppedSseEvent> DroppedEvents { get; init; } = [];
}

internal sealed class BridgeRequest<TBody> where TBody : class
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    /// <summary>Mutable typed body — stages transform this.</summary>
    public TBody Body { get; set; } = default!;
    /// <summary>Mutable header dict — stages add/remove/rename.</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class BridgeResponse
{
    public int Status { get; set; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Streaming responses: an async sequence of SSE events. Stages wrap this
    /// with transforming iterators (drop, mutate, capture). The final consumer
    /// (the writer that flushes to the inbound client) drives the chain.
    /// </summary>
    public IAsyncEnumerable<SseItem<string>>? EventStream { get; set; }

    /// <summary>Non-streaming responses: buffered body. Stages parse / mutate / re-serialize.</summary>
    public byte[]? BufferedBody { get; set; }

    public ResponseMode Mode { get; set; } = ResponseMode.Streaming;
}

internal enum ResponseMode { Streaming, Buffered }
```

**Lifetime — `BridgeContext` is a scoped DI service.** It is registered
`AddScoped<BridgeContext<MessagesRequest>>()`: the container creates one empty shell
per ASP.NET Core request scope, the pipeline-driving endpoint populates it
(`Request`/`Response`/`Ct`/`TraceId`/`InboundBetas`), and every pipeline component
(stages, strategies, adapters, the runner, and the detectors) **constructor-injects
that same instance** rather than receiving it as a method argument. Consequently
`Request`/`Response`/`Ct` are settable (not `required init`) so the endpoint can fill
the DI-created shell, and **a component's constructor MUST NOT read `ctx.Request`** —
it is unpopulated at construction; request data is read only in
`ApplyAsync`/`ForwardAsync`/`Begin`, which the runner invokes strictly after the fill.
See §4.8 for the per-request scope boundary as a whole.

### 4.2 RouteTarget

```csharp
internal sealed record RouteTarget(
    BackendVendor Vendor,
    string Endpoint,           // e.g. "/v1/messages", "/chat/completions"
    string ModelId);           // backend-side model id post-mapping

internal enum BackendVendor { CopilotAnthropic, CopilotOpenAi, CopilotResponses }
```

### 4.3 Stage interfaces

```csharp
internal interface IRequestStage<TBody> where TBody : class
{
    string Name { get; }
    Task ApplyAsync();
}

internal interface IResponseStage<TBody> where TBody : class
{
    string Name { get; }
    Task ApplyAsync();
}
```

Stages inject the scoped `BridgeContext<TBody>` (see §4.1) and read/mutate it in
`ApplyAsync` — `ctx` is no longer a method parameter. `TBody` now only groups
same-shape stages into a `Pipeline<TBody>`.

Most response stages are shape-agnostic in practice (they wrap event streams
or mutate response headers). The `TBody` parameter is there for symmetry and
to give shape-specific stages the option of seeing the typed request that was
just forwarded (useful for "what did we ask for" decisions). Stages that don't
care about `TBody` can be defined as `IResponseStage<object>` and registered
against any concrete pipeline via a thin generic adapter.

### 4.4 Upstream strategy

```csharp
internal interface IUpstreamStrategy<TBody> where TBody : class
{
    /// <summary>
    /// Inspects ctx.Request (now fully transformed by request stages) and
    /// decides whether this strategy can handle the resolved Target.
    /// </summary>
    bool Matches(RouteTarget target);

    /// <summary>
    /// Performs the upstream HTTP call. Internally may translate the request
    /// body to the backend's native shape (e.g. Anthropic → OpenAI) and wrap
    /// the response stream with a reverse translator. After this returns,
    /// ctx.Response.EventStream / BufferedBody / Headers / Status are populated
    /// in the SAME shape the inbound client expects. Reads the injected
    /// BridgeContext (see §4.1); no ctx parameter.
    /// </summary>
    Task ForwardAsync();
}
```

#### 4.4.1 Upstream inactivity timeout (`Pipeline:UpstreamTimeout`)

The forward paths (both `/cc` and Codex) bound how long they wait on an
*unresponsive* Copilot with two independent **inactivity** budgets — not a
total-duration cap. As long as upstream keeps making progress the relevant timer
resets, so a legitimately slow-but-progressing request (e.g. a near-full-`[1m]`
prompt whose response headers legitimately take minutes while Copilot builds a large
prompt cache) is never aborted.

- **Response-header budget** (historical key `FirstByteTimeoutSeconds`, default
  240) bounds the wait for upstream response headers, applied *per send attempt* inside
  `CopilotClient.PostMessagesAsync` (a linked `CancellationTokenSource` +
  `CancelAfter`, disarmed the instant headers arrive so it can't fire during the
  body read). Because it wraps each `SendAsync` individually, retry backoff never
  eats the budget. On expiry it throws `UpstreamTimeoutException(FirstByte)`,
  which is terminal (the transient-retry `when` clause does not catch it — a slow
  upstream just times out again). This is the *sole* bound on that phase: the
  shared `HttpClient` uses `Timeout.InfiniteTimeSpan`, replacing a coarse,
  unconfigurable cap that — under `ResponseHeadersRead`, which both forward paths
  use — bounded the same header phase anyway, on buffered and streaming responses
  alike. Note this budget is disarmed once headers arrive, so a **buffered** body
  that stalls afterwards has no bound today. See `docs/timeout-chain.md`.
- **Stream-idle budget** (`StreamIdleTimeoutSeconds`, default 240) bounds the gap
  between consecutive **upstream** SSE events, reset on every event pulled from
  upstream. Each read is driven by the shared `StreamIdleReader`, which races
  `MoveNextAsync` against an independent `Task.Delay` rather than arming/disarming a
  `CancelAfter` on the enumerator's own token — an arm/disarm on a reused CTS has a
  nanosecond poison race (a timer firing between a successful read and the disarm
  permanently cancels the source and spuriously aborts the next read), whereas an
  independent delay can never poison the source. A move that completes
  synchronously (the next event is already buffered) takes an allocation-free fast
  path; only a real wait on the network allocates the race scaffolding, and on an
  idle timeout the pending read is cancelled and awaited so it never dangles.
- **Downstream keepalive** (`KeepAliveIntervalSeconds`, default 15) makes
  `StreamIdleReader` carry a **second deadline over the same pending read**, so the
  reader returns a three-state outcome (`Event` / `EndOfStream` / `KeepAliveDue`)
  rather than a bool. Both deadlines are computed from a **single** clock reading and
  raced against the one in-flight read; on a `KeepAliveDue` win the read is left
  pending and untouched and the idle deadline is **not** recomputed, so no number of
  keepalives can postpone the idle timeout (ties go to the idle budget). A caller
  that wants no keepalive passes `TimeSpan.Zero` and gets the previous behaviour.
  Callers emit `StreamKeepAlive.Ping()` on each `KeepAliveDue`, but only after the
  first upstream event. The same calculation and pending read serve both client
  protocols; there is no endpoint-side periodic writer or Codex-specific clock.

  Eligibility is by **downstream client protocol, not upstream strategy**: the
  `/cc` passthrough strategy and the Responses strategy inject for `/cc` (including
  CC→gpt), and the Responses strategy also injects for native `/codex`. The payload
  is a complete SSE event (`event: ping`, `data: {"type":"ping"}`), not an SSE
  comment: Codex 0.144.1 and current source apply their 300 s default idle timeout to
  each parsed `eventsource().next()`, and the parser discards comment-only frames
  without yielding. A complete event resets that wait; Codex then ignores its unknown
  `type` without changing response state.

  Native Codex T4 passes the identity-marked ping through **before** native Responses
  fidelity accounting, so it consumes no upstream ordinal and cannot contaminate the
  semantic sequence authorizing restoration of the next genuine event. The injected
  ping is teed *above* `RawResponseCapture`, so `upstream-resp` structurally cannot
  contain it; both client endpoints mark it `injected: true`. Response inspection's
  per-block withholding relays it live rather than buffering it to
  `content_block_stop` — withholding exists to gate model *content*, and holding a
  keepalive for the block it was invented to cover would defeat it. See
  `docs/timeout-chain.md` and the `stream-keepalive` capability.

  Client idle settings remain independent operator-owned values. Startup reads the
  inspectable global Claude/Codex files, labels their visibility boundary, and
  reports an undercut or equal-deadline race without rewriting either client.
  Keepalive cannot protect the first-event gap because no ping is emitted before
  the first genuine upstream event; whole-response buffering likewise prevents a
  ping from reaching the downstream socket. On expiry the upstream strategy throws
  `UpstreamTimeoutException(StreamIdle)` through the hub stream; the downstream
  client edge selects the protocol-specific failure surface (see below). An
  observation cancelled at that deadline is right-censored and cannot establish
  whether upstream would later have resumed, so timeout tuning remains a separate
  operator decision — Copilot can remain silent for many minutes during extended
  thinking, so operators tune the bridge and native client settings separately.

Each budget disables at `<= 0` (no timer armed, no allocation — the byte-identical
`/cc` passthrough hot path is unchanged). Every positive response-header,
stream-idle, and keepalive value is used exactly: startup validates the actual
`CancelAfter`/`Task.Delay` representable range and introduces no margin, clamp, or
fallback. Surfacing, mapped by the endpoint's one
`catch (UpstreamTimeoutException)`:

- **Before headers** (first-byte): a real `504 Gateway Timeout`.
- **Mid-stream** (headers already sent, status locked at `200`): by default inject
  the *same* retryable `overloaded_error` SSE event the response guards use
  (`ResponseDetectionError.JsonWithMessage`) so Claude Code discards the partial
  attempt and issues its `stream:false` recovery request. Buffered T3 translates a
  successful cross-routed Responses object into Anthropic Messages IR before
  response detectors run; the `/cc` client edge then remains identity while Codex
  T4 converts the same IR back to Responses. If Claude Code chooses to issue a
  non-streaming recovery request, the bridge supports it; the connection command
  preserves `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` rather than selecting that
  client policy;
  `StreamIdleAction=Truncate` instead ends the stream with no error event. Note the
  detector framework is pull-based (it inspects events that *arrive*), so an idle
  gap — the absence of an event — cannot be a detector; the injection happens at
  the read site / endpoint catch, reusing only the shared error builder.

**Both forward paths are covered.** The first-byte budget lives in a shared client
helper (`CopilotClient.SendWithFirstByteBudgetAsync`) called by both
`PostMessagesAsync` (`/cc`) and `PostResponsesAsync` (Codex), so a first-byte
stall on either path throws `UpstreamTimeoutException(FirstByte)` → `504`. The
stream-idle budget is applied at each strategy's read site, but the **mid-stream
surface is selected by the downstream client protocol, not the upstream backend**.
This distinction matters on cross-model routes: Claude Code may enter through
`/cc` while routing selects Copilot `/responses`. The Responses-to-IR translator
therefore propagates a read fault instead of encoding it as a normal IR terminal.
At the Claude Code edge, `ClaudeCodeMessagesEndpoint` catches the fault and writes
the configured Anthropic retryable `event: error` (or truncates, by explicit
operator choice). At the Codex edge, `IrToResponsesOutboundAdapter` catches the
same fault, flushes exactly one `response.failed` terminal required by the Codex
parser, then rethrows so `CodexResponsesEndpoint` records
`upstream_timeout=stream_idle` and the audit error. A bridge-private failure marker
must never cross the `/cc` edge as `message_delta.stop_reason`; Claude Code accepts
unknown stop-reason strings as ordinary terminal metadata and would silently end
the agent turn instead of entering its streaming retry path.

A client cancellation always wins the race against a timeout: the throw sites only
convert to `UpstreamTimeoutException` when the linked timer fired **and** the
caller's own token did not, and the endpoint's client-cancel `catch` is ordered
first. The summary line carries `upstream_timeout=first_byte|stream_idle` (or
`(none)`).

### 4.5 Pipeline

```csharp
internal sealed class Pipeline<TBody> where TBody : class
{
    public required string Name { get; init; }
    public required IReadOnlyList<IRequestStage<TBody>> RequestStages { get; init; }
    public required IReadOnlyList<IResponseStage<TBody>> ResponseStages { get; init; }
    public required IStrategyRegistry<TBody> Strategies { get; init; }
}

internal interface IStrategyRegistry<TBody> where TBody : class
{
    /// <summary>Picks the first registered strategy whose <c>Matches</c> returns true.</summary>
    IUpstreamStrategy<TBody> Resolve(RouteTarget target);
}
```

### 4.6 PipelineRunner

The runner is what an endpoint handler calls. It's the entry point that drives
the entire request → forward → response flow.

```csharp
internal interface IPipelineRunner<TBody> where TBody : class
{
    Task RunAsync(Pipeline<TBody> pipeline);
}
```

The runner injects the scoped `BridgeContext<TBody>` (it no longer takes it as an
argument). Implementation contract:

```
RunAsync:
    foreach stage in pipeline.RequestStages:
        Log.Debug("req-stage start {Stage}"); apply; Log.Debug("req-stage end {Stage}")
    strategy = pipeline.Strategies.Resolve(ctx.Target)
    Log.Debug("strategy resolved {Name} target={Target}")
    strategy.ForwardAsync()
    Log.Debug("strategy returned status={Status} mode={Mode}")
    foreach stage in pipeline.ResponseStages:
        Log.Debug("resp-stage start {Stage}"); apply; Log.Debug("resp-stage end {Stage}")
    write ctx.Response to outbound (handled by endpoint code, not the runner)
```

### 4.7 Client adapters (the IR boundary)

Adapters live at the boundary between client wire shapes and the IR. They are
NOT part of the stage pipeline — they run **before** request stages and
**after** response stages, transforming `(client shape) ↔ (IR shape)`.

```csharp
/// <summary>
/// Translates a parsed inbound request body from a client's native shape
/// into the bridge IR. For Anthropic clients the IR shape == client shape;
/// the identity adapter just returns the body unchanged.
/// </summary>
internal interface IClientInboundAdapter<TClientBody, TIR>
    where TClientBody : class
    where TIR : class
{
    string Name { get; }

    /// <summary>
    /// Parses raw bytes (or a typed body decoded by the endpoint) into the IR.
    /// Headers are read-only context; mutated headers belong on
    /// <see cref="BridgeContext{TIR}.Request"/>.Headers after this returns.
    /// </summary>
    ValueTask<TIR> AdaptAsync(TClientBody clientBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>
/// Translates an IR-shape response stream back to the client's native shape.
/// Always async-enumerable to support stateful streaming translators (e.g.
/// IR→OpenAI must accumulate tool_use input_json_delta into a complete
/// argument string before emitting the OpenAI tool_calls chunk).
/// </summary>
internal interface IClientOutboundAdapter<TIR>
    where TIR : class
{
    string Name { get; }

    /// <summary>
    /// Wraps the IR-shape response event stream into a client-shape event
    /// stream. Stages have already finished mutating <c>ctx.Response</c>
    /// before this is invoked. Identity for Anthropic clients.
    /// </summary>
    IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        BridgeContext<TIR> ctx,
        CancellationToken ct);

    /// <summary>
    /// Buffered (non-streaming) variant. Most translators only need to
    /// implement one of the two — the endpoint picks based on
    /// <c>ctx.Response.Mode</c>.
    /// </summary>
    ValueTask<byte[]> AdaptBufferedAsync(byte[] irBody, BridgeContext<TIR> ctx, CancellationToken ct);
}
```

Adapter pairs are registered per (client) and per pipeline; the endpoint
handler picks the right pair based on its URL prefix:

```
/cc/v1/messages    → ClaudeCode adapters (identity for Anthropic shape == IR)
/codex/responses   → Codex adapters (translate Responses shape ↔ IR)
/gemini/v1/...     → Gemini adapters (translate Gemini shape ↔ IR)
```

Strategy-side translators (IR ↔ backend shape) are an internal concern of the
strategy and do not implement these interfaces — they live as private fields
inside, e.g., `CopilotChatCompletionsStrategy<TIR>`.

### 4.8 Per-request scope boundary (DI lifetimes)

Each forwarded request runs on its own ASP.NET Core request scope, and the whole
pipeline object tree is scoped to it — created at request start, disposed at request
end, fully isolated from concurrent requests. This is a structural guarantee, not a
convention: the boundary is drawn by DI lifetime.

**Scoped (one per request):** `BridgeContext<MessagesRequest>` (§4.1), the six
request stages, both upstream strategies, the four client adapters,
`IPipelineRunner<MessagesRequest>`, `ResponseInspectionStage`, and the five
`IResponseDetector`s (plus the `Pipeline<MessagesRequest>` object itself, composed
per scope by `BuildAnthropicPipeline`). Because these inject the scoped
`BridgeContext`, they *cannot* be singletons — a singleton capturing a scoped service
is a captive dependency.

**Singleton (process-level shared resources):** `IHttpClientFactory` and
`ICopilotClient` (a per-request `HttpClient` is the classic socket-exhaustion
anti-pattern; a *cached* one pins a pooled handler and defeats rotation, so
consumers lease per call — one named client per upstream surface, see
`UpstreamHttpClientNames`),
`AuthService`/`IAuthService` (owns refreshable GitHub OAuth credentials and the
in-memory atomic Copilot auth lease), the immutable catalog/registry lookup tables (`ModelProfileCatalog`,
`CodexModelProfileCatalog`, `IModelRegistry`), the Codex metadata services
(`CodexCatalogSourceCache`, `CodexCatalogDiskStore`,
`CodexCatalogOverlayService`, and `CodexCatalogProjector`),
`CopilotHeaderFactory`,
`BridgeIoSink`, `RequestSummaryLogger`, all options, and the hosted services.

**Guardrail.** The host container is built with
`ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }`
(`ServeCommand`). `ValidateOnBuild` walks the whole graph at startup and throws on
any captive dependency; `ValidateScopes` rejects resolving a scoped service from the
root provider. So a future regression that makes a pipeline component a singleton
fails fast at startup and in `DetectorCompositionTests`, rather than silently leaking
one request's state into another. The static routing helpers (`ModelRouteResolver`,
`ProfileAdjuster`, `MatchExpression`) are not DI services; the injecting stage passes
its context to them as an argument — the only place `ctx` still flows as a parameter.

### 4.9 Codex model metadata control plane

Codex remote-model discovery is deliberately outside
`Pipeline<MessagesRequest>`. It carries no prompt and performs no model
translation. `Codex.ModelCatalog.Enabled` controls only whether this metadata
route is mapped; it defaults to `true` in code and in the stock configuration,
so upgraded installations without the new key remain enabled. Setting it to
`false` leaves `POST /codex/responses` inference registered and makes Codex
fall back to its bundled catalog:

```
Codex (three-part query + corroborating complete User-Agent when prerelease)
  GET /codex/models?client_version=0.147.0
  User-Agent: codex_cli_rs/0.147.0-alpha.1.2 (...)
      ├─ CodexCatalogSourceCache
      │    ├─ fresh validated process memory
      │    ├─ fresh/stale validated per-user disk record
      │    └─ exact official openai/codex tag (anonymous HTTPS, TTL + ETag)
      ├─ CodexCatalogOverlayService → independent bounded live Copilot /models facts
      └─ CodexCatalogProjector      → exact-slug allow-list overlay + stable ETag
```

Ownership is strict:

- **Codex owns client behavior.** Complete entries, including
  `base_instructions`, tool/shell mode, reasoning descriptions, picker fields,
  compatibility hashes, and `auto_review_model_override`, originate from the
  exact requesting version's official
  `openai/codex/rust-v{client_version}/codex-rs/models-manager/models.json`.
  Stable and prerelease identities are never shortened or substituted.
- **The request proves the source identity.** Codex's model manager intentionally
  truncates prerelease `client_version` to `major.minor.patch`, while its
  `User-Agent` retains the complete `Codex Desktop/{version}`,
  `codex_cli_rs/{version}`, or headless `codex_exec/{version}` value. The bridge uses that complete identity only when
  its core matches the query (and requires exact agreement for complete queries).
  It never enumerates tags to guess a prerelease.
- **Copilot owns backend capacity.** Only exact bridge Responses profiles that
  are also advertised live with `/responses` remain API-supported. Valid live
  `max_context_window_tokens` raises `context_window` and
  `max_context_window`; `auto_compact_token_limit` is the lower of 85% total
  and 97.5% maximum prompt, rounded down to 1,000. Invalid or missing limits do
  not raise the validated exact-version baseline.
- **The bridge owns the safe join.** Live-only models are never synthesized,
  retired/baseline-only models are hidden, and review overrides are retained
  only when their target remains routable.

`CodexCatalogOverlayService` is a singleton, single-flight, five-minute successful-
overlay cache. A refresh failure serves its stale last known good overlay; a cold
failure returns the compatible baseline without uplift, so metadata degradation
never prevents otherwise healthy inference. That failure also establishes one
process-local retry deadline using the exact
`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds` value (default 300, valid
range 1..3600). Polls before the deadline perform no upstream `/models` I/O and
emit no duplicate warning; callers arriving at or after the deadline share one
retry. Success clears the failure state, and restart never restores it. This policy
is independent from inference and from the official-source cache: the latter
defaults to a 24-hour source-check TTL,
uses ETag revalidation, and persists a self-validating record in the OS per-user
cache. TTL controls when GitHub is checked, not when validated bytes expire. A
timeout, transport failure, 429/5xx, exact-tag 404, or invalid replacement serves
the exact-version stale memory/disk LKG. With no exact-version LKG, only the metadata request fails and
Codex retains its bundled catalog; `POST /codex/responses` remains independent.

The process-memory level and same-version stampede protection are provided by
Microsoft `HybridCache`; no `IDistributedCache` participates. Its factory owns the
straight-line persistent-disk → GitHub path. A stale L1 entry repeats the canonical
key lookup with local reads disabled, so the framework coordinates one refresh and
does not require a private memory or in-flight dictionary. A stale-if-error result
is surfaced to the owner and all coalesced waiters through the shared factory task,
but is deliberately not published as fresh L1 state; a later request can retry the
source. Different versions may download and validate concurrently. Because HybridCache serializes factory results
before L1 publication even with L2 disabled, the catalog registers a trim-safe
local-only serializer; its deserialize path fails closed if the no-distributed-cache
contract is ever violated. One process-wide asynchronous writer lock serializes
every temporary-file write, atomic record promotion, freshness update, and cleanup
deletion. The destination/protection state is rechecked after acquiring that lock.
Unknown, malformed, missing, or repeated `client_version` values receive a non-2xx
metadata error before source or cache I/O.

HybridCache 10.8.0 still contains an upstream-known reflection-JSON fallback
(`dotnet/extensions#5624`). The bridge makes that path unreachable by registering
an explicit-only serializer factory before `AddHybridCache`, and tests the resolved
DI graph plus the absence of `IDistributedCache`. A detailed Native AOT audit reports
three `IL2026`, three `IL3050`, and three `IL2070` diagnostics, all from that fallback.
NativeAOT 10.0 cannot consume ILLink's external member-level suppression attributes,
so the AOT input substitutes fail-closed throwing bodies only for the four diagnostic-
producing fallback members. Warning IDs remain enabled globally; a negative mutation
probe confirmed unrelated `IL2026`/`IL3050` diagnostics still fail a Werror publish.
The package version and exact assembly SHA-256 are pinned, so an upgrade or byte
change fails before substitution and requires a new audit.

The command-auth token is only the stable public sentinel emitted by
`auth provider-token`. Inbound provider authorization is never promoted to an
upstream credential: both metadata and inference obtain the real Copilot token
only through `IAuthService`.

### 4.10 Authentication leases and bounded 401/403 CAPI recovery

Authentication supports two version-owned lease shapes behind `AuthService`; an
independent `CredentialService` shields all persistence and OAuth details:

1. **CredentialService (encrypted at rest).** The only runtime file is exe-local
   `github_credentials.dat`; its required version selects semantics. If absent, the
   service migrates complete `github_credentials.v2.dat` state or falls back to
   `github_token.dat`, atomically writes and re-opens version 1, then deletes both old
   files; a later load retries any cleanup interrupted after verified commit. Version 1
   keeps rotating refresh state and remains usable until terminal rejection. Explicit
   login implements the official GitHub Copilot Plugin Device Flow in-process
   (`Iv1.b507a08c87ecfe98`, `read:user`, form-encoded RFC 8628, no client secret or
   `gh` process) and overwrites the same path with version 3, including the issuing
   OAuth client ID. Version 2 remains readable for existing direct credentials. The service
   owns migration, locks, first-use login single-flight, identity/generation, refresh,
   rejection, status, and logout;
   AuthService sees only an immutable credential lease.
2. **Copilot lease (memory only).** `GetCopilotTokenAsync` returns an immutable
   `CopilotAuthLease`: bearer, API base URL, deadline metadata, kind, and generation.
   Version 2 is the compatible direct bearer paired with
   `https://api.githubcopilot.com`; no `/copilot_internal/v2/token` request or
   short-lived refresh timer exists. Versions 1 and 3 use the exchanged lease: bearer,
   returned `endpoints.api`,
   receipt-relative refresh deadline, and server expiry. That local deadline follows
   the official-client clock-skew rule (`receipt + refresh_in + 60s`, refreshed five
   minutes early). Callers never read a token and endpoint from separate snapshots.

Every authenticated CAPI send (`/v1/messages`, `/responses`, `/models`, and
`/v1/messages/count_tokens`) is built from one lease. For an exchanged version-1 or version-3
lease, a first HTTP 401 or 403 disposes the rejected response/request, obtains the
already-newer or freshly minted lease, rebuilds the single-use request with the same
body bytes and business headers, and replays once. A direct version-2 403 follows the
same bounded ambiguous replay, but a direct 401 rejects the persisted credential
identity and requires `auth login`; resending the same `gho_` cannot recover. That
transition CAS-invalidates every cached lease generation for the terminal identity,
and the lock-free cache fast path checks terminal identity before reuse. The same check
also catches a late 401 for older credential A without returning already-terminal cached
credential B. A truly newer non-terminal credential identity remains usable.

The first 403 is deliberately ambiguous: it can be a stale short-lived bearer or
a real policy/entitlement refusal, so it is not terminally classified before the
fresh/newer-lease replay. A replayed 403 is terminal
`policy_or_entitlement_after_auth_replay`; a replayed 401 is terminal Copilot
authentication failure. One shared replay flag bounds mixed `401 -> 403` and
`403 -> 401` sequences at two authenticated sends total. 400/402/429 and all
other statuses never enter auth replay. Connection-layer retries retain one
shared budget across the auth replay, while each actual send retains its configured
first-byte inactivity budget.

GitHub `401 Bad credentials` during `/user` or legacy Copilot-token exchange uses at
most one version-1 refresh-token rotation and one replay. A direct CAPI 401 takes the
terminal path above. If supported refresh metadata is absent or rejected, the persisted
record is preserved and interactive login is required. Rate limits, 5xx responses, and
transport failures remain transient and
use bounded retry/backoff without marking the credential rejected. Lifecycle logs
carry layer, trigger, status/classification, generation,
timing and API host only—never access/refresh tokens, Authorization values, token
prefixes, hashes, or response bodies. A rejection log records recovery evaluation;
the bridge emits `replaying once` only after replacement lease acquisition succeeds,
so terminal direct 401 never claims a replay that cannot occur. Production security-log
evidence showed the
old per-login Copilot Plugin tokens being evicted by `oauth_access.destroy` with
`explanation=max_for_app`; the 86,100-second exchanged-bearer cache delayed
observation of that revocation and was not the GitHub token lifetime. See
`docs/token-storage.md` for file and threat-model details.

## 5. Request pipeline (current: `Pipeline<MessagesRequest>`)

The endpoint handler parses the JSON body before constructing
`BridgeContext<MessagesRequest>`; there is no separate `InboundCaptureStage`.
The assembled stage list is in `Hosting/BridgePipelines.cs`.

```
inbound bytes (POST /cc/v1/messages)
    │
    ▼  (parsed by endpoint handler into MessagesRequest)
    │
[1] ModelRouterStage              normalize model id; apply matching
                                  Routing.Locations entry (When→Use: model
                                  swap, effort remap, header set/remove);
                                  look up the target's ModelProfile; run
                                  ProfileAdjuster to shape the body to what
                                  the profile accepts; resolve ctx.Target.
                                   See §7.
    │
    ▼
[2] AssistantThinkingFilterStage  drop unsigned thinking blocks from
                                  historical assistant messages
    │
    ▼
[3] SystemSanitizeStage           strip <system-reminder>'s `# currentDate`
                                  and other volatile injections (cache stable)
    │
    ▼
[4] MessagesSanitizeStage         strip "Tool loaded." boundary text;
                                  fix trailing assistant message;
                                  merge tool_result + adjacent text in same msg
    │
    ▼
[5] ToolsSanitizeStage            drop `mcp__ide__executeCode` and
                                  similar IDE-only tools
    │
    ▼
[6] HeadersOutboundStage          generate Copilot's 7 official headers +
                                  conditional `anthropic-beta` per
                                  chatEndpoint.ts:193-210; drop inbound
                                  auth / x-api-key / x-claude-code-* / etc.
    │
    ▼
ctx.Target now resolved + ctx.Request.Body transformed
    │
    ▼
strategy = pipeline.Strategies.Resolve(ctx.Target)
strategy.ForwardAsync(ctx)        executes HTTP call to upstream;
                                   populates ctx.Response.EventStream
```

`ModelRouterStage` is an adapter around the singleton, I/O-free
`ModelRoutePlanner`. The planner holds only immutable catalogs/options and mutates
the `BridgeContext` supplied for that request; it is also used by
`POST /cc/v1/messages/count_tokens`. Thus normalization, first-match Location
selection, effort mapping, target-profile validation, and vendor selection have
one implementation. The count endpoint creates its own request context and never
borrows state from a messages request.

### 5.1 Claude Code sub-agent delegation on a Responses target

Claude Code identifies every request executed by a sub-agent with the inbound
`x-claude-code-agent-id` header. The first generation does **not** necessarily carry
`x-claude-code-parent-agent-id`, so the agent-id header alone is the authoritative
sub-agent signal. The `/cc` endpoint snapshots that signal on the scoped
`BridgeContext` before `HeadersOutboundStage` removes the private `x-claude-code-*`
headers.

When routing selects `CopilotResponses:/responses`, T2 treats Claude Code's `Agent`
tool as a client-specific delegation primitive, not an ordinary repeatable function.
With `Pipeline:CcToResponses:PreventRecursiveAgentDelegation` enabled (the default),
T2 omits the exact `Agent` tool from a sub-agent's translated Responses `tools[]` and
reconciles a forced choice for that removed tool to `auto`. Each actual removal emits
an operator-visible warning with the
`Pipeline:CcToResponses:PreventRecursiveAgentDelegation=false` recovery setting.
Root Claude Code requests retain `Agent`, and the IR is not mutated. Consequently
native Anthropic `/cc` passthrough and native `/codex` traffic are unaffected;
disabling the option restores the previous CC-to-Responses translation behavior.

The same T2 builder and recursive-Agent decision are used when a count request is
routed to Responses. Count-time Anthropic `thinking` configuration is a control
with no Responses input equivalent; when no `output_config.effort` is present it
is an explicit T2 drop, as are historical plaintext `thinking` blocks. Their
visible sibling text remains in the input. Encrypted reasoning blocks retain the
existing T2 mapping.

This is deliberately a translation-boundary compatibility policy. Claude Code may
legitimately expose delegation recursively, but a non-Claude backend can interpret the
tool as an unrestricted fan-out primitive: many individually valid, bounded responses
can form a request storm that a per-response runaway detector cannot see.

`CacheControlCleanStage` from research §3.6 rule 1 is intentionally absent —
the DTO does not model `cache_control.scope`, so the field is silently dropped
at deserialize time. Add the stage if a `Scope` property is ever introduced.

Body coercions that used to live as the standalone `ThinkingRewriteStage` or
spread across multiple routing-rule entries (haiku adaptive → enabled,
opus-4.7 enabled → adaptive, effort variant routing, beta strips) now live as
data in `ModelProfileCatalog`, applied uniformly by `ProfileAdjuster`. User
`Routing.Locations` express operator preference only (model swap, per-target
effort remap, whitelisted header tweaks) — the target's profile decides every
wire-shape fact. See §7.

### 5.2 Route-aware count-tokens and Responses admission accounting

`POST /cc/v1/messages/count_tokens` first executes the shared route plan using
the request's own model, headers/betas, optional effort, and content:

- A `CopilotAnthropic` target remains byte passthrough in both directions,
  including non-success status and content type. Unknown native fields are not
  parsed or discarded.
- A `CopilotResponses` target is strictly parsed into the supported count shape,
  then built by `ResponsesRequestBuilder` (T2). Those exact T2 bytes are sent once
  to Copilot's existing `/v1/messages/count_tokens`; the bridge never calls
  `/responses` to count and cannot execute a prompt. Unknown top-level or nested
  count fields fail explicitly instead of disappearing during translation.
- The successful upstream `input_tokens` value is parsed as a non-negative
  32-bit integer and converted by `ResponsesAdmissionEstimator`. The 2026-08-05
  GPT-5.6 calibration is `ceil(raw * 1.05) + 16`; an uncalibrated Responses model
  uses `ceil(raw * 1.075) + 32` and emits a bounded warning. Arithmetic is
  monotonic and saturates at `Int32.MaxValue`. The exact record is scoped to
  `gpt-5.6-sol`; unprobed sibling ids use the fallback rather than borrowing a
  family-name guess. See
  `docs/copilot-api-research.md` §15.4 for the exact-body evidence corpus.

The estimate covers the exact count input only. It does not synthesize a main
turn's system prompt, stream flag, output budget, or effort. A transformed-body
count failure is returned as failure; there is no fallback to the smaller source
Anthropic framing.

For a `/cc` messages request resolved to Responses, the buffered error path also
recognizes only Copilot's confirmed HTTP 400 pair
`code=invalid_request_body` plus the exact context-window sentence. It rewrites
that body to Anthropic `invalid_request_error` containing `prompt is too long`,
which Claude Code can compact and retry. Status, code, message, `/cc` edge, and
Responses target are all required; native `/codex`, native Anthropic `/cc`, and
near-miss 400s remain unchanged. The raw Copilot body is captured at
`upstream-resp` before the rewritten Claude envelope is captured at
`inbound-resp`.

## 6. Response pipeline

As-built, the response side is a single stage — `ResponseInspectionStage` — that
runs an ordered set of **scoped** `IResponseDetector`s in one stream wrap
(one SSE parse, fanned out), replacing the earlier one-stage-per-concern layout:

```
ctx.Response.EventStream  (raw events from upstream, or post-translation)
    │
    ▼
ResponseInspectionStage   injected with IEnumerable<IResponseDetector> (each a
    │                     scoped service — one instance per request scope, so
    │                     streaming state never crosses requests). Per request it
    │                     keeps the config-enabled detectors, calls Begin(ctx) on
    │                     each, then renders every event through them in order:
    │
    ├─ DoneFilterDetector          DropEvent on `event:message data:[DONE]`
    ├─ ModelRewriteDetector        RewriteEvent on the first `message_start`
    │                              (and the buffered body's top-level `model`)
    ├─ ResponseLeakDetector        Abort on leaked protocol markup (see §6.1)
    ├─ RunawayGuardDetector        Abort on degenerate response volume or token repetition
    │                              (streaming AND buffered `application/json`; see §6.4)
    └─ ToolInputValidationDetector Observe (default) / Abort invalid tool input
    │
    ▼
endpoint writes ctx.Response.EventStream (streaming) or BufferedBody (buffered)
```

For a native `/codex` request routed to `/responses`, the stream entering this
stage is interleaved as `semantic IR event(s) → private ordinal carrier` for each
upstream source event. Only the semantic IR events are fanned out to detectors;
the carrier stays in relative order through normal streaming, whole-response
buffering and per-block buffering. T4 releases the corresponding original
Responses event only if the semantic sequence still matches exactly. Consequently
the fidelity channel cannot undo `DropEvent`, `RewriteEvent` or `Abort`. The sole
accepted clean rewrite is `ModelRewriteDetector`: T4 proves that the only semantic
change was `message_start.message.model` and patches just `response.model` into
original Responses envelopes, preserving every unrelated field.

A non-cancelled native Codex stream always ends in exactly one Responses terminal.
Clean `response.completed` / `response.incomplete` events are restored whole.
An upstream `response.failed` remains a typed fault so generated error text cannot
leak; Codex receives the bounded bridge-owned failed envelope. Premature EOF,
upstream exception, stream-idle timeout or detector abort discards any open partial
block and emits `response.failed` without fabricating tool-argument `.done`,
`output_item.done` or a successful terminal. If the downstream request token is
already cancelled, writing stops and endpoint accounting records client
cancellation instead.

On the native Codex request side, `function_call_output.output` is provider-native
opaque JSON. T1 pushes its string/object/array value into the IR block unchanged;
T2 pulls and writes that same JSON kind and value. A paired replay of real captured
`input_text[]` output returned 200 from `gpt-5.6-sol`, so flattening it would be an
unnecessary silent rewrite. Function-output `id`/`status` and future siblings ride
the same part-level provider record.

Responses message items and developer/system placement use the same rule. T1 keeps
message text semantically visible to stages but records provider metadata and the
original source position in the IR. T2 restores valid `msg*` ids, phase/status and
unmodeled siblings, and emits developer/system items at their original positions.
The one live-proven protocol correction is a non-Responses message id such as
`item_0`: Copilot rejects it because message ids must begin with `msg`, so T2 omits
only that id and records the mutation while preserving every unrelated item value.
Top-level `reasoning.context` and future reasoning siblings ride the request bag;
dropping `all_turns` is forbidden because Codex documents omission as the
`current_turn` default.

The Claude Code→Responses request side has one content-driven exception. An
exact target profile whose direct two-turn probe proves structured multimodal
function output may receive an image-bearing Anthropic `tool_result` array as
ordered Responses `input_text`/`input_image` output content items. The
writer preserves the image data URL or source URL and sets
`Copilot-Vision-Request: true`. It takes this branch only when every source
block is a valid Anthropic text/image block; unknown or malformed siblings fall
back as a complete array, never a partially translated one. T2 never asks who
produced the request: a source that means its tool output to stay opaque says so
ON THE IR (Codex T1 marks `opaque_tool_output` on the block), and T2 pulls that
fact. Positive support is exact-profile only (currently live-proven for
`gpt-5.6-sol`); fuzzy-nearest and unprobed sibling models keep the compatibility
string path.

Responses reasoning items follow the same push/pull split. T3 pushes the whole
item into the IR unconditionally — a hidden `redacted_thinking` block carrying
the opaque blob plus a `bridge_reasoning_item` marker holding the original item
— exactly as it already does for `web_search_call`. Each client edge then pulls
what its protocol needs: the native Codex edge restores the original events from
the event ledger and emits no output item for the block, while the Claude edge
folds the marker into the block's own `data` (the only field the Anthropic wire
can carry) and scrubs it. On the next turn the Claude inbound edge unfolds that
value back into the part-level provider bag, which T2 already reads — so the
request builder needs no knowledge of the envelope. The fold is versioned,
size-bounded, and fails closed on a corrupt or newer-version payload rather than
forwarding arbitrary JSON upstream. Live probes pin the backend requirement this
preserves: `gpt-5.6-sol` rejects a replayed reasoning item that carries
`encrypted_content` without `summary`.

The carrier is DURABLE CLIENT DATA, not a transient encoding: Claude Code persists
transcripts and replays them across `--resume`, compaction, and a bridge downgrade.
It is therefore a protocol between bridge versions separated by time, with no
negotiation step and a peer that may be OLDER than the writer. The design rule,
measured against the shipped v0.4.29-beta decoder rather than assumed: an older
reader fails CLOSED (bounded 400) on its own discriminator with a payload it cannot
handle — including an unknown `v` or an unknown top-level field — but returns
`Absent` for an unfamiliar discriminator and forwards the value upstream as
provider-native content. So evolution rides the PAYLOAD's `v`, and the emitted
prefix stays `cbridge_rr_7f3a9d2c_v1:`. The version-free `cbridge_rr_7f3a9d2c:`
form is recognised on READ only, reserved so a future build could adopt it without
stranding today's readers; emitting it is what would be unsafe, not reading it.
Encrypted reasoning state is also model-private, so T3 stamps the producing model
(`bridge_reasoning_origin`) and the Claude edge folds it into the carrier. Judging
that origin is a separate step, and deliberately NOT part of the edge: the inbound
adapter runs before the model router, where the body still names the model the
CLIENT asked for. On CC→gpt those differ by design — the client says
`claude-opus-5` while routing resolves `gpt-5.6-sol` — so comparing at the edge
would drop every valid carrier and still miss a rewrite performed later. The edge
decodes and states the origin as a fact on the IR; `ClaudeReasoningOriginStage`,
which runs after routing and legitimately knows the destination, pulls that fact
and drops a carrier minted by a different model. The turn then runs stateless,
which is right for the same reason established for an unreplayable item: a turn
missing hidden state recovers, a turn carrying foreign state does not.
Both halves of that codec live in the Claude client adapters, and the unfold
takes no gate — not on the resolved target, not on the inbound route. It is not
"restoring state for a Responses backend"; it is one edge decoding what the same
edge encoded, so asking which backend will serve the request would mean baking
routing into a client edge. Isolation from the native Codex edge is structural:
that edge has its own adapter, never mints a carrier, and never references the
codec (pinned by `NativeCodexEdge_CannotSeeTheCarrierCodec`). Provider-native
blobs are separated by the private discriminator alone, which is also what keeps
a native Anthropic passthrough's opaque data untouched. Genuinely
destination-specific stages use the existing `CopilotAnthropicOnlyStage` wrapper
instead — that is where vendor knowledge belongs.

Modeled Responses reasoning input items also require an opaque extension lane.
T1 maps `encrypted_content` to the IR redacted-thinking data slot and stores every
present `id`, `summary`, and `content` value in that block's provider bag; T2 writes
them back unchanged. This is not optional metadata under detailed reasoning:
Codex echoes a reasoning item after custom-tool execution, and Copilot rejects the
next turn when the present (sometimes empty) `summary` array was dropped.

Each detector returns a `DetectionAction` — `None` (pass through), `DropEvent`
(swallow + record in `ctx.DroppedEvents`), `RewriteEvent` (replace payload), or
`Abort` (inject an error and end). The first non-`None` action wins for an event;
`Abort` short-circuits. Detectors never touch the stream — the stage owns the one
async-iterator combinator, so consumption (the endpoint's writer) still drives the
chain lazily. For non-streaming responses `EventStream` is null and detectors run
via `InspectBuffered` over `BufferedBody`.

Detectors are **scoped DI services** (one instance per request scope) because a
streaming detector may carry cross-delta state (the response-leak automaton) that must
not be shared across requests. They are injected as the whole set
`IEnumerable<IResponseDetector>`, and the stage runs them in ascending
`IResponseDetector.Order` — an explicit value assigned at registration (see
§6.3), so **precedence does not depend on the container's enumeration order**. Each
detector self-gates on its own config via `Enabled` (backed by an
`IOptionsSnapshot<T>`), and reads its per-request data (declared tools, model ids)
from the injected `BridgeContext` in a parameterless `Begin()` — the stage calls
`Begin` once, after config-gating and before any inspection, so DI-construction
timing (scope start, body empty) is decoupled from request-data availability
(response phase, body populated). A disabled detector is filtered out before
`Begin`: it is never initialized and never scans.

### 6.1 Response-leak guard

`ResponseLeakDetector` detects a Copilot-served Claude model leaking a tool call as
literal `<invoke name="X">…<parameter…>…</parameter>…</invoke>` XML inside a
`text`/`thinking` block (instead of a real `tool_use` block) and forces Claude
Code to retry the turn cleanly. Detection is **structural** and requires all of:
a closed, balanced `<invoke>…</invoke>` with ≥1 closed `<parameter>`; the tool
name in the request's `tools[]`; and not inside a markdown code fence. It does
**not** key off the drifting prefix token (`court`/`call`), `stop_reason` (both
`tool_use` and `end_turn` observed), or a bare unbalanced `<invoke`.

The same detector also detects a second family of leaks: Claude Code **control /
event envelopes** emitted as literal text — `<task-notification>` (closed, with a
closed `<task-id>` and at least one closed `<summary>`/`<status>`/`<output-file>`
child), `<teammate-message teammate_id="…">`, `<channel source="…">`,
`<cross-session-message from="…">`, `<tick>…</tick>` (non-empty), and
`<system-reminder>…</system-reminder>` (the bare-tag wrapper Claude Code puts
around every injected attachment/system block; non-empty inner, no attributes).
Each is **shape-gated** the same way: closed, required child/attribute present and
non-empty, and not inside a code fence. `<channel>` is distinguished from the
sibling `<channel-message>` wrapper by its exact close tag, so the latter never
trips the guard. Both families share one config, one retry path, and one
detection-point `Warning`.

The scan is a single-pass streaming automaton with O(1) state that retains no
content: one `ResponseLeakAutomaton` owns the shared concerns exactly once — code-fence
tracking, the tripped latch, and the matched subject — and dispatches each character
to a list of bounded, KMP-based matchers: one for the tool-call signature
(`<invoke name="X">…</invoke>`, matched via KMP failure edges with name capture and
`<parameter>` balance counting) and one per control envelope (each proving its
required child/attribute). Every matcher keeps its own independent state; the first
to report a closed, shape-valid signature on a character names the leaked subject,
and a trip is gated on being outside a code fence. A signature split across deltas,
even character-by-character, is carried by automaton state, so an arbitrarily long
leaked block is handled without a window the opening tag could scroll out of.
Runaway name/attribute/inner captures fail open with a bounded buffer.

Two orthogonal knobs (`Pipeline:Detectors:ResponseLeakGuard`): `PreserveStream` (default true —
keep streaming, inject a mid-stream SSE `error` event; false — buffer the whole
response and emit a real HTTP status) × `Signal` (default `OverloadedError` →
`overloaded_error`/529, which Claude Code reliably retries and, after 3
consecutive, falls back opus→Sonnet; or `ApiError` → `api_error`/500). `ScanThinking`
extends both families into `thinking` blocks (which have no fence concept).
Because retry discards the whole attempt (including already-streamed dirty text),
the dirty bytes never commit to the transcript — this breaks the self-reinforcing
poisoning loop. See `docs/copilot-upstream-toolcall-bug-report.md` for the leak's
empirical basis (~2.2% of responses in a poisoned session, all closed/unfenced).

**Airtight streaming suppression (`BufferScannableBlocks`, default false).** With
`PreserveStream=true` the guard relays each `text_delta` live and can only inject the
error *after* the closing tag is scanned — so a detected leak's bytes have already
reached the client (recent Claude Code may then keep the partial response instead of
retrying). `BufferScannableBlocks=true` closes this: the stage withholds each
**scannable** block (`text` and `thinking`, unconditionally) from `content_block_start`
until `content_block_stop`, feeding detectors live but buffering output, and relays the
block only if it stayed clean — so a leak is suppressed *before any byte is written*.
Non-scannable blocks (`tool_use`/`input_json_delta`) still stream live, so only
scannable blocks pay latency, and only until each block ends — a much smaller TTFT cost
than whole-response buffering (`PreserveStream=false`). It has no effect when
`PreserveStream=false` (already buffered) or the guard is off. `thinking` is withheld
whenever it is scannable regardless of `ScanThinking` (which only gates whether the
withheld block is *scanned*), keeping the stage free of per-detector scan-config coupling.

Each signature can be **disabled independently** under
`Pipeline:Detectors:ResponseLeakGuard:Signatures` (`Invoke`, `TaskNotification`,
`TeammateMessage`, `Channel`, `CrossSessionMessage`, `Tick`, `SystemReminder`; all
default true) — a false-positive escape hatch. If the model is legitimately echoing
this markup (say the user is discussing how `<invoke>` tool-use or a
`<task-notification>` envelope works and a sample reply gets caught), turning off
just that one signature omits only its matcher and leaves the rest of the guard
active. The tripped signature and
the exact key to flip are named in both the retry error the client receives and the
detection-point `Warning`, so a false positive is self-service to fix. Config is
read at **startup, so a restart is required** after changing a switch. (Hot-reload
is not wired today: each detector already reads an `IOptionsSnapshot<T>` afresh per
request scope, so the only change needed to make a flipped switch take effect live
is registering the JSON file with `reloadOnChange: true` in
`BridgeConfigurationExtensions` — no change to any detector or the stage.)

### 6.2 Tool-input validation

`ToolInputValidationDetector` covers the adjacent failure mode where the model emits
a **real** `tool_use` block, but the accumulated `input_json_delta.partial_json`
fragments close into malformed JSON or an object that violates the request's declared
tool schema. This is distinct from `ResponseLeakDetector`: there is no leaked
`<invoke>` text here; the transport shape is correct, but the tool arguments are
invalid.

The detector accumulates fragments for the current tool block and validates only at
`content_block_stop`, when the final JSON object is available. The schema check is
a conservative JSON-Schema subset (implemented by the standalone
`JsonSchemaSubsetValidator`, unit-tested directly): top-level input must be an
object; declared `required` properties must exist; declared `object` / `array` /
`string` / `boolean` / `number` / `integer` / `null` types must match; and `items`
/ nested `properties` (including nested `required`) are checked recursively. Every
keyword it does not model (`enum`, `pattern`, `additionalProperties`, …) fails
**open** — a pass means "not obviously invalid", not "fully schema-valid". It
deliberately does **not** repair bad arguments or inject dummy values.

**Semantics — observe by default; Claude Code self-heals.** Aborting an invalid tool
call was found to *cut off* a recovery Claude Code already performs. CC parses the
accumulated tool input with `safeParseJSON` (malformed JSON → `null` → falls back to
`{}`; see `messages.ts`), then runs the tool's `zod strictObject.safeParse`, and on
failure feeds the model an `is_error` `tool_result` (`toolExecution.ts`) so it retries
the tool call with corrected input. A real trace showed a valid-looking
`AskUserQuestion` emitted without the required `question` field: CC would have
re-prompted the model, but a mid-stream abort instead surfaced *"API Error: Server
error mid-response"* to the user. So the detector **records the diagnosis
(`tool_input_invalid=true` on the summary) but relays the response unchanged** unless
a class is explicitly opted into an abort.

Config lives at `Pipeline:Detectors:ToolInputValidation`: `Enabled` (default true,
keeps the diagnosis flowing); `MalformedJsonAction` and `SchemaViolationAction` — the
two failure classes, each `Observe` (default) / `AbortOverloaded` / `AbortApiError`
(the abort variants fold the wire shape into the action, so there is no separate
signal knob to diverge); and `PreserveStream` (only relevant when a class aborts:
default true injects the SSE `error` mid-stream, false buffers for a real HTTP
status). When an action *is* `Abort*`, the abort replaces the block's
`content_block_stop` — the point where CC commits a content block into its
conversation — so the bad block never enters context; the buffered path
(`InspectBuffered`) re-validates the same way.

### 6.3 Adding a detector

Derive from `AbstractOrderAwareDetector<TSelf>` (CRTP — it carries the `Order`
property and the default members, so you implement only `Name`, `Enabled`, and
`InspectEvent`), and register it with one line in `BridgeServiceCollectionExtensions`
— `services.RegisterResponseDetector<YourDetector>(order)` — in the right
position. `RegisterResponseDetector` registers the detector as a scoped
`IResponseDetector` AND a singleton `DetectorOrder<YourDetector>` holding that
explicit precedence value. Lower order runs first; duplicate detector types or
duplicate order values throw during registration, so precedence stays visible at
the call site and never falls back to `IEnumerable<T>` resolution order. The
detector's constructor injects `DetectorOrder<YourDetector>` (forwarded to
`base(order)`), the scoped `BridgeContext<MessagesRequest>`, and its
`IOptionsSnapshot<T>` config (exposed through `Enabled`); read request data and
reset streaming state in the parameterless `Begin()`. The standard async-iterator
combinator still applies (the stage owns it; a detector only returns a
`DetectionAction`).

For reference, the stage's stream-wrap uses the standard async-iterator combinator:

```csharp
private static async IAsyncEnumerable<SseItem<string>> TransformEvents(
    IAsyncEnumerable<SseItem<string>> source,
    [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var evt in source.WithCancellation(ct))
    {
        // ... transform or drop ...
        yield return evt;
    }
}
```

This is the standard async-iterator combinator pattern; consumption (the
endpoint's writer) drives the entire chain lazily.

### 6.4 Runaway guard

`RunawayGuardDetector` is the degeneracy circuit-breaker: it aborts a runaway
generation — a model stuck emitting an unbounded stream of tiny fragments, or
repeating the same token — before it hangs the client, with the same retryable
`overloaded_error`/529 the leak guard uses (setting `runaway=true` on the summary
line). It trips on any of **four** per-response signals under
`Pipeline:Detectors:RunawayGuard`:

- **`MaxDeltaBytes`** — cumulative `content_block_delta` payload bytes (streaming only;
  a buffered body has no deltas).
- **`MaxDeltaCount`** — per-block ceiling on delta events (streaming only).
- **`RepetitionWindow` / `RepetitionMinUniqueRatio`** — repetition *density*: a
  trailing sliding window of whitespace tokens trips when it is **full** and its
  unique-token ratio falls below the floor. This needs the window to fill, so it
  targets long floods.
- **`RepetitionMaxConsecutiveRepeat`** (default 50) — repetition *run length*: trips
  when the same token repeats this many times **consecutively**, independent of the
  window filling or total length. This catches a **short** flood the density window
  can never see (observed: `claude-opus-4.8` repeating one token ~100× in a 108-token
  body). A legitimate repetitive output that trips it is resolved by **raising** this
  value, not disabling the guard; `<= 0` disables the signal.

Both delivery paths are covered. On a streaming response the signals run per
`content_block_delta` via `InspectEvent`; on a **buffered** (`application/json`)
response — which Copilot returns when it ignores `stream:true` (seen with `tool_use`
turns) — `InspectBuffered` parses the Anthropic body and feeds each `text`/`thinking`
block's content through the *same* per-block core, so a runaway is caught whether or
not Copilot streamed. (Before this, `RunawayGuard` implemented only `InspectEvent`, so
a buffered runaway was relayed verbatim — the `count`/`court` captures.) The density
and run-length signals share one whitespace tokenizer with a bounded carried-tail so a
token split across deltas is counted once; the density ring materializes a key only for
a token new to the window, and the run-length counter compares against a reused buffer,
so the degenerate single-token path stays near-zero-allocation. All state resets per
`content_block_start` (streaming) or per block (buffered). The detection-point `Warning`
names the tripped reason, the `Signal`, and the delivery mode (`stream`/`buffer`), never
the runaway content. See `docs/gpt55-runaway-diagnosis.md` for the original volume-runaway
basis.

## 7. Routing semantics

```
inbound (model, effort, thinking, betas, ...)
   ↓ Normalize          canonical id (dotted, no date suffix)
   ↓ User location      appsettings.json Routing.Locations — first-match-wins;
                        applies Use (model swap, effort remap, header set/remove)
   ↓ Profile lookup     ModelProfileCatalog — wire truth for the target model
   ↓ ProfileAdjuster    mechanical body coercion (effort, thinking, betas,
                        mid-conv-system fold, budget cap)
   ↓ Resolve            vendor + endpoint dispatch
outbound to upstream
```

This sequence is implemented by `ModelRoutePlanner` and shared by messages and
count-tokens; endpoint-specific code must not duplicate the Location scan.

The split is **"profile = fact, location = preference, adjuster = mechanism"**:

- **`ModelProfile`** describes what Copilot's variant of one model actually
  accepts on the wire — the values come from playground probes against
  `/v1/messages` (see
  `tests/CopilotBridge.Playground/ModelProfileProbe.cs`), never from
  Copilot's `/models` metadata (which is incomplete and sometimes wrong:
  haiku-4.5 advertises adaptive thinking but rejects it at runtime). A
  profile is Copilot's behavior, not a preference — users cannot override
  it.
- **`Routing.Locations`** in `appsettings.json` are nginx-style entries:
  each is a self-contained `When` (match expression) → `Use` (change-set)
  closure, scanned first-match-wins. A `Use` may swap `body.model`, remap
  `output_config.effort` (per-target, e.g. `max → xhigh` only where the
  backend accepts xhigh), and set/remove a whitelisted header. This lets an
  operator paper over a stale catalog (Copilot ships opus-4.9, our catalog
  only knows opus-4.8 → redirect 4.9 to 4.8 until we release), pick a
  deliberate fallback (1M-context beta → the dedicated 1M model id), or
  normalize client vocabulary the backend rejects (`effort=max`). Locations
  express **operator preference**; they never override a profile fact (the
  rewritten body still flows through `ProfileAdjuster`).
- **`ProfileAdjuster`** is a pure function `(body, profile) → body` that
  enforces the profile's accepts/rejects: strip unsupported effort, coerce
  unsupported thinking shape, fold mid-conversation `role:"system"`
  messages into the top-level `system` field, cap `budget_tokens`, register
  beta-strip patterns for `HeadersOutboundStage`.

A profile miss is **best-effort, not a hard fail**: `ModelRouterStage` first
tries an exact catalog lookup, and on a miss falls back to the *nearest known
profile* by fuzzy string similarity (`ModelNameMatcher`, Jaccard over character
bigrams). A Copilot model newer than this build's catalog (e.g. a freshly
shipped `claude-sonnet-6`) is therefore **forwarded under the closest known
model's wire contract** — the **real** model id stays on the wire (Copilot has
the model; only our probed profile is missing), and only the coercion rules
(thinking policy, accepted efforts, mid-conv-system, beta strips) are borrowed.
Every fuzzy match is **WARN-logged**: it's a guess, so the operator should
upgrade the bridge (or add an explicit `Routing.Locations` remap) and watch for
unexpected behavior if the borrowed contract doesn't fit. Copilot remains the
final authority — a wrong borrowed shape surfaces as Copilot's own model error.

Only when the requested id is **too dissimilar to any known model** (below the
`ModelNameMatcher.DefaultMinSimilarity` floor — typically a typo or a foreign
vendor prefix) does the bridge fall back to the old behavior: throw
`UnknownModelException` → 400 + Anthropic-format error body naming the nearest
rejected candidate and its score, plus how to fix it (add a routing rule, fix
the typo, file an issue). The borrowed profile never rewrites the model id: if
a borrowed profile used `EffortHandling.RouteToVariant`, the stage neutralizes
it to `Strip` so a sized-sibling id that may not exist for the new model is
never substituted.

> **Why this inverts the earlier "never guess" principle.** The old design
> fail-closed to avoid a *silent* Copilot 400. But the real model id is always
> what's sent, so a genuinely-new model just works; a mis-borrowed contract
> produces Copilot's own (visible) error, not a silent one; and the similarity
> floor still gives a crisp, actionable error for true typos. Net: new models
> work out of the box, typos still fail loudly.

### 7.1 Vendor + endpoint dispatch — `IModelRegistry.Resolve`

Pure name normalization + prefix-based dispatch in `CopilotModelRegistry`:

| Prefix | Vendor | Default endpoint |
| --- | --- | --- |
| `claude-*` | CopilotAnthropic | `/v1/messages` |
| `gpt-*`, `o3-*`, `o4-*`, `gemini-*` | CopilotOpenAi | `/chat/completions` |

`Normalize` canonicalizes the inbound id:

- Strips a trailing 8-digit date suffix (`claude-opus-4-8-20260115` → `claude-opus-4-8`)
- Merges the first consecutive `digit-digit` pair into `digit.digit`
  (`claude-opus-4-7` → `claude-opus-4.7`). Any trailing non-numeric segments are
  preserved verbatim (`claude-opus-4-7-1m-internal` → `claude-opus-4.7-1m-internal`)
  — that variant id is itself retired, but the suffix-preserving rule is what
  lets a future sized/variant id round-trip without a code change.

`Resolve` returns `null` for unknown prefixes; the runtime stage throws a
descriptive error in that case.

### 7.2 Model profile catalog — `ModelProfileCatalog`

The wire-truth table. One `ModelProfile` per Copilot Anthropic model id,
hand-curated in `Pipeline/Routing/ModelProfileCatalog.cs`. After the 2026-07
reconciliation the catalog covers 7 models (every Claude id Copilot still
exposes on this account: haiku-4.5, sonnet-4.6, **sonnet-5**, opus-4.6,
opus-4.7, opus-4.8, **opus-5**). The reconciliations **retired** opus-4.5,
opus-4.6-1m, the opus-4.7 -high/-xhigh/-1m-internal variants, and (2026-07)
**sonnet-4.5** — all now 400 (`model_not_supported` / "not available for
integrator"; `ModelProfileProbe.RetiredCandidate_LivenessProbe`).
The values are sourced from the corresponding probe rows in
`ModelProfileProbe.cs` — re-run that test after Copilot ships or changes any
model, then reconcile.

> **Neither `/models` nor the integrator allowlist is authoritative — probe.**
> They disagree in *both* directions. The retired `-1m-internal`/`-high`/`-xhigh`
> ids routed 200 for months while absent from `/models`; conversely, as of
> 2026-07 the allowlist still advertises `claude-sonnet-4.5`, `claude-opus-4.5`,
> `claude-fable-5`, and `claude-opus-4.8-fast`, **every one of which 400s** on a
> live request (`IntegratorAllowlist_Dump`,
> `UnadvertisedCandidate_LivenessProbe`). Only a live probe adds or removes a
> profile. (The allowlist only surfaces for an id that exists upstream but is not
> granted — a wholly unknown id returns a bare `model_not_supported` with no
> list, so dump it with a real-but-restricted id.)

```csharp
internal sealed record ModelProfile
{
    public required string CanonicalId { get; init; }

    // Effort values accepted as-is. Empty = backend rejects the field outright.
    public IReadOnlyList<string> AcceptedEfforts { get; init; } = [];

    // What to do with an inbound effort not in AcceptedEfforts:
    //   Strip            — drop the field; model falls back to its default.
    //   RouteToVariant   — re-target body.model to a sized sibling id
    //                      (e.g. opus-4.7 + high → opus-4.7-high).
    public EffortHandling EffortOnUnsupported { get; init; } = EffortHandling.Strip;
    public IReadOnlyDictionary<string, string> EffortToVariant { get; init; } = …;

    // Which thinking shapes the backend takes, and how to coerce the others.
    public ThinkingPolicy Thinking { get; init; } = ThinkingPolicy.AdaptiveOnly;
    public int MaxThinkingBudget { get; init; } = 64000;

    // opus-4.8, opus-5 and sonnet-5 accept the protocol extension in legal
    // placements (pred=user, succ=assistant-or-end); every other model rejects
    // it — see the cross-cutting facts below.
    public bool AcceptsMidConversationSystem { get; init; }
    public bool AcceptsSpeedFast { get; init; }

    // anthropic-beta tokens to strip from the outbound header set when this
    // profile is the active target (e.g. `context-1m-*` on the dedicated 1M
    // model ids, which subsume the beta).
    public IReadOnlyList<string> StripBetas { get; init; } = [];

    // CROSS-FIELD: efforts the backend rejects ONLY when thinking is disabled.
    // Empty for every model but opus-5 — see the cross-cutting facts below.
    public IReadOnlyList<string> EffortsRejectedWhenThinkingDisabled { get; init; } = [];
}

internal sealed record ThinkingPolicy
{
    public required IReadOnlyList<string> AcceptedShapes { get; init; }
    public required string CoerceToWhenUnsupported { get; init; }
    public bool DeriveBudgetFromEffortOnEnabled { get; init; }
    public bool DeriveEffortFromBudgetOnCoerce { get; init; }

    // Four named presets cover today's catalog. Add more as needed.
    public static ThinkingPolicy AdaptiveOnly       { get; } // opus-4.7, opus-4.8, sonnet-5
    public static ThinkingPolicy AdaptiveOrDisabled { get; } // opus-5
    public static ThinkingPolicy EnabledOnly        { get; } // haiku-4.5
    public static ThinkingPolicy All                { get; } // sonnet-4.6, opus-4.6
}
```

Cross-cutting facts the probe surfaced and the catalog encodes (re-probed in
the 2026 reconciliations):

- **Effort acceptance is per-model and non-monotonic.** opus-4.7 / opus-4.8 /
  opus-5 / sonnet-5 accept `low/medium/high/xhigh/max`; opus-4.6 / sonnet-4.6
  accept `low/medium/high/max` but reject `xhigh` (max works where xhigh 400s);
  haiku-4.5 rejects the effort field entirely. So `max` is stripped
  only on the models that reject it, not universally.
- **opus-5 carries the catalog's only CROSS-FIELD constraint.** With
  `thinking:disabled` it rejects effort `xhigh`/`max` — *"output_config.effort
  'max' is not supported when thinking is disabled on this model. Use effort
  'high' or below, or enable thinking."* — while accepting each field
  independently (`Opus5_DisabledThinking_EffortInteraction_Probe`). A
  single-axis probe matrix cannot see this, so **when adding a model, probe the
  field COMBINATIONS the client actually emits**, not just each axis. It is
  modeled as `EffortsRejectedWhenThinkingDisabled` rather than by narrowing
  `AcceptedEfforts`, because narrowing would silently downgrade every
  *thinking-on* max/xhigh request — the common case, which probes show is fine.
  §7.3's clamp applies only on the disabled-thinking path. This was proven live:
  with the constraint removed, a real `claude.exe` run at max effort sent
  `disabled`+`max` upstream and Copilot 400'd it.
- **opus-5 is `AdaptiveOrDisabled`, not `AdaptiveOnly`.** It rejects only
  `thinking:enabled`; `disabled` returns 200 (`Opus5_Thinking_ProbeAcceptance`).
  Reusing opus-4.8's `AdaptiveOnly` would coerce a user's explicit
  `thinking:disabled` up to adaptive — silently re-enabling and billing
  reasoning they turned off — and would make the cross-field clamp unreachable.
  (opus-4.7/4.8/sonnet-5 probe the same way but retain `AdaptiveOnly`; changing
  them alters shipped behavior and needs its own real-client verification.)
- **opus-4.8, opus-5 and sonnet-5 accept non-first `role:"system"` messages** in
  legal placements (predecessor=user; successor=assistant or end-of-array); every
  other model rejects the extension with `"Unexpected role 'system'"`. So
  `AcceptsMidConversationSystem` is `true` for those three and `false` elsewhere,
  and the §7.3 handler keeps legal placements / converts illegal ones when
  `true` and converts unconditionally when `false`. (sonnet-5 mid-conv support
  contradicts Anthropic's "opus-4.8 only" docs — it was confirmed by live probe.)
- **`thinking.budget_tokens` must always be less than `max_tokens`** or
  Copilot 400s on that constraint before evaluating the shape at all.

### 7.3 Body coercion — `ProfileAdjuster.Apply`

Pure function over `(BridgeContext, ModelProfile, ModelProfileCatalog)`,
applied in order:

1. **Effort + variant routing.** If `body.output_config.effort` isn't in
   the profile's `AcceptedEfforts`, either drop the field (`Strip`) or
   rewrite `body.model` to a sized sibling id from `EffortToVariant`
   (`RouteToVariant`). A variant rewrite switches profiles — the adjuster
   continues against the new profile's contract for the remaining steps.
2. **Thinking shape coercion.** If `body.thinking.type` isn't in
   `AcceptedShapes`, coerce to `CoerceToWhenUnsupported`. When coercing
   enabled→adaptive and the policy says so, carry the inbound
   `budget_tokens` forward into `output_config.effort` first (so the
   user's reasoning depth survives). When the coerced shape lands on
   enabled, derive `budget_tokens` from effort using the standard
   mapping (low=4096, medium=16384, high=32768, xhigh=64000).
3. **Mid-conversation system fold.** When the profile rejects
   `role:"system"` messages outside the first slot, collect their text
   blocks, drop the messages from `body.messages`, and append the text to
   `body.system`. Preserves order. Without this, 4.8 → 4.7 fallback (or
   any 4.8 request at all on Copilot, per §7.2) would 400 on
   `Unexpected role 'system'`.
4. **Budget cap.** Clamp `thinking.budget_tokens` to
   `MaxThinkingBudget`.
4b. **Cross-field effort clamp (disabled thinking).** If the FINAL thinking
   shape is `disabled` and the surviving effort is in the profile's
   `EffortsRejectedWhenThinkingDisabled`, clamp *down* to the highest tier
   that is both in `AcceptedEfforts` and not vetoed (opus-5: `max`/`xhigh` →
   `high`); if no such tier exists, drop the field. Runs **after** step 2
   (the coerced shape is what makes the constraint bind) and after both
   effort passes (it clamps whatever survived them). Clamping rather than
   stripping preserves as much of the user's requested depth as the backend
   allows — Copilot's own error names `"effort 'high' or below"` as the
   remedy, whereas stripping would fall back to the model default. No-op for
   every profile with an empty list, i.e. everything except opus-5.
5. **Beta strip registration.** Append the profile's `StripBetas`
   patterns to `ctx.PendingBetaStrips` so `HeadersOutboundStage` removes
   them from the outbound `anthropic-beta` header. A global
   `advisor-tool-*` strip is also registered here unconditionally —
   Claude Code 4.8 sends `advisor-tool-2026-03-01` by default and
   Copilot's gateway 400s on it (`unsupported beta header(s)`), so it's
   dropped for every model regardless of profile.

The adjuster never decides what's a user preference vs a fact — by the
time it runs, the user rule has already picked the target profile, and
every adjustment from there is fully determined by the profile's
documented behavior.

### 7.4 User routing locations — `appsettings.json` `Routing.Locations`

Loaded via standard `IConfiguration` / `IOptions<T>` at startup. Validated by
`RoutesValidator` before Kestrel binds the port. **No hot reload** — edit and
restart.

Modeled after nginx `location { ... }`: a request matches at most one entry
(first-match-wins, no chain, no fall-through), and that entry's `Use` is the
complete change-set applied to the request. Each location is a self-contained
closure — everything that should happen for "this kind of request" lives in
one block.

Schema (model aliases are for preference/routing only; Codex context discovery
does not require an alias):

```jsonc
{
  "Routing": {
    "Locations": [
      {
        "When": { "Model": "claude-opus-5" },
        "Use": { "Model": "gpt-5.6-sol", "EffortMap": { "max": "xhigh" } },
        "Note": "Optional Claude Code to Codex-model substitution"
      }
    ]
  }
}
```

**`When` — the match expression** (`Pipeline/Routing/MatchExpression.cs`). A
small JSON-native tree, no custom DSL:

| Node | Form | Semantics |
| --- | --- | --- |
| `Model` | `"claude-opus-4.8"` | Exact (case-insensitive) match on the canonical model id. |
| `Effort` | `"max"` | Exact match on `output_config.effort`. |
| `Header` | `{ "Name": "...", "Eq" \| "Contains": "..." }` | Header match. For `anthropic-beta` the inbound representation is the parsed token set, so `Contains` is token-containment (with trailing `*` wildcard) and `Eq` is "this exact token is present". |
| `AllOf` | `[ … ]` | AND — every child must match. |
| `AnyOf` | `[ … ]` | OR — at least one child must match. |

Top-level `Model` / `Effort` / `Header` are **implicitly AND-ed** (the 80%
case needs no `AllOf` wrapper). There is **no `Not`** — negation is a
readability footgun; write two locations instead. An empty `When` matches
everything (a catch-all), but `RoutesValidator` rejects empty `AllOf`/`AnyOf`
arrays as authoring slips.

**`Use` — the change-set.** At least one field is required (an empty `Use` is
rejected):

| Field | Effect |
| --- | --- |
| `Model` | Replace `body.model` (canonical id). |
| `EffortMap` | `{ inbound: outbound }` — per-target effort remap, applied **after** the model swap, so a map like `{"max":"xhigh"}` is scoped to the resolved target (only meaningful on ids that accept xhigh). |
| `Headers.Set` | `{ name: value }` — set/replace a whitelisted header. For `anthropic-beta` the value is split into tokens and merged into the outbound set (`PendingBetaAdds`). |
| `Headers.Remove` | `[ "X-Foo", "anthropic-beta:context-1m-*" ]` — plain entries drop the whole header; the `name:pattern` form drops matching `anthropic-beta` tokens (trailing `*` wildcard). |

**Header whitelist.** `Headers.Set` / `Headers.Remove` only accept
`anthropic-beta`, `Editor-Version`, `Editor-Plugin-Version`,
`Copilot-Integration-Id`, `X-GitHub-Api-Version`. Any other name fails
startup validation — bridge-internal protocol headers (`Authorization`,
session/machine/device ids, the vision flag) are off-limits so a config
typo can't produce silent 401s. Identity-header overrides thread through
`BridgeContext.CopilotHeaderOverrides` into `CopilotHeaderFactory`;
`anthropic-beta` add/remove flows through `HeadersOutboundStage`.

Today's `appsettings.json` ships an **empty** active location list
(`"Locations": []`) — no rewrites by default — plus a **disabled** example under
`_Locations_disabled` (a key the config binder ignores; enable by renaming it to
`Locations`):

| `When` model | `Use.Model` | `Use.EffortMap` |
| --- | --- | --- |
| `claude-opus-5` | `gpt-5.6-sol` | `max` → `xhigh` |

Note:
- The example routes Claude Code's `claude-opus-5` to Copilot's newest Codex
  model `gpt-5.6-sol`. The `EffortMap max→xhigh` is an **optional down-tier**:
  unlike gpt-5.5, gpt-5.6-sol (the "xlarge" effort profile) accepts `max`
  natively, so without the map Claude Code's `max` passes through verbatim — the
  map caps it at `xhigh` instead (drop the `EffortMap` to send `max` through). It
  ships disabled because it's a cross-model substitution, not because the target
  is a poor fit.
- Earlier releases used a `gpt-5.5-1m → gpt-5.5` alias plus a manual
  `model_context_window` override to evade Codex's bundled 272k-era cap. That
  workaround is retired. `config codex` now enables command-auth discovery and
  Codex obtains exact live limits from `GET /codex/models`; routing aliases are
  no longer part of context-window negotiation.

**Retired: the opus 1M redirects.** Earlier releases shipped
`"opus 4.x + 1M beta → dedicated 1M model id"` redirects (opus-4.7/4.8 →
`claude-opus-4.7-1m-internal`, opus-4.6 → `claude-opus-4.6-1m`). The 2026
reconciliation removed both: Copilot **retired** the `-1m-internal` / `-1m`
target ids (they 400 "not available for integrator"), and the opus-4.6 / 4.7
**base** ids now serve 1M context natively (a >600k-token prompt returns 200 —
`ModelProfileProbe.OpusBase_LargePrompt_ProbeOneMillionContextSupport`), same as
opus-4.8 and sonnet-5. So the 1M beta now passes through to the base model
unchanged; no redirect (and no `StripBetas`) is needed.
- No `claude-haiku-4.5 + 1M`, `claude-sonnet-4.6 + 1M`, etc. locations are
  needed: Copilot has no 1M variant for those families, and the bridge
  refuses to invent one. The inbound beta passes through to Copilot,
  which 400s with its own message — the user's signal to either drop
  the beta or pick a 1M-capable model.

### 7.5 Unknown-model error path

When `ModelRouterStage` looks up the post-normalize, post-location model id in
`ModelProfileCatalog` and finds nothing, it throws
`UnknownModelException` carrying the inbound model id, the resolved id, the
matching location (if any) plus its index, and the full known-profile list.
`ClaudeCodeMessagesEndpoint` catches the exception and writes:

```
HTTP/1.1 400 Bad Request
Content-Type: application/json

{
  "type": "error",
  "error": {
    "type": "invalid_request_error",
    "message": "[copilot-bridge] No profile for model 'claude-opus-4.9' …"
  }
}
```

The message is Anthropic's standard error envelope (so clients that already
handle `invalid_request_error` display it correctly) with a
`[copilot-bridge]` prefix and one of two diagnostic templates:

- **Client sent unknown model**: lists known profiles + a JSON snippet the
  operator can paste into `appsettings.json` to redirect.
- **A location's `Use.Model` target is unknown**: cites the offending
  `Routing.Locations[i]` (by index + its `Note`) and asks the operator to
  fix it or add an earlier location that remaps the resolved id.

`ModelRouterStage` also `Log.Error`s the same diagnostic so it lands in
the bridge's runtime log without needing the client transcript.

### 7.6 Refreshing model profiles and the Codex client catalog

These are two different maintenance loops. Do not use catalog metadata as a
substitute for live request-shape probes.

**Backend request profiles (`CodexModelProfileCatalog` and
`ModelProfileCatalog`):**

1. Run `tests/CopilotBridge.Playground/CopilotGapProbes.DumpClaudeModelsAndCapabilities`
   to see what Copilot exposes on the current account.
2. Run `tests/CopilotBridge.Playground/ModelProfileProbe.Thinking_ProbeAcceptance`,
   `Effort_ProbeAcceptance`, and `MidConversationSystem_ProbeAcceptance`
   for the full per-model matrix. The error messages tell you which
   values are accepted (e.g. `"supported values: [low medium high]"`).
3. Reconcile `Pipeline/Routing/ModelProfileCatalog.cs` against the
   results. Each entry's field should map back to a row in the probe
   output — don't extrapolate from family names; sibling models surprise
   you (haiku-4.5 ≠ sonnet-4.6 on thinking; opus-5 accepts `thinking:disabled`
   while opus-4.7/4.8 are modeled adaptive-only, and opus-5 alone rejects
   `xhigh`/`max` *when* thinking is disabled — a constraint no single-axis
   probe can see).

A future Copilot model can land as a hard error first (the bridge refuses
the request with a clear 400) and then as a profile after probes confirm
the wire shape. This is by design — silent breakage is worse than an
explicit "I don't know this model yet."

**Codex exact-source catalog contract:**

1. Runtime support does not require a fixture or bridge release for a new
   version. The requesting complete version selects only its exact official tag.
2. To refresh a contract-test capture, run
   `scripts/update-codex-catalog.ps1 -ClientVersion X.Y.Z[-prerelease]`. It writes
   only beneath `tests/Fixtures/Codex/rust-vX.Y.Z...`; fixtures are never compiled
   into the bridge.
3. If the script or live resolver reports schema drift, update the validator,
   projector allow-list, and pinned real-Codex consumer test before accepting the
   new shape. Unknown additive fields remain opaque and are preserved.
4. Re-run the live Copilot model snapshot and >272k real-Codex-shaped probes.
   Update `docs/copilot-codex-model-capabilities-snapshot.json` and protocol
   research only from captured evidence.
5. Run unit and `Kind=ApiContract` suites, then the command-auth
   `Kind=ClientBehavior` long-context task. The final verdict must include the
   real Codex dispatch log, not merely an HTTP 200.

At runtime the endpoint resolves exact official source bytes through memory,
persistent disk, then anonymous GitHub; overlays exact-slug live Copilot facts;
and degrades independently in each layer as described in §4.9.

## 8. Per-client URL prefixes

Each client is mounted on its own URL prefix. This eliminates body sniffing
entirely — the URL **is** the client identity.

| Prefix | Client | Inbound shape | Endpoints |
| --- | --- | --- | --- |
| `/cc/v1/...` | Claude Code | Anthropic Messages | `messages`, `messages/count_tokens`, `models` |
| `/codex/...` (M3+) | Codex | OpenAI Responses + Codex metadata | `POST responses`, `GET models?client_version=...` |
| `/gemini/v1/...` (future) | Gemini CLI | Google Gemini | `models/.../generateContent`, etc. |

`Endpoints/` becomes shape-organized:

```
Endpoints/
  ClaudeCode/
    ClaudeCodeMessagesEndpoint.cs            # POST /cc/v1/messages
    ClaudeCodeCountTokensEndpoint.cs         # POST /cc/v1/messages/count_tokens
    ClaudeCodeModelsEndpoint.cs              # GET  /cc/v1/models
    ClaudeCodeEndpoints.Map(app);            # registers all of the above
  Codex/                                     # M3+
    CodexResponsesEndpoint.cs                # POST /codex/responses
    CodexModelsEndpoint.cs                   # GET  /codex/models?client_version=...
```

Configuration impact: Claude Code's `ANTHROPIC_BASE_URL` environment variable
must be set to `http://localhost:8765/cc` (note the `/cc` suffix), so its
client appends `/v1/messages` and lands on our `/cc/v1/messages`.

When a Location rewrites a Claude model to a Responses model, the same rewrite
also applies to `/cc/v1/messages/count_tokens`. The count route still calls the
Anthropic-named Copilot count endpoint, but its body is the canonical Responses
T2 shape and its returned count is calibrated as described in §5.2.

## 9. Logging

Two log channels, both flowing through a single Serilog pipeline configured
once in `Program.cs`.

> **See also:** [`docs/observability-design.md`](observability-design.md) — the
> full observability subsystem design: the four-artifact audit trace, trace-id
> correlation, raw upstream capture, and the `RequestAudit` gating seam, with
> mermaid diagrams. This section is the concise reference; that doc is the map.

### 9.1 Per-request IO audit (opt-in, four files per request)

**Off by default.** The audit captures full request and response bodies —
including user prompts — so it's gated behind a config toggle:

```jsonc
"Tracing": {
  "Enabled": false,              // flip to true to capture; restart to apply
  "Directory": "request-traces"  // relative to the .exe unless absolute
}
```

When `Enabled` is false (the default), `Program.cs` never constructs the
`BridgeIoSink` and the audit sub-logger is skipped entirely — no files, no
channel, no background writer. The startup banner says `Req trace: disabled`.
The separate Serilog **text** log (`<exe-dir>/log/bridge-<stamp>.log`,
startup banner + stage debug + errors) is always on; only the per-request
JSON capture is opt-in.

For cross-routed count requests, the four bodies deliberately differ:
`inbound-req` is the original Anthropic count body, `upstream-req` is the exact T2
Responses body, `upstream-resp` is Copilot's raw count, and `inbound-resp` is the
calibrated Anthropic count response. The ordinary summary logs only requested and
resolved model, raw count, calibration id/reserve, and returned count—not prompt
or tool content.

When enabled, each inbound request produces **four** JSON files in
`<exe-dir>/request-traces/` (or the configured `Directory`), sharing a stamp
so they're trivially groupable:

```
20260521-143205-0042-inbound-req.json     ← what Claude Code sent us
20260521-143205-0042-upstream-req.json    ← what we forwarded to Copilot
20260521-143205-0042-upstream-resp.json   ← what Copilot returned
20260521-143205-0042-inbound-resp.json    ← what we sent back to Claude Code
                                            (including dropped SSE events)
```

`<seq>` is a process-wide monotonic counter (`BridgeIoSeq.Next`); the inbound
and upstream halves of one request share it. The four artifacts are
intentionally separate files (not one combined record) so an operator can
load a single side into an editor without bringing along megabytes of the
streaming response. (Note: `log/` = always-on text log, `request-traces/` =
opt-in per-request JSON — two distinct directories.)

#### Pipeline

```
endpoint
   │  logger.LogInboundRequest (seq, method, path, headers, body)
   │  logger.LogUpstreamRequest (seq, method, url, headers, body)
   │  logger.LogUpstreamResponse(seq, status, headers, body)
   │  logger.LogInboundResponse (seq, status, headers, body, events?, error?, durationMs)
   ▼
ILogger<T> (Microsoft.Extensions.Logging)
   ▼   one entry per call, EventId in {1001..1004}, state =
   ▼   IEnumerable<KVP<string,object?>> { ("Payload", BridgeIoPayload) }
   ▼
Serilog.Extensions.Logging bridge
   ▼   LogEvent.Properties["Payload"] = ScalarValue(BridgeIoPayload)
   ▼
Serilog logger split (Program.cs):
  .WriteTo.Logger(lc => lc
    .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("Payload"))
    .WriteTo.Sink(BridgeIoSink))
  .WriteTo.Logger(lc => lc
    .Filter.ByExcluding(e => e.Properties.ContainsKey("Payload"))
    .WriteTo.Console(...)
    .WriteTo.File(...))
   ▼
BridgeIoSink (custom ILogEventSink, Hosting/Logging/BridgeIoSink.cs)
   │  Emit(LogEvent) extracts Payload, drops it into a bounded Channel.
   │  Channel: capacity=256, FullMode=Wait → producer (request thread)
   │           blocks when full = real back-pressure on the request side.
   ▼
Worker task (single reader)
   ▼  pretty-prints to <utc>-<seq>-<kind>.json (using JsonNode tree;
   ▼  body is parsed as JSON when possible, falls back to string)
   ▼  the payload body is a plain array, reclaimed by GC (the sink does not pool)
```

#### Buffer ownership

Endpoints read inbound bodies into a pooled buffer (a
`Microsoft.IO.RecyclableMemoryStream`, pooled chunks) to avoid per-request LOH
allocation churn on conversation-sized payloads, via the shared
`InboundBody.ReadPooledAsync` helper which returns a disposable `PooledBody`.
Kestrel enforces the bridge-wide ceiling bound from
`Server.MaxRequestBodySizeBytes` before this reader can consume the body. The
default is 104,857,600 bytes (100 MiB); `KestrelOptionsConfigurator` rejects
non-positive values at startup and assigns the configured value to
`KestrelServerOptions.Limits.MaxRequestBodySize`. Raising the ceiling permits
larger image-bearing histories but does not compact them and increases the
maximum memory attributable to one accepted request.
The endpoint consumes the body **synchronously** (deserialize + the audit
capture) inside a `using`, so the pooled storage is returned to the manager within
the endpoint's synchronous section — it does **not** cross `await` into the
pipeline (the request context has no raw-bytes field for a stage to read). When
tracing is on, `RequestAudit.RecordInbound` makes a one-shot copy for the sink;
that copy is a plain payload-owned array, distinct from the pooled read buffer.

The sink-owned body buffer is a plain array reclaimed by GC after the audit JSON
is written; the sink does not pool or return it.

#### Redaction

`Authorization`, `X-Api-Key`, `Anthropic-Auth-Token` header values are
replaced with `<redacted>` before serialization. Bodies are not scrubbed
(the wire bodies don't contain OAuth tokens; those flow only through the
`/login/device/*` endpoints which never enter the bridge audit path).

#### Shutdown

`AppDomain.ProcessExit` calls `Log.CloseAndFlush()`, which disposes the
custom sink. Dispose:

1. `_channel.Writer.TryComplete()` — no more payloads will be accepted.
2. `_worker.Wait(5s)` — drain the queue naturally.
3. On timeout, cancel the worker token and wait another 1s — panic flush.

So pending IO audits land before the process exits, even on Ctrl+C.

### 9.2 Runtime log — Serilog console + per-startup file

Non-IO events (stage debug lines, startup banners, framework noise from
Kestrel / Routing / Hosting.Diagnostics) flow through Serilog's standard
sinks:

- **Console** (`Serilog.Sinks.Console` 6.1.1) — mirrors to **stderr** from
  level Verbose+. Stderr (not stdout) so it doesn't interleave with the
  device-code OAuth banner.
- **File** (`Serilog.Sinks.File` 7.0.0) — writes to
  `<exe-dir>/log/bridge-{YYYYMMDD-HHMMSS}.log`. One file per process start
  (no rolling). Each restart gets its own file; old files accumulate
  until the operator cleans up.

Output template:
`{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}`.

#### Level filtering — driven by appsettings.json

Serilog's own minimum is `Verbose` (everything in). Per-category filtering
is delegated to `Microsoft.Extensions.Logging` via `appsettings.json`'s
`Logging` section (the slim host wires this up automatically when
`SerilogLoggerProvider` is the only MEL provider — see `KestrelServer.cs`'s
`builder.Logging.ClearProviders(); AddProvider(new SerilogLoggerProvider(...))`):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "CopilotBridge.Cli": "Debug",
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
      "Microsoft.AspNetCore.Routing": "Warning",
      "Microsoft.AspNetCore.Server.Kestrel": "Warning"
    }
  }
}
```

> **Known gap**: the bridge's own code still uses the static `Serilog.Log.Debug(...)`
> API in places, which bypasses MEL filtering and is always emitted (subject
> only to Serilog's own minimum). Converting all static calls to injected
> `ILogger<T>` is mechanical but not yet done.

#### AOT footprint

Serilog 4.3.0+ closes the IL2072 trim warning. The destructuring path
(`{@Property}`) reflects on `obj.GetType()`'s public properties; we use the
plain message-template path everywhere, so trimming is clean. Verified by
`dotnet publish` with 0 warnings.

### 9.3 Startup auth flow

Before opening the listening socket, `BridgeHost.RunStartupAsync`:

1. Validates `Routing.Locations` (`RoutesValidator`). On failure: log error,
   exit code 2.
2. `auth.EnsureGitHubTokenAsync(ct)` — if `auth.IsAuthenticated` is false,
   logs `[INF] No GitHub token on disk — starting device-code flow`, then
   the injected `PrintDeviceCode` callback writes the verification URL +
   user code to stdout. `EnsureGitHubTokenAsync` **blocks** polling GitHub
   until the user completes the browser handshake; version 3 is encrypted and
   atomically saved as exe-local `github_credentials.dat` (DPAPI on Windows,
   derived-key protection on Linux/macOS). Before any login, CredentialService first
   migrates and continues a still-working version-1 credential. An existing
   refreshable credential is silently
   rotated first when its access-token deadline is near.
3. `auth.GetCopilotTokenAsync(ct)` — publishes an atomic version-specific lease.
   Version 2 remains a direct generic-CAPI lease with no exchange timer; versions 1 and 3 are exchanged for the
   short-lived token/endpoint lease and arms its receipt-relative refresh timer.
   A GitHub 401 on the exchange path rotates once before failing.
4. `ModelProfileCatalog` is a static DI singleton (hand-curated, no
   network call) — startup just logs its profile count and ids. The
   catalog deliberately does **not** derive from Copilot's `/models`
   metadata; see §7.2 for why.

Net behavior: a fresh checkout / fresh machine just runs `copilot-bridge
serve` and the operator pastes the device code into their browser; no
separate `auth login` step is required. `auth login` still exists for
operators who want to handshake offline before starting the server.

## 10. File layout

```
src/CopilotBridge.Cli/
├── Program.cs                                   # System.CommandLine root + subcommands; Serilog init
│
├── Auth/
│   ├── AuthService.cs                           # Copilot lease facade; no credential persistence knowledge
│   ├── CredentialService.cs                     # sole migration/login/refresh/rejection/status owner
│   ├── CredentialStore.cs                       # one versioned encrypted file + legacy migration
│   ├── TokenStore.cs                            # process-default OS protector/store composition
│   └── CopilotAuthLease.cs                      # immutable token + endpoint + deadlines + generation
│
├── Pipeline/
│   ├── BridgeContext.cs                         # context + request + response containers + ResponseMode
│   ├── RouteTarget.cs                           # record + BackendVendor enum
│   ├── PipelineRunner.cs                        # IPipelineRunner<TBody> + impl
│   ├── Pipeline.cs                              # Pipeline<TBody>
│   ├── IModelRegistry.cs                        # Resolve (name normalize + vendor/endpoint dispatch)
│   │
│   ├── Stages/                                  # IRequestStage<TBody> implementations
│   │   ├── IRequestStage.cs
│   │   ├── Anthropic/                           # for Pipeline<MessagesRequest> — IR shape stages
│   │   │   ├── ModelRouterStage.cs              # normalize + user location + profile lookup +
│   │   │   │                                    #   ProfileAdjuster + ctx.Target
│   │   │   ├── SystemSanitizeStage.cs           # strip currentDate
│   │   │   ├── MessagesSanitizeStage.cs         # tool_result merge, trailing assistant fix
│   │   │   ├── AssistantThinkingFilterStage.cs
│   │   │   ├── ToolsSanitizeStage.cs
│   │   │   └── HeadersOutboundStage.cs          # chatEndpoint.ts logic
│   │   └── (OpenAi/, Gemini/ added later — only if a future pipeline runs in non-IR shape)
│   │
│   ├── Response/                                # IResponseStage<TBody> implementations
│   │   ├── IResponseStage.cs
│   │   └── DoneFilterStage.cs                   # shape-agnostic
│   │
│   ├── Strategies/                              # backend forwarders + internal IR↔backend translators
│   │   ├── IUpstreamStrategy.cs
│   │   ├── IStrategyRegistry.cs
│   │   ├── StrategyRegistry.cs                  # default impl
│   │   ├── Anthropic/                           # Copilot /v1/messages strategies
│   │   │   └── CopilotMessagesPassthroughStrategy.cs   # M1
│   │   └── OpenAi/                              # Copilot /chat/completions strategies (M3)
│   │       └── (CopilotChatCompletionsStrategy.cs, with internal IR↔OpenAI translators)
│   │
│   ├── Adapters/                                # client-side IR boundary
│   │   ├── IClientInboundAdapter.cs
│   │   ├── IClientOutboundAdapter.cs
│   │   ├── ClaudeCode/                          # identity adapters (Anthropic shape == IR)
│   │   │   ├── ClaudeCodeInboundAdapter.cs
│   │   │   └── ClaudeCodeOutboundAdapter.cs
│   │   ├── Codex/                               # M3 — OpenAI ↔ IR translators
│   │   └── Gemini/                              # M4 — Gemini ↔ IR translators
│   │
│   └── Routing/
│       ├── CopilotModelRegistry.cs              # IModelRegistry impl: pure name normalize +
│       │                                        #   prefix-based vendor/endpoint dispatch
│       ├── ModelProfile.cs                      # per-model wire-truth record (effort, thinking,
│       │                                        #   mid-conv-system, betas, budget cap)
│       ├── ModelProfileCatalog.cs               # hand-curated id → profile map, sourced from
│       │                                        #   ModelProfileProbe.cs playground results
│       ├── ProfileAdjuster.cs                   # pure body coercion against a profile
│       ├── UnknownModelException.cs             # thrown by ModelRouterStage on profile miss;
│       │                                        #   endpoint converts to 400 + Anthropic error body
│       ├── RoutesConfig.cs                      # POCOs bound from appsettings.json Routing.Locations
│       │                                        #   (RouteLocation = When + Use{Model,EffortMap,Headers})
│       ├── MatchExpression.cs                   # When tree: AllOf/AnyOf + Model/Effort/Header leaves
│       ├── ModelRouteResolver.cs                # first-match-wins; apply Use (model/effort/headers)
│       └── RoutesValidator.cs                   # startup fail-fast schema + header-whitelist validation
│
├── Endpoints/
│   ├── ClaudeCode/
│   │   ├── ClaudeCodeEndpoints.cs               # Map() — registers all /cc/v1/... handlers
│   │   ├── ClaudeCodeMessagesEndpoint.cs        # POST /cc/v1/messages
│   │   ├── ClaudeCodeCountTokensEndpoint.cs     # POST /cc/v1/messages/count_tokens
│   │   └── ClaudeCodeModelsEndpoint.cs          # GET  /cc/v1/models
│   ├── Codex/                                   # M3+
│   │   ├── CodexResponsesEndpoint.cs            # POST /codex/responses
│   │   └── CodexModelsEndpoint.cs               # GET  /codex/models?client_version=...
│   └── Gemini/                                  # M4
│
├── Hosting/
│   ├── KestrelServer.cs                         # builds + runs the HTTP host
│   ├── BridgePipelines.cs                       # the assembly: builds Pipeline<MessagesRequest>
│   ├── BridgeHost.cs                            # ConfigureServices + RunStartupAsync (auth, routes,
│   │                                            #   ModelProfileCatalog log)
│   ├── ServeCommand.cs                          # typed entry point: RunAsync(int port)
│   └── Logging/
│       ├── BridgeIoEvents.cs                    # EventIds 1001..1004 (the four IO artifacts)
│       ├── BridgeIoPayload.cs                   # sink payload + ArrayPool ownership
│       ├── BridgeIoLoggerExtensions.cs          # logger.LogInboundRequest / ...
│       ├── BridgeIoSink.cs                      # bounded Channel + worker, writes per-request JSONs
│       └── BridgeIoSinkHolder.cs                # static handoff slot (Program.cs ↔ DI)
│
└── appsettings.json                             # ships next to the .exe; PreserveNewest copy.
                                                 # Holds Routing.Locations (When/Use) + Tracing toggle.
                                                 # Wire facts (effort, thinking, betas) live in
                                                 # ModelProfileCatalog.cs, not here.
```

## 11. Evolution path

### 11.1 Adding a new client (e.g. Gemini)

1. Define source-generated DTOs for the client's request and response shape.
2. Add the client's URL-prefixed endpoints.
3. Implement the client-edge inbound/outbound adapters to and from the shared
   Anthropic-shape IR.
4. Register those adapters and endpoints against the existing scoped
   `Pipeline<MessagesRequest>`.
5. Add a backend strategy only if the client introduces a backend shape that
   the bridge does not already expose through Copilot.

The pipeline framework, runner, response stages, audit log — all reused.

### 11.2 Adding a new backend route (e.g. Anthropic client → OpenAI backend)

1. Add a new strategy class:
   `Pipeline/Strategies/OpenAi/CopilotChatCompletionsTranslateFromAnthropicStrategy.cs`.
   Inside it: `IBodyTranslator<MessagesRequest, ChatCompletionsRequest>` and
   `IStreamTranslator<OpenAiSseChunk, AnthropicSseEvent>`.
2. Register it in `Pipeline<MessagesRequest>`'s strategy registry, matching
   targets where `BackendVendor == CopilotOpenAi`.
3. Update `CopilotModelRegistry`'s prefix dispatch (or add a new entry to
   `ModelProfileCatalog`) so the relevant model ids resolve to that vendor.

Pipeline stages, audit log, endpoints — all reused.

### 11.3 Substituting a model alias (the explicit override)

When Copilot ships a model the bridge has no profile for yet, it is
**forwarded automatically** under the nearest known profile (see §7 —
best-effort fuzzy matching), with a WARN log. The operator only needs to act if
they want to override that automatic choice — e.g. pin a new id to a *specific*
known model rather than whichever the matcher picks. They do so with a redirect
location in `appsettings.json`:

```jsonc
{
  "When": { "Model": "claude-opus-5" },
  "Use":  { "Model": "claude-opus-4.8" }
}
```

The redirect is a user-visible knob: the operator can confirm a specific
fallback works for their workload, and remove the location once the bridge ships
a real `ModelProfile` for the new id. The fuzzy matcher is the default;
`ModelNameMatcher` uses Jaccard similarity over character bigrams with a
family-then-version tie-break, so it already prefers the same-family, highest
-version sibling — the redirect is for when the operator wants a *different*
target than that.

Stage: `ModelRouterStage` normalizes the inbound id, runs the location matcher
to apply the `Use` change-set, looks up the target profile (exact, then nearest
via fuzzy match), and runs `ProfileAdjuster`. Only if the resolved id is too
dissimilar to *any* known model — below the `ModelNameMatcher` floor — does the
bridge 400 with a message naming the nearest rejected candidate (and, if a
routing location produced the id, the offending `Routing.Locations[i]`).

## 12. Migration plan from current code

> **Completed 2026-05-07** — kept as a record. The unprefixed `/v1/...`
> endpoints described as "current" below were deleted; `/cc/v1/...` is the
> production path. The `DiagLog`/`BRIDGE_DIAG` machinery referenced in step 2
> was later replaced by Serilog (see §9; `BRIDGE_DIAG` no longer exists in
> the csproj). The `BridgeRequestLog` / `BridgeRequestLogger` types
> mentioned in step 2 were subsequently rewritten as a custom Serilog sink
> (`BridgeIoSink`) with four ILogger extension methods and per-request
> ArrayPool-backed JSON files — see §9.1.

The starting state (M1 step 6a) had `Endpoints/MessagesEndpoint.cs`,
`Endpoints/ModelsEndpoint.cs`, `Endpoints/CountTokensEndpoint.cs` registered at
unprefixed paths (`/v1/messages`, etc.) with inline logic.

Migration steps (one PR per step preferred):

1. **Build pipeline framework** (everything in `src/CopilotBridge.Cli/Pipeline/`):
   container, stage interfaces, runner, registry types, **adapter interfaces**
   (`IClientInboundAdapter`, `IClientOutboundAdapter`), DiagLog. No stages, no
   strategies, no concrete adapters, no endpoint changes. Compile + AOT clean
   (the unused framework gets trimmed away by the AOT linker).
2. **Wire DiagLog through** the existing `BridgeRequestLogger`: add a `Diag`
   field on `BridgeRequestLog` and emit a `diag` JSON section if non-null. Add
   `BRIDGE_DIAG` to `<DefineConstants>` in the Debug configuration.
3. **Implement Claude Code identity adapters** (`ClaudeCodeInboundAdapter`,
   `ClaudeCodeOutboundAdapter`) — they're trivial passthroughs for M1 (Anthropic
   shape == IR) but establish the integration pattern.
4. **Implement M1 stages** under `Pipeline/Stages/Anthropic/`. Each stage one
   PR or one logical group of PRs. Some are passthrough (do nothing) for now —
   build the box, fill it incrementally.
5. **Implement `CopilotMessagesPassthroughStrategy`** moving the HTTP call
   logic out of `MessagesEndpoint.cs` into the strategy.
6. **Implement `CopilotModelRegistry`** under `Pipeline/Routing/` with the
   alias table; wire it into `ModelRouterStage`.
7. **Build the Claude Code endpoint set** under `Endpoints/ClaudeCode/`,
   mounted at `/cc/v1/...`. Handlers are thin: parse → adapter.AdaptAsync →
   build context → run pipeline → adapter.AdaptStreamAsync → write outbound.
8. **Update `.claude/settings.local.json`** to set
   `ANTHROPIC_BASE_URL=http://localhost:8765/cc`.
9. **Delete the old endpoints** (`Endpoints/MessagesEndpoint.cs` etc.) once
   `/cc/v1/...` is verified end-to-end.
10. **Re-run the harness prompts** (`tests/harness/prompts/`) and the playground
    tests. All must still pass.

Throughout the migration, the audit log shape is preserved (existing log
format remains compatible). Diag log is additive (new section in the JSON).

## 13. Open questions / future revisions

- **Where does the request body parsing happen** — in the endpoint handler or
  inside `InboundCaptureStage`? Current proposal: in the stage, so the parsing
  rules (which `JsonContext`, which polymorphism settings) live in the
  pipeline rather than the endpoint. Endpoints become near-trivial wrappers.
- **Should `IResponseStage<TBody>` ever need to see the typed request body?**
  Examples: a stage that adds a downstream-shaped trailing event based on what
  was requested. Current decision: yes — `TBody` is in scope for symmetry, and
  most stages will ignore it.
- **Do stages need configuration objects?** For now no — all M1 stages have no
  per-instance state. If we add e.g. a `RateLimitStage`, configuration enters.
  At that point introduce `IRequestStage<TBody, TConfig>` or a sealed
  `StageOptions<TStage>` pattern.
- ~~**Diag log for production debugging** — currently `[Conditional]` strips
  it from Release.~~ **Resolved (v0.3)**: Serilog now logs in Release too;
  level is controlled at runtime via `BRIDGE_LOG_LEVEL`. No build-time gate.
