# Bridge Pipeline Design

> Status: v0.1 · 2026-05-06
>
> This document is the architectural contract for the bridge's request/response
> transformation framework. New stages, new clients, new backends should
> conform to the abstractions defined here. Diverging requires updating this
> document first.

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
6. **Verbose diagnostic logging is `[Conditional]`-stripped in Release** — the
   structured per-request audit log (`logs/<utc>-<seq>.json`) is always written;
   the per-stage diff/timing log is compiled out of Release builds for zero
   runtime cost.
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
  a `DiagLog.Diff` warning.
- **Tool result message structure**: Anthropic puts tool_result as a content
  block inside the next user message; OpenAI uses a dedicated `tool` role
  message. Translation maintains the `assistant tool_use → tool_result → user`
  ordering.

Every lossy mapping calls `DiagLog.Diff(...)` so debug builds surface what was
dropped or approximated.

## 4. Core abstractions

### 4.1 BridgeContext

```csharp
internal sealed class BridgeContext<TBody> where TBody : class
{
    public required BridgeRequest<TBody> Request { get; init; }
    public required BridgeResponse Response { get; init; }
    public required RouteTarget Target { get; set; }
    public required BridgeRequestLog Log { get; init; }
    public required CancellationToken Ct { get; init; }
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
        DiagLog.StageStart(name); apply; DiagLog.StageEnd
    strategy = pipeline.Strategies.Resolve(ctx.Target)
    DiagLog.StrategyStart(name); strategy.ForwardAsync(ctx); DiagLog.StrategyEnd
    foreach stage in pipeline.ResponseStages:
        DiagLog.StageStart; apply; DiagLog.StageEnd
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

## 5. Request pipeline (M1 example: `Pipeline<MessagesRequest>`)

```
inbound bytes (POST /cc/v1/messages)
    │
    ▼
[1] InboundCaptureStage          parse JSON → ctx.Request.Body, copy headers,
                                  snapshot RawBody for the audit log
    │
    ▼
[2] ModelRouterStage              consult model registry → resolve ctx.Target;
                                  may rename ctx.Request.Body.Model
                                  (e.g. claude-opus-4.8 → claude-opus-4.7)
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
[5] CacheControlCleanStage        strip system[*].cache_control.scope
                                  (Copilot rejects extra fields)
    │
    ▼
[6] AssistantThinkingFilterStage  drop unsigned thinking blocks from
                                  historical assistant messages
    │
    ▼
[7] ThinkingRewriteStage          per-model-family thinking shape
                                  (haiku→explicit, opus-4.7→adaptive, sonnet→passthrough)
    │
    ▼
[8] ToolsSanitizeStage            mcp__ide__executeCode drop;
                                  mcp__ide__getDiagnostics description rewrite
    │
    ▼
[9] HeadersOutboundStage          generate Copilot's 7 official headers +
                                  conditional anthropic-beta per chatEndpoint.ts:193-210;
                                  drop inbound auth/x-api-key/x-claude-code-* etc.
    │
    ▼
ctx.Target now resolved + ctx.Request.Body transformed
    │
    ▼
strategy = pipeline.Strategies.Resolve(ctx.Target)
strategy.ForwardAsync(ctx)        executes HTTP call to upstream;
                                  populates ctx.Response.EventStream
```

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

The `ModelRouterStage` is the heart of "client model id → backend route". It
consults a `ModelRegistry`:

```csharp
internal interface IModelRegistry
{
    /// <summary>
    /// Given the model id the client requested, returns the resolved backend
    /// (vendor + endpoint + actual backend model id), or null if no mapping.
    /// </summary>
    RouteTarget? Resolve(string requestedModel);
}
```

For M1, `CopilotModelRegistry` reads the live `/models` list from Copilot
(filtered to `/v1/messages`-supporting) and applies a static alias table. Every
model on the alias table OR matching by exact id resolves to a `RouteTarget`.

Future targets:
- `claude-opus-4.8` → alias to whichever opus is current on Copilot
- `claude-opus-5` → alias to opus-4.7 (or whatever's the closest available)
- `gpt-5` → would resolve to `(CopilotOpenAi, /chat/completions, gpt-5)`,
  triggering a different strategy that translates Anthropic body → OpenAI body

The ModelRouterStage never throws on miss — if no mapping, the strategy
registry returns a "cannot route" strategy that emits HTTP 400 with a clear
"model X not available; closest available: ..." message.

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

## 9. Diagnostic logging

Two log channels:

### 9.1 Audit log (always-on)

`logs/<utc>-<seq>.json` — written for every request via `BridgeRequestLogger`.
Includes inbound headers/body, upstream URL/headers/body, all SSE events
(including dropped ones), status, duration. Already implemented.

### 9.2 Diagnostic trace ([Conditional], file-based)

Direct file writer, modeled after RamDrive's
[`FsTracer`](https://github.com/hooyao/RamDrive/blob/main/src/RamDrive.Core/Diagnostics/FsTracer.cs).
Stripped from Release builds via the C# `[Conditional]` attribute — zero
runtime cost, zero AOT footprint, including no string interpolation evaluation.

```csharp
internal static class DiagTracer
{
    [Conditional("BRIDGE_DIAG")]
    public static void Log(string message)            { /* append one line */ }

    [Conditional("BRIDGE_DIAG")]
    public static void Log(object? value)             { /* JSON-serialize then append */ }
}
```

The single primary surface is `Log(string)`. Stages and the runner build their
trace lines with normal string interpolation — when `BRIDGE_DIAG` isn't
defined, the call site, the format string, and every interpolation hole
disappear at IL emission time:

```csharp
DiagTracer.Log($"req-stage end {stage.Name}  stripped {n} currentDate occurrences");
```

For dumping a complex object (a typed request body, a SSE event tree, etc.),
the `Log(object?)` overload uses reflection-based JSON serialization. It
carries `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` so the AOT
analyzer is silent — and since the call sites are conditional anyway, the
method body is unreachable in Release/AOT publish and gets trimmed.

#### Build-time gate

`csproj` defines the constant in Debug:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DefineConstants>$(DefineConstants);BRIDGE_DIAG</DefineConstants>
</PropertyGroup>
```

- Debug builds: `BRIDGE_DIAG` is defined → all `DiagTracer.Log(...)` call sites
  compile through.
- Release builds (`dotnet build -c Release`, `aot_publish.bat`): `BRIDGE_DIAG`
  is not defined → C# compiler removes call sites and their argument
  expressions entirely.

#### Runtime gate

Even in a Debug-built binary, tracing only activates when the
`BRIDGE_DIAG_FILE` environment variable is set:

- `BRIDGE_DIAG_FILE=` (empty) → log to `<exe-dir>/logs/diag.log`
- `BRIDGE_DIAG_FILE=C:\path\to\trace.log` → log to that path
- variable unset → DiagTracer's static initializer stays disabled; the
  early-return in `Log` short-circuits to a single int-load + branch

This double-gate matches FsTracer: compile-time means Release ships zero
DiagTracer code; runtime means a Debug build can be quiet by default.

#### Output format

One line per call, columns: `<elapsed-seconds-since-start>  T<thread-id>  <message>`.
Greppable, easy to diff across runs, easy to feed into a chat session for
analysis. Single shared file (truncated at process start), with explicit flush
on `ProcessExit` and `Console.CancelKeyPress`.

#### Buffering and back pressure

`Log` does not synchronously write to disk — it enqueues onto a bounded
`Channel<string>` (`System.Threading.Channels`) and a single dedicated
consumer task drains the channel and batch-flushes the file:

- Capacity: 4096 lines (~1 MB at 256 B/line).
- Producer: `TryWrite` non-blocking — if the queue is full, the line is
  dropped and a counter incremented (surfaced on shutdown as
  `# dropped N lines due to full queue`). Diagnostic data is best-effort by
  design: it must not slow the request path or block on disk.
- Consumer: `await foreach` over `ChannelReader.ReadAllAsync`; flushes the
  underlying `StreamWriter` every 64 lines and once more on completion.
- Shutdown: `Channel.Writer.TryComplete` + bounded wait on the consumer task
  (2 seconds) drains pending lines, flushes, disposes the writer.

Caller threads pay the cost of one timestamp read, one thread-id read, one
string format, and one channel `TryWrite` — all synchronous but lock-free or
minimally contended. No I/O on the call path.

#### Constraints

- `[Conditional]` only applies to `void` methods. Both `Log` overloads return
  void.
- The tracer's static state (StreamWriter, lock) lives in the assembly even in
  Release builds, but if no `Log` callers compile in, the entire static class
  is unreachable and the AOT linker drops it.
- Do not use `DiagTracer.Log` for control flow — calls vanish in Release.

## 10. File layout

```
src/CopilotBridge.Cli/
├── Pipeline/
│   ├── BridgeContext.cs                         # context + request + response containers + ResponseMode
│   ├── RouteTarget.cs                           # record + BackendVendor enum
│   ├── DiagTracer.cs                            # [Conditional]-gated file tracer (FsTracer pattern)
│   ├── PipelineRunner.cs                        # IPipelineRunner<TBody> + impl
│   ├── Pipeline.cs                              # Pipeline<TBody>
│   ├── IModelRegistry.cs                        # interface only; impls under Routing/
│   │
│   ├── Stages/                                  # IRequestStage<TBody> implementations
│   │   ├── IRequestStage.cs
│   │   ├── Anthropic/                           # for Pipeline<MessagesRequest> — IR shape stages
│   │   │   ├── ModelRouterStage.cs
│   │   │   ├── SystemSanitizeStage.cs           # strip currentDate
│   │   │   ├── MessagesSanitizeStage.cs         # tool_result merge, trailing assistant fix
│   │   │   ├── CacheControlCleanStage.cs        # strip cache_control.scope
│   │   │   ├── AssistantThinkingFilterStage.cs
│   │   │   ├── ThinkingRewriteStage.cs
│   │   │   ├── ToolsSanitizeStage.cs
│   │   │   └── HeadersOutboundStage.cs          # chatEndpoint.ts logic
│   │   └── (OpenAi/, Gemini/ added later — only if a future pipeline runs in non-IR shape)
│   │
│   ├── Response/                                # IResponseStage<TBody> implementations
│   │   ├── IResponseStage.cs
│   │   ├── DoneFilterStage.cs                   # shape-agnostic
│   │   ├── EventCaptureStage.cs                 # shape-agnostic
│   │   └── ResponseHeadersStage.cs              # shape-agnostic
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
│       ├── CopilotModelRegistry.cs              # IModelRegistry impl backed by /models + alias table
│       └── ModelAliases.cs                      # static alias table (e.g. claude-opus-4.8 → 4.7)
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
└── Hosting/
    ├── BridgePipelines.cs                       # the assembly: builds Pipeline<MessagesRequest>
    └── (existing files: KestrelServer, BridgeRequestLogger, etc.)
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

The current state (M1 step 6a) has `Endpoints/MessagesEndpoint.cs`,
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
- **Diag log for production debugging** — currently `[Conditional]` strips it
  from Release. If we ever need to diag a Release build, add a separate flag
  like `BRIDGE_DIAG_RELEASE` defined per ad-hoc publish; same machinery.
