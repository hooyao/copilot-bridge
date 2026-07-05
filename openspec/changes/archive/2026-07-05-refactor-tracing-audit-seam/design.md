# Design — Refactor the tracing / upstream-audit plumbing behind one seam

> **Companion doc:** [`docs/observability-design.md`](../../../docs/observability-design.md)
> is the diagram-heavy subsystem design (four-artifact trace, trace-id
> correlation, raw capture, the `RequestAudit` seam). This file is the
> change-scoped rationale; that doc is the full picture. Keep them consistent.

## Context

The bridge emits four per-request audit JSON files (`inbound-req`,
`inbound-resp`, `upstream-req`, `upstream-resp`) sharing a trace id, when
`Tracing.Enabled=true`. The emission path is a Serilog sink (`BridgeIoSink`) that
picks EventIds 1001–1004 out of the log stream. When tracing is off the sink is
not registered, **and** the full logger excludes bridge-IO events from console
and file too (`SerilogBootstrapper.cs:98-115`) — so every audit payload built
off-trace is unconditionally dropped.

The `upstream-req` audit must record **the exact bytes POSTed to Copilot**. Two
backends produce those bytes differently:

- **Passthrough** (`CopilotMessagesPassthroughStrategy`, claude-* → `/v1/messages`):
  the IR *is* the wire body. The strategy serializes `ctx.Request.Body`
  (`:50`) and POSTs it; the local `body` is not stored.
- **Translating** (`CopilotResponsesStrategy`, gpt-* → `/responses`): T2 builds a
  differently-shaped Responses body and stashes it on `Response.UpstreamWireBody`
  (`:61`) so the endpoint audits the real thing, not the IR.

This is correct but the "are we tracing?" decision is scattered across six files
in five different shapes, and the passthrough endpoint re-derives the upstream
body by **re-serializing the IR** — unconditionally, even off-trace.

### The three defects, precisely

**P1 — audit-only work on the off-trace hot path.** The passthrough endpoint's
`upstream-req` body is computed as:

```csharp
// ClaudeCodeMessagesEndpoint.cs:219-220 — inside try, NO if(tracingEnabled) guard
var upBody = bridgeCtx.Response.UpstreamWireBody
    ?? JsonSerializer.SerializeToUtf8Bytes(bridgeCtx.Request.Body, JsonContext.Default.MessagesRequest);
```

On passthrough `UpstreamWireBody` is null, so the right side runs on **every**
request. For the corpus's largest real bodies (~2.78 MB) a `MessagesRequest`
serialize is ~9–15 ms and ~3.4–5 MB of allocation. It is thrown away when tracing
is off (the consuming `LogUpstreamRequest` in `finally` has nowhere to send it).
Two sibling wastes share the pattern: the unconditional `inboundAuditBody =
inboundBytesView.ToArray()` (`:83`) copies the whole inbound body, and all four
`LogInbound*/LogUpstream*` calls build a `BridgeIoPayload` that the logger drops
off-trace.

**P2 — `UpstreamWireBody == null` is overloaded.** It conflates three unrelated
facts: *which backend ran* (the endpoints' actual use — null ⇒ passthrough),
*whether translation happened*, and (once P1 stashes passthrough bytes too)
*whether tracing is on*. Deciding "is this gpt-routed?" from a nullable audit
field is backwards: the authoritative signal is `bridgeCtx.Target.Vendor`, set by
`ModelRouterStage` and already read two lines up for the summary
(`:205-206`). The Codex strategy compounds it by setting `UpstreamWireBody`
**unconditionally** (`:61`) while gating the buffered raw capture right below
(`:103`) — two rules for two fields that should share one.

**P3 — five gating shapes, one of them "none".**

| Mechanism | Site(s) |
| --- | --- |
| DI factory returns `null` sink | `BridgeServiceCollectionExtensions.cs:129-135` |
| ctor `bool` field | `CopilotMessagesPassthroughStrategy.cs:26,37`; `CopilotResponsesStrategy.cs:30,43` |
| endpoint local `var tracingEnabled` | `ClaudeCodeMessagesEndpoint.cs:55`; `CodexResponsesEndpoint.cs:67` |
| `?:` ternary (allocate-or-null) | endpoints `capturedEvents`; strategies `capture` |
| `if (_tracingEnabled)` guard | passthrough `:111`, responses `:103` |
| **no guard** | `UpstreamWireBody` at responses `:61`; the endpoint serialize at `:219` |

The last row is the bug surface: a capture site with no guard silently ignores
the flag. There is no single seam a reviewer can point at to see "here is how a
request gets traced", so P1 was invisible.

## Decision 1: a `RequestAudit` per-request seam owns the flag and the emissions

Introduce one scoped service, injected wherever tracing is consulted. It holds
the `Enabled` flag and wraps the four emissions and the two trace-only buffer
factories. Every method is a no-op when disabled.

```csharp
internal sealed class RequestAudit
{
    private readonly ILogger _io;              // the ILogger<MessagesRequest> the endpoint already resolves
    public bool Enabled { get; }               // = TracingOptions.Enabled, read once

    public RequestAudit(IOptions<TracingOptions> tracing, ILogger<RequestAudit> io) { ... }

    // Trace-only buffers — return null when off, so callers keep `?.` idioms.
    public RawResponseCapture? NewCapture()        => Enabled ? new() : null;
    public List<CapturedSseEvent>? NewEventList()  => Enabled ? new() : null;

    // Emissions — no-op when off. On-trace they call the existing
    // BridgeIoLoggerExtensions, so the four artifacts are byte-identical.
    public void RecordInbound(int seq, string traceId, ... ) { if (!Enabled) return; _io.LogInboundRequest(...); }
    public void RecordUpstreamRequest(...)  { if (!Enabled) return; _io.LogUpstreamRequest(...); }
    public void RecordUpstreamResponse(...) { if (!Enabled) return; _io.LogUpstreamResponse(...); }
    public void RecordInboundResponse(...)  { if (!Enabled) return; _io.LogInboundResponse(...); }
}
```

Why a service and not a static helper: it is **request-scoped state** (the
`Enabled` snapshot, and it is the natural owner if capture slots ever move off
`BridgeResponse`). It composes with the existing scoped `BridgeContext` — the
container constructs it per request; strategies and endpoints inject the same
instance. No DI-factory-returns-null trick: the seam is always present; the flag
lives inside it.

**What the seam removes:** the two strategy `_tracingEnabled` fields, the three
endpoint `tracingEnabled` locals, and the unguarded `?? serialize` fallback. The
DI-null sink stays (it is the *sink*'s concern, one layer lower, and unrelated to
per-request gating) but is no longer the only place the flag is read on the
request path.

### Zero-overhead-when-off, made observable

The seam's value is that the invariant is now testable at one type. The
guarantee: with `Enabled=false`, no `Record*` builds a `BridgeIoPayload`, no
buffer factory allocates, and — critically — **the endpoints no longer copy the
inbound body or re-serialize the IR** when off-trace, because those computations
move behind `Record*` parameters that aren't evaluated when the method early-
returns. (Where an argument is expensive to compute at the call site — e.g. the
`.ToArray()` — the call site itself guards on `audit.Enabled`, or the method
takes the `ReadOnlyMemory<byte>` view and copies only when enabled.)

## Decision 2: both strategies stash the wire bytes, gated on `audit.Enabled`

Passthrough gains the stash the translating path already has:

```csharp
// CopilotMessagesPassthroughStrategy.ForwardAsync, right after the existing serialize (:50)
var body = JsonSerializer.SerializeToUtf8Bytes(ctx.Request.Body, JsonContext.Default.MessagesRequest);
if (_audit.Enabled) ctx.Response.UpstreamWireBody = body;   // NEW — reuse the bytes we already POST
```

Codex's existing unconditional stash becomes gated, matching its buffered sibling:

```csharp
// CopilotResponsesStrategy.cs:61
if (_audit.Enabled) ctx.Response.UpstreamWireBody = body;   // was: unconditional
```

Then the endpoints simply read the field; the `?? serialize` fallback is deleted:

```csharp
// both endpoints — replaces the current `?? SerializeToUtf8Bytes(...)`
if (audit.Enabled)
{
    var upBody = bridgeCtx.Response.UpstreamWireBody ?? Array.Empty<byte>();
    audit.RecordUpstreamRequest(seq, traceId, "POST", upstreamUrl, upstreamHeaders, upBody, upBody.Length, false);
}
```

This is strictly better than re-serializing for the audit: `upstream-req` becomes
the **exact** byte array the `CopilotClient` received, not a second serialization
that could diverge (field ordering, a future non-deterministic serializer option,
a body mutated between POST and audit). The one behavioral question — *could
`UpstreamWireBody` be null on-trace after this change?* — is answered no: both
strategies set it whenever `audit.Enabled`, and the endpoint only reads it when
`audit.Enabled`. If a future third strategy forgets, the fidelity test (below)
fails loudly rather than silently logging an empty `upstream-req`.

### Invariant: every artifact is the wire on its boundary, never the IR

The IR (`MessagesRequest`, Anthropic-shaped) is an *internal hub*; it must never
appear in an audit file. Each of the four artifacts records the real bytes that
crossed its boundary, in the native protocol of whichever side owns that boundary
— for **both** client types:

| Artifact | `/cc` (Claude Code) | `/codex` (Codex) | Recorded from |
|---|---|---|---|
| `inbound-req` | Anthropic, as CC sent it | Responses, as Codex sent it | raw inbound bytes, **before T1** builds the IR |
| `upstream-req` | Anthropic we POSTed | Responses (T2 output) we POSTed | `UpstreamWireBody` = the array POSTed |
| `upstream-resp` | Anthropic, as Copilot sent | Responses, as Copilot sent | `RawUpstreamRespBytesOrNull()`, **before T3/T4** |
| `inbound-resp` | Anthropic we returned | Responses (T4 output) we returned | outbound bytes + dropped events |

Two consequences the refactor must preserve, both asserted by tests:

1. **`inbound-req` is the untranslated client request.** It is captured in the
   endpoint from `httpCtx.Request.Body` *before* the inbound adapter (T1) runs, so
   it is Codex's original Responses request, not the IR. This is unchanged by the
   refactor (the read site does not move); the zero-overhead work only gates the
   *copy for audit*, not the fact that the bytes are the raw inbound ones.
2. **`upstream-req` on Codex is T2's Responses output, and is not the IR.** A
   Codex request is translated twice (T1: Codex-Responses → IR, T2: IR →
   Copilot-Responses). Both `inbound-req` and `upstream-req` are Responses-shaped;
   when T1∘T2 reshapes the body they differ, and for a trivial request where the
   round-trip is an identity they may coincide — byte-inequality is
   request-dependent and NOT an invariant. The invariant the fidelity test asserts
   is that `upstream-req == the exact bytes the stub client received` (T2 output)
   and that **neither** artifact is the IR (the IR's required `max_tokens` is absent
   from both Responses bodies) — guarding against a regression that accidentally
   audits the IR or the inbound bytes as the upstream request.

## Decision 3: routing identity comes from `Target.Vendor`, not the null-overload

`UpstreamWireBody` now means exactly one thing: **"the captured upstream wire
bytes; non-null iff tracing is on and a strategy ran."** Nothing reads it to
decide *which* backend ran — the endpoints already have `bridgeCtx.Target.Vendor`
for that (read for the summary at `:205`). The field doc in `BridgeContext.cs`
is corrected from "Null on passthrough paths" to the tracing-gated meaning.

This is the ordering constraint the handover flagged: P1 and P2 must land
together. Stashing passthrough bytes (P1) changes when `UpstreamWireBody` is
null, which would break any code inferring backend identity from it (P2) — so the
inference is removed in the same change, not after.

## What is deliberately NOT moved

The capture **result** slots — `RawUpstreamResponseBody` (buffered) and
`RawUpstreamResponseCapture` (streaming) — and `RawUpstreamRespBytesOrNull()`
with its `ToArray()`-finalizes-once side effect stay on `BridgeResponse`. They
are the strategy→endpoint hand-off channel, pinned by
`UpstreamResponseCaptureContractTests` and `UpstreamResponseAuditEndpointTests`,
and moving them would churn the well-tested streaming-finalize ordering for no
gain. This change unifies their **gating** (all behind `audit.Enabled`), not
their location. `UpstreamStreamFault` likewise stays — it is a fault channel, not
an audit toggle.

## Alternatives considered

| Option | Why not |
| --- | --- |
| **Leave P1, just add `if (tracingEnabled)` around the endpoint serialize** | Fixes the perf symptom but not the cause — the flag is still read six ways and the next capture site can still forget it. Doesn't touch P2/P3. The handover explicitly asks for one seam. |
| **Static `AuditGate.IsEnabled(options)` helper instead of a scoped service** | No per-request state to own today, but a static can't carry the future capture slots and re-introduces "read the flag at each call site". A scoped service is the same DI shape as `BridgeContext` the code already uses. |
| **Move all four capture slots into `RequestAudit` now** | Larger blast radius across the two contract test files for no correctness gain this change needs; the slots' streaming-finalize ordering is subtle and separately tested. Defer; the seam makes a later move mechanical. |
| **Decide the audit body from `Target.Vendor` (serialize IR for Anthropic, use stashed bytes for Responses)** | Keeps a serialize path alive for the passthrough audit — the exact waste P1 removes — and re-couples the audit body to routing identity. Stashing on both paths is simpler and makes `upstream-req` the real POSTed bytes. |

## Risks / trade-offs

- **On-trace fidelity regression.** The whole point is that on-trace output is
  unchanged. Guarded by the fidelity test (four artifacts byte-identical to a
  pre-refactor golden) and the existing capture-contract suite, which already
  assert `upstream-resp == raw Copilot bytes` on both endpoints and both modes.
- **A future strategy forgets to stash `UpstreamWireBody`.** Then on-trace
  `upstream-req` would be empty. The fidelity test asserts `upstream-req ==` the
  bytes the stub `CopilotClient` received, so a forgotten stash is RED, not a
  silent empty file.
- **Argument-evaluation footgun.** A no-op `Record*` still evaluates its
  arguments at the call site. The design keeps expensive arguments (the
  `.ToArray()` copy) behind an explicit `if (audit.Enabled)` at the call site OR
  passes the `ReadOnlyMemory<byte>` view and copies inside the guarded method —
  never an eager `.ToArray()` in the argument list. The zero-overhead test
  enforces this by asserting no allocation/no event off-trace.
- **AOT.** No reflection; `RequestAudit` is a plain scoped service, serialization
  stays on `JsonContext`. Publish sanity-check per `CLAUDE.md` still required.

## Migration

None user-facing. Internal plumbing only: no config keys, wire bytes, trace-file
names, or audit JSON shape change. Off-trace behavior is *more* efficient;
on-trace behavior is byte-identical.
