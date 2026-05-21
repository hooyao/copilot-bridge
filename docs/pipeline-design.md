# Bridge Pipeline Design

> Status: v0.3 · 2026-05-08
>
> This document is the architectural contract for the bridge's request/response
> transformation framework. New stages, new clients, new backends should
> conform to the abstractions defined here. Diverging requires updating this
> document first.
>
> **2026-05-08 (v0.3) — three migrations**:
> (a) Routing redesigned. Per-model effort behavior is now C# capability data
>     in `CopilotModelRegistry.EffortAware`; `appsettings.json` keeps only
>     user preferences as `Routing.Rules` (Match → Rewrite). See §7. (b) Logger
>     swapped from the bespoke `DiagTracer` (`[Conditional("BRIDGE_DIAG")]`) to
>     Serilog 4.3.1 (AOT-clean since PR #2175) with console + per-startup file
>     sinks. See §9. (c) `Program.cs` uses `System.CommandLine` 3.0-preview.3
>     for argument parsing; subcommand handlers are typed entry points instead
>     of `string[]` parsers.
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
6. **Two log channels** — per-request inbound/upstream IO is captured as four
   discrete JSON artifacts (`logs/<utc>-<seq>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`)
   through a custom Serilog sink with a bounded back-pressured queue;
   runtime diagnostics go through Serilog (console + per-startup file under
   `log/`), with per-category levels driven by appsettings.json's `Logging`
   section. See §9.
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

### 3.3 Translator inventory

Request side (client → IR → backend):

| Direction | Translator | Status |
| --- | --- | --- |
| Anthropic client → IR | identity | M1 |
| OpenAI client → IR | `OpenAiToBridgeRequestAdapter` | M3 |
| Gemini client → IR | `GeminiToBridgeRequestAdapter` | M4 |
| IR → Anthropic backend | identity | M1 |
| IR → OpenAI backend | `BridgeToOpenAiRequestAdapter` | M3 |

Response side (backend → IR → client; all streaming variants are stateful):

| Direction | Translator | Status |
| --- | --- | --- |
| Anthropic backend → IR | identity | M1 |
| OpenAI backend → IR | `OpenAiToBridgeResponseAdapter` | M3 |
| IR → Anthropic client | identity | M1 |
| IR → OpenAI client | `BridgeToOpenAiResponseAdapter` | M3 |
| IR → Gemini client | `BridgeToGeminiResponseAdapter` | M4 |

**Total non-identity translators: 6** (3 in each direction). Each request
transits at most 2. Adding a new client = 1 inbound + 1 outbound adapter; a new
backend shape = 1 of each. Linear growth.

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
    /// <summary>Original inbound bytes — read-only after InboundCaptureStage.</summary>
    public ReadOnlyMemory<byte> RawBody { get; init; }
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
    Task ApplyAsync(BridgeContext<TBody> ctx);
}

internal interface IResponseStage<TBody> where TBody : class
{
    string Name { get; }
    Task ApplyAsync(BridgeContext<TBody> ctx);
}
```

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
    /// in the SAME shape the inbound client expects.
    /// </summary>
    Task ForwardAsync(BridgeContext<TBody> ctx);
}
```

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
    Task RunAsync(Pipeline<TBody> pipeline, BridgeContext<TBody> ctx);
}
```

Implementation contract:

```
RunAsync:
    foreach stage in pipeline.RequestStages:
        Log.Debug("req-stage start {Stage}"); apply; Log.Debug("req-stage end {Stage}")
    strategy = pipeline.Strategies.Resolve(ctx.Target)
    Log.Debug("strategy resolved {Name} target={Target}")
    strategy.ForwardAsync(ctx)
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
/codex/v1/...      → Codex adapters (translate OpenAI shape ↔ IR)
/gemini/v1/...     → Gemini adapters (translate Gemini shape ↔ IR)
```

Strategy-side translators (IR ↔ backend shape) are an internal concern of the
strategy and do not implement these interfaces — they live as private fields
inside, e.g., `CopilotChatCompletionsStrategy<TIR>`.

## 5. Request pipeline (current: `Pipeline<MessagesRequest>`)

The endpoint handler parses the JSON body before constructing
`BridgeContext<MessagesRequest>`; there is no separate `InboundCaptureStage`.
The assembled stage list is in `Hosting/BridgePipelines.cs`.

```
inbound bytes (POST /cc/v1/messages)
    │
    ▼  (parsed by endpoint handler into MessagesRequest)
    │
[1] ModelRouterStage              normalize model id; apply user
                                  Routing.Rules (Match → Rewrite); apply
                                  CopilotModelRegistry capability (effort →
                                  variant suffix, strip on the wire); resolve
                                  ctx.Target. See §7.
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

`CacheControlCleanStage` from research §3.6 rule 1 is intentionally absent —
the DTO does not model `cache_control.scope`, so the field is silently dropped
at deserialize time. Add the stage if a `Scope` property is ever introduced.

The previous standalone `ThinkingRewriteStage` is gone: its haiku adaptive →
enabled and opus-4.7 enabled → adaptive coercions live as data in
`appsettings.json`'s `Routing.Rules`, applied by `ModelRouterStage` via
`ModelRouteResolver`. See §7.

## 6. Response pipeline (M1 example)

```
ctx.Response.EventStream  (raw events from upstream, or post-translation
                           if strategy wrapped them)
    │
    ▼
[A] DoneFilterStage               drops the OpenAI-style `event:message
                                  data:[DONE]` terminator
    │
    ▼
[B] EventCaptureStage             appends each event to ctx.Log.Events
                                  for the audit log (always-on)
    │
    ▼
[C] ResponseHeadersStage          drop x-quota-snapshot-*, x-github-* etc.
                                  from outbound response headers (optional)
    │
    ▼
endpoint writes ctx.Response.EventStream to inbound client
```

For non-streaming responses, the `EventStream` is null; stages operate on
`BufferedBody`. The shape of stages is the same; each checks the `Mode`.

Stages that need to **transform** events (rather than drop/observe) wrap the
async enumerable with their own transformation:

```csharp
public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
{
    var source = ctx.Response.EventStream!;
    ctx.Response.EventStream = TransformEvents(source, ctx.Ct);
    return Task.CompletedTask;
}

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

## 7. Routing semantics

Three concentric layers, each with a distinct responsibility:

```
inbound (model, effort, thinking, ...)
   ↓ Normalize (canonical id)
   ↓ User rules     (JSON, in appsettings.json)        — operator preferences
   ↓ Capability     (C#, in CopilotModelRegistry)      — physical wire facts
   ↓ Resolve        (vendor + endpoint dispatch)
outbound to upstream
```

The split is "what users decide" vs "what the wire requires." User preferences
are JSON; physical wire facts are code. Each scales linearly with the number
of models we cover.

### 7.1 Vendor + endpoint dispatch — `IModelRegistry.Resolve`

Maps a (canonical) model id to `(BackendVendor, default endpoint, model id)`.
Hardcoded by prefix in `CopilotModelRegistry`:

| Prefix | Vendor | Default endpoint |
| --- | --- | --- |
| `claude-*` | CopilotAnthropic | `/v1/messages` |
| `gpt-*`, `o3-*`, `o4-*`, `gemini-*` | CopilotOpenAi | `/chat/completions` |

Plus a `Normalize` step that canonicalizes the inbound id:

- Strips a trailing 8-digit date suffix (`claude-sonnet-4-5-20250929` → `claude-sonnet-4-5`)
- Merges the first consecutive `digit-digit` pair into `digit.digit`
  (`claude-opus-4-7` → `claude-opus-4.7`,
  `claude-opus-4-7-1m-internal` → `claude-opus-4.7-1m-internal`)

`Resolve` returns `null` for unknown prefixes; the runtime stage throws a
descriptive error in that case (fail-fast, not silent passthrough).

`Aliases` (currently empty) is a static degradation table for "client requests
a model the backend doesn't have yet" — populated when needed
(e.g. `claude-opus-4.8 → claude-opus-4.7`).

### 7.2 Per-model effort capability — `CopilotModelRegistry.EffortAware`

Per-model wire facts about `output_config.effort` live in code, not JSON. One
entry per known effort-aware model; the value is a `ModelCapability` declaring
the effort → variant-suffix map. Models **not** in the table fall through to
the safe default ("strip the effort field, keep the model"):

```csharp
private static readonly Dictionary<string, ModelCapability> EffortAware = new(
    StringComparer.OrdinalIgnoreCase)
{
    ["claude-opus-4.7"] = new(EffortToSuffix: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["max"]    = "-xhigh",   // no -max variant; xhigh is the closest sized variant
        ["xhigh"]  = "-xhigh",
        ["high"]   = "-high",
        ["medium"] = "",         // base model is implicitly medium-effort
        ["low"]    = "",         // no -low variant; clamp to base
    }),
};
```

`IModelRegistry.ApplyEffortRouting(modelId, effort)` returns
`(newModel, stripEffort)`:

- effort is null → no-op
- model not in `EffortAware` → strip effort, keep model
- effort matches an entry → append the suffix, strip effort

Rationale for embedding in code rather than JSON: this collapses the previous
seven `VariantRoutes` rows for opus-4.7 into one capability entry, and the
table grows linearly with the number of effort-aware models (small) rather
than (model × effort) combinations (cartesian).

> **M3 TODO** — replace the static table with a startup-built one populated
> from `CopilotClient.GetModelsAsync()`. The capability shape will grow new
> fields (`SupportedEndpoints`, accepted thinking shapes); see §7.4.

### 7.3 User routing rules — `appsettings.json` `Routing.Rules`

Loaded via standard `IConfiguration` / `IOptions<T>` at startup. Validated by
`RoutesValidator` before Kestrel binds the port. **No hot reload** — edit and
restart.

Single linear scan, first-match-wins. Each rule has a `Match` (AND-combined,
null = no constraint) and a `Rewrite` (any subset of model / effort / thinking
fields). If the rule sets `Effort`, that field is "claimed" — phase 7.2 won't
re-touch it (so `Rewrite: { Effort: "xhigh" }` round-trips even when the
target model isn't in `EffortAware`).

Schema:

```jsonc
{
  "Routing": {
    "Rules": [
      {
        "Match":   { "InboundModel": "claude-haiku-4.5", "InboundThinking": "adaptive" },
        "Rewrite": { "Thinking": { "Type": "enabled" }, "DeriveBudgetFromEffort": true },
        "Note":    "haiku-4.5 advertises adaptive but rejects it at runtime"
      },
      {
        "Match":   { "InboundModel": "claude-opus-4.7", "InboundThinking": "enabled" },
        "Rewrite": { "Thinking": { "Type": "adaptive" }, "DeriveEffortFromBudget": true },
        "Note":    "opus-4.7 base only accepts adaptive thinking"
      }
    ]
  }
}
```

`Rewrite` fields:

| Field | Meaning |
| --- | --- |
| `Model: "..."` | Replace outbound model with this canonical id. |
| `Effort: "..."` | Replace outbound `output_config.effort`. Marks effort as user-explicit; phase 7.2 won't override. |
| `Thinking: { Type, BudgetTokens? }` | Replace `thinking` block. `Type` is `"enabled" \| "adaptive" \| "disabled"`. |
| `DeriveBudgetFromEffort: true` | After applying `Thinking` (or if already `enabled`), set `budget_tokens` from current effort using the standard mapping (low=4096, medium=16384, high=32768, xhigh/max=64000). |
| `DeriveEffortFromBudget: true` | When the post-rewrite `Thinking` is `enabled`, derive effort from `budget_tokens` using the inverse mapping. Marks effort user-explicit. |

The rule list typically holds three kinds of entries:

1. **Per-model thinking quirks** — what the previous `ShapeRewrites` did.
   These ship by default in the example `appsettings.json`.
2. **User redirects** — operator preferences, e.g.
   `{ Match: {InboundModel: "claude-opus-4.7"}, Rewrite: {Model: "claude-opus-4.6"} }`.
3. **Bug-patches** — when Copilot ships a new model whose physical
   capability table is stale, the operator can write a rule to set the
   right effort/model on the wire. After the next bridge release adapts
   `EffortAware`, the rule can be removed and zero-config behavior is
   restored.

`RoutesValidator` enforces: each `Match` constrains at least one dimension;
`Match.InboundThinking` and `Rewrite.Thinking.Type` are one of
`enabled / adaptive / disabled`. Invalid config fails the process at startup.

### 7.4 Endpoint selection for multi-endpoint models (M3 design)

When a single model is served by multiple Copilot endpoints (gpt-5 supports
both `/chat/completions` and `/responses`), the bridge picks **automatically**
based on hardcoded heuristics — **not** a user setting:

- Single-endpoint model → use that one
- Multi-endpoint model with `thinking` enabled or multi-turn agent loop →
  prefer `/responses` (reasoning state persistence)
- Otherwise → `/chat/completions` (simpler, cross-model compatible)

Rationale: endpoint choice is a **derived fact** from request features +
model capabilities, not user preference. Forcing users to know "use
/responses for o-series, /chat for gpt-4.1, gpt-5 depends on whether you
want reasoning persistence" is unreasonable.

Implementation plan: extend `ModelCapability` with `SupportedEndpoints`
(populated from Copilot's live `/models` response at startup), and add
`CopilotModelRegistry.SelectEndpoint(modelId, body)` that applies the
heuristic above.

### 7.5 1M-context routing — model-variant + beta-header rewrite (planned)

> **Status: not yet implemented.** This section captures the design so
> the next developer can pick it up directly. The unknowns have been
> probed; the configuration shape and code touch points are concrete.

#### The problem

Claude Code's "1M context" toggle ships as an `anthropic-beta` header value
`context-1m-2025-08-07`, not a different model name. On Anthropic Direct
that header opts the request into a 1M context window for the same
`claude-opus-4.7` model.

Copilot does **not** accept that beta token. Instead, it exposes 1M variants
as **dedicated model ids**. Verified empirically by running
`CopilotGapProbes.DumpClaudeModelsAndCapabilities` against the live
`/models` endpoint:

| Copilot model id                | `max_context_window_tokens` |
|---------------------------------|-----------------------------|
| `claude-opus-4.7`               | 200 000                     |
| `claude-opus-4.7-high`          | 200 000                     |
| `claude-opus-4.7-xhigh`         | 200 000                     |
| `claude-opus-4.7-1m-internal`   | **1 000 000**               |
| `claude-opus-4.6`               | 200 000                     |
| `claude-opus-4.6-1m`            | **1 000 000**               |

So the bridge needs to: (a) detect the inbound 1M beta token, (b) rewrite
`body.model` to the dedicated 1M variant, and (c) **strip** the beta from
the outbound header set (Copilot would reject it).

#### Configuration shape

Extend the existing `Routing.Rules` with two new fields. Reusing the
first-match-wins, AND-combined `Match` machinery keeps the mental model
uniform — 1M is just another (model, beta) → (model, header-strip)
rewrite.

```json
{
  "Routing": {
    "Rules": [
      {
        "Match": {
          "InboundModel": "claude-opus-4.7",
          "InboundBeta":  "context-1m-2025-08-07"
        },
        "Rewrite": {
          "Model":      "claude-opus-4.7-1m-internal",
          "StripBetas": ["context-1m-*"]
        },
        "Note": "1M context: Copilot exposes it as a dedicated model id, not a beta"
      },
      {
        "Match": {
          "InboundModel": "claude-opus-4.6",
          "InboundBeta":  "context-1m-2025-08-07"
        },
        "Rewrite": {
          "Model":      "claude-opus-4.6-1m",
          "StripBetas": ["context-1m-*"]
        }
      }
    ]
  }
}
```

`Match.InboundBeta` semantics: the inbound `anthropic-beta` header is split
on `,`; the rule matches when the resulting set contains the given token
(case-insensitive). For future "multiple betas must all be present"
scenarios, the field can grow into `InboundBetas: [...]` later — start
narrow.

`Rewrite.StripBetas` semantics: each pattern is matched against tokens in
the outbound header list; matching tokens are removed. `*` is a trailing
wildcard so `context-1m-*` covers future spec dates without code changes.

#### Inbound beta default behavior

**Currently** (`HeadersOutboundStage.cs`): the inbound `anthropic-beta`
header is **discarded entirely** and the outbound list is rebuilt from
scratch by the stage (adaptive thinking, context-management,
tool-search). This is broken in two directions:

- Tokens Copilot *does* accept (`interleaved-thinking-2025-05-14`,
  `context-management-2025-06-27`, `advanced-tool-use-2025-11-20`) are
  derived from request shape on every call, so they happen to land
  correctly even though inbound is ignored.
- Tokens Copilot *does not* accept (`context-1m-2025-08-07`,
  `prompt-caching-scope-2026-01-05`, `claude-code-20250219`) are silently
  dropped — which is fine for the rejection case but loses the signal
  that we needed to act on (e.g. trigger the model rewrite above).

**The fix** has two parts:

1. **Parse inbound betas into IR**, so rules can match on them. Add a
   transient (non-wire-serialized) field on the request DTO populated by
   the inbound adapter, e.g.
   `MessagesRequest { [JsonIgnore] InboundBetaSet { get; init; } }`. The
   pipeline stages use it; `HeadersOutboundStage` reads `StripBetas`
   directives accumulated on `BridgeContext` to decide what to forward.
2. **Establish a forward whitelist** for unknown betas. Open question — the
   project is fact-driven, so the right default depends on what Copilot
   actually does with unknown beta tokens. Run `BetaAcceptanceTests` (see
   `tests/CopilotBridge.Playground/BetaAcceptanceTests.cs`) for every known
   Anthropic beta name and observe the matrix (200 vs 400 vs silently
   ignored). Decide the default once data is in.

#### Effort × 1M interaction

Copilot's `-1m-internal` variant does **not** have its own `-high` /
`-xhigh` suffix (per the `/models` dump above). So when a rule rewrites
`claude-opus-4.7` (with effort=max) to `claude-opus-4.7-1m-internal`, the
effort field should pass through unchanged — Copilot's catalog-driven
effort router will then handle it as a single model + body.effort. No
extra wiring needed in the rule.

#### Code touch points

1. `Models/Anthropic/Request/MessagesRequest.cs` — add
   `[JsonIgnore] IReadOnlyList<string>? InboundBetaHeader { get; init; }`
   field (or pass via `BridgeContext` — pick one).
2. `Pipeline/Adapters/ClaudeCode/ClaudeCodeInboundAdapter.cs` — parse
   `anthropic-beta` header CSV from the headers dictionary, populate the
   field.
3. `Pipeline/Routing/RoutesConfig.cs` — add `InboundBeta` to `RouteMatch`,
   `StripBetas` to `RuleRewrite`.
4. `Pipeline/Routing/ModelRouteResolver.cs` — extend `Matches()` for the
   new field; extend `ApplyRewrite()` to accumulate strip directives on
   the context.
5. `Pipeline/Stages/Anthropic/HeadersOutboundStage.cs` — merge inbound
   betas with computed betas, then apply strip patterns.
6. `Pipeline/Routing/RoutesValidator.cs` — validate `InboundBeta` is a
   non-empty token; validate `StripBetas` patterns are non-empty.
7. `tests/CopilotBridge.Playground/BetaAcceptanceTests.cs` — extend to
   cover `context-1m-2025-08-07` and the two Claude Code 2.1.x betas
   currently marked "acceptance unknown".
8. `appsettings.json` — add the two new rules.

#### Verification

A `Headless/` test that drives a real `claude.exe` with the 1M effort
toggle on (so the inbound carries the beta), then asserts that the audit
JSON for that request shows `body.model == "claude-opus-4.7-1m-internal"`
on the upstream side and **no** `context-1m-*` token in the upstream
headers. The `HeadlessRunner` pattern already exists for effort routing
tests (`tests/.../Headless/EffortRoutingTests.cs`).

## 8. Per-client URL prefixes

Each client is mounted on its own URL prefix. This eliminates body sniffing
entirely — the URL **is** the client identity.

| Prefix | Client | Inbound shape | Endpoints |
| --- | --- | --- | --- |
| `/cc/v1/...` | Claude Code | Anthropic Messages | `messages`, `messages/count_tokens`, `models` |
| `/codex/v1/...` (M3) | Codex | OpenAI Chat Completions / Responses | `chat/completions`, `responses`, `models` |
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
    CodexChatCompletionsEndpoint.cs
    ...
```

Configuration impact: Claude Code's `ANTHROPIC_BASE_URL` environment variable
must be set to `http://localhost:8765/cc` (note the `/cc` suffix), so its
client appends `/v1/messages` and lands on our `/cc/v1/messages`.

## 9. Logging

Two log channels, both flowing through a single Serilog pipeline configured
once in `Program.cs`.

### 9.1 Per-request IO audit (always-on, four files per request)

Each inbound request produces **four** JSON files in `<exe-dir>/logs/`,
sharing a stamp so they're trivially groupable:

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
streaming response.

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
   ▼  returns pooled byte[] back to ArrayPool<byte>.Shared
```

#### Buffer ownership

Endpoints read inbound bodies into `ArrayPool<byte>.Shared`-rented buffers
to avoid GC pressure on conversation-sized payloads. The pooled buffer is
**not** the one handed to the sink — endpoints make a one-shot copy for
the audit and keep the pooled buffer alive for the pipeline's `RawBody`
view. The endpoint's `finally` block returns the pool buffer after the
request completes.

The sink-owned body buffer is tracked by `BridgeIoPayload.BodyPooled` and
released by the worker after the audit JSON is written (via
`BridgeIoPayload.Release()`).

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

1. Validates `Routing.Rules` (`RoutesValidator`). On failure: log error,
   exit code 2.
2. `auth.EnsureGitHubTokenAsync(ct)` — if `auth.IsAuthenticated` is false,
   logs `[INF] No GitHub token on disk — starting device-code flow`, then
   the injected `PrintDeviceCode` callback writes the verification URL +
   user code to stdout. `EnsureGitHubTokenAsync` **blocks** polling GitHub
   until the user completes the browser handshake; the token is then
   DPAPI-encrypted and saved next to the .exe.
3. `auth.GetCopilotTokenAsync(ct)` — exchanges the GitHub token for a
   short-lived Copilot bearer token, started the in-memory refresh timer.
4. `catalog.LoadFromAsync(...)` — fetches `/models`; on failure logs a
   warning but continues (effort routing degrades to "strip the field
   for unknown models").

Net behavior: a fresh checkout / fresh machine just runs `copilot-bridge
serve` and the operator pastes the device code into their browser; no
separate `auth login` step is required. `auth login` still exists for
operators who want to handshake offline before starting the server.

## 10. File layout

```
src/CopilotBridge.Cli/
├── Program.cs                                   # System.CommandLine root + subcommands; Serilog init
│
├── Pipeline/
│   ├── BridgeContext.cs                         # context + request + response containers + ResponseMode
│   ├── RouteTarget.cs                           # record + BackendVendor enum
│   ├── PipelineRunner.cs                        # IPipelineRunner<TBody> + impl
│   ├── Pipeline.cs                              # Pipeline<TBody>
│   ├── IModelRegistry.cs                        # Resolve + ApplyEffortRouting; impls under Routing/
│   │
│   ├── Stages/                                  # IRequestStage<TBody> implementations
│   │   ├── IRequestStage.cs
│   │   ├── Anthropic/                           # for Pipeline<MessagesRequest> — IR shape stages
│   │   │   ├── ModelRouterStage.cs              # normalize + user rules + capability + ctx.Target
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
│       ├── CopilotModelRegistry.cs              # IModelRegistry impl: Resolve + Normalize +
│       │                                        #   EffortAware capability dictionary
│       ├── RoutesConfig.cs                      # POCOs bound from appsettings.json Routing.Rules
│       ├── ModelRouteResolver.cs                # apply user rules then capability
│       └── RoutesValidator.cs                   # startup fail-fast schema validation
│
├── Endpoints/
│   ├── ClaudeCode/
│   │   ├── ClaudeCodeEndpoints.cs               # Map() — registers all /cc/v1/... handlers
│   │   ├── ClaudeCodeMessagesEndpoint.cs        # POST /cc/v1/messages
│   │   ├── ClaudeCodeCountTokensEndpoint.cs     # POST /cc/v1/messages/count_tokens
│   │   └── ClaudeCodeModelsEndpoint.cs          # GET  /cc/v1/models
│   ├── Codex/                                   # M3
│   │   └── (CodexChatCompletionsEndpoint, CodexResponsesEndpoint, CodexModelsEndpoint)
│   └── Gemini/                                  # M4
│
├── Hosting/
│   ├── KestrelServer.cs                         # builds + runs the HTTP host; loads RoutesConfig
│   ├── BridgePipelines.cs                       # the assembly: builds Pipeline<MessagesRequest>
│   ├── BridgeHost.cs                            # ConfigureServices + RunStartupAsync (auth, models)
│   ├── ServeCommand.cs                          # typed entry point: RunAsync(int port)
│   └── Logging/
│       ├── BridgeIoEvents.cs                    # EventIds 1001..1004 (the four IO artifacts)
│       ├── BridgeIoPayload.cs                   # sink payload + ArrayPool ownership
│       ├── BridgeIoLoggerExtensions.cs          # logger.LogInboundRequest / ...
│       ├── BridgeIoSink.cs                      # bounded Channel + worker, writes per-request JSONs
│       └── BridgeIoSinkHolder.cs                # static handoff slot (Program.cs ↔ DI)
│
└── appsettings.json                             # ships next to the .exe; PreserveNewest copy.
                                                 # Holds Routing.Rules — user preferences only.
                                                 # Wire facts (effort → variant) live in
                                                 # CopilotModelRegistry.EffortAware, not here.
```

## 11. Evolution path

### 11.1 Adding a new client (e.g. Codex)

1. Define `Models/OpenAi/ChatCompletionsRequest.cs` (and friends).
2. Add `Endpoints/Codex/CodexEndpoints.cs` mounting `/codex/v1/...`.
3. Add `Pipeline/Stages/OpenAi/` with OpenAI-specific stages
   (CodexInboundCaptureStage, CodexModelRouterStage, ...).
4. Build `Pipeline<ChatCompletionsRequest>` in `Hosting/BridgePipelines.cs`.
5. Register Codex strategies in the strategy registry.

The pipeline framework, runner, response stages, audit log — all reused.

### 11.2 Adding a new backend route (e.g. Anthropic client → OpenAI backend)

1. Add a new strategy class:
   `Pipeline/Strategies/OpenAi/CopilotChatCompletionsTranslateFromAnthropicStrategy.cs`.
   Inside it: `IBodyTranslator<MessagesRequest, ChatCompletionsRequest>` and
   `IStreamTranslator<OpenAiSseChunk, AnthropicSseEvent>`.
2. Register it in `Pipeline<MessagesRequest>`'s strategy registry, matching
   targets where `BackendVendor == CopilotOpenAi`.
3. Update `ModelRegistry` to map the relevant model ids to that vendor.

Pipeline stages, audit log, endpoints — all reused.

### 11.3 Substituting a model alias (the immediate case)

`Pipeline/ModelRegistry.cs` carries a static alias table:

```csharp
private static readonly Dictionary<string, string> Aliases = new()
{
    // Claude Code may default to a model not yet on Copilot.
    ["claude-opus-4.8"] = "claude-opus-4.7",
    ["claude-opus-5"]   = "claude-opus-4.7",
    // ...
};
```

Stage: `ModelRouterStage` resolves from alias, then validates against the live
Copilot model list, then sets `ctx.Request.Body.Model` to the resolved id.

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
