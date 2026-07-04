# Design — Retire `RawBody` and the inbound-buffer debt

> **Companion:** builds directly on `refactor-tracing-audit-seam` (the
> `RequestAudit` seam). That change made the audit path go through the seam; this
> one removes the wire-buffer debt the seam sat on top of. Read that change's
> design for the audit-emission shape.

## Context

The inbound request body is read once per request into a pooled buffer, then used
for three things and kept alive for a fourth:

```
ReadBodyPooledAsync → (byte[] inboundBuf, int inboundLen)   // rented, grows by doubling
   view = new ReadOnlyMemory<byte>(inboundBuf, 0, inboundLen)
   ① deserialize      JsonSerializer.Deserialize(view.Span)          // SYNC, endpoint
   ② audit            audit.RecordInbound(..., view)                 // SYNC, endpoint (copies iff tracing)
   ③ RawBody          bridgeCtx.Request = new { RawBody = view, ... } // crosses await into the pipeline
   ④ return           finally: ArrayPool.Shared.Return(inboundBuf)   // after RunAsync completes
```

①② are synchronous and complete before `await runner.RunAsync`. ③ is the only
reason the buffer must outlive the sync section — and the pipeline's sole use of
③ is `PipelineRunner.cs:29`, which reads `RawBody.Length` for a debug line. So the
buffer is pinned across the entire request to serve a length nobody meaningfully
consumes.

### Why `RawBody` is wrong at the type level (not just unused)

`BridgeRequest<TBody>` models *the request the pipeline operates on*. Its
`Body: TBody` is the IR; a stage mutates it. Putting `RawBody: ReadOnlyMemory<byte>`
next to it asserts the type simultaneously holds the **wire** representation and
the **IR** representation, and invites a stage to read the wire bytes. That
contradicts the layering (`Translation/` is pure over DTOs; stages see IR, not
wire). The three legitimate needs for raw bytes all live at the **boundary**, none
in the pipeline:

| need | layer | home |
|---|---|---|
| deserialize → IR | endpoint | local, sync, discard after |
| audit archive (`inbound-req.json`) | observability | the `RecordInbound` copy (already so) |
| a stage reading raw JSON | pipeline | **forbidden** — would bypass the IR |

And the length: the one reader wants "request size," but **every** pipeline mutates
`Body` (even cc→opus sanitizes), so the inbound wire length is stale from the first
stage. It is an endpoint-boundary observation, not pipeline state — its correct
home is the endpoint's own log line, where `inboundLen` is already in hand.

## Decision 1: delete `RawBody`; log the size at the endpoint

`BridgeRequest` loses `RawBody` entirely (no `RawBodyLength` replacement — a length
carried into the pipeline is the same mislayering one size smaller, and goes stale
anyway). `BridgeRequest` is left with `Method`, `Path`, `Body`, `Headers` — all
things the pipeline genuinely consumes.

`PipelineRunner`'s start line drops the `body-bytes` hole:

```csharp
// before: "... path={Path}  body-bytes={BodyBytes}", ..., _ctx.Request.RawBody.Length
// after:  "... path={Path}", ..., _ctx.Request.Path
```

The inbound size moves to the endpoint `enter` line, where it is a boundary fact:

```csharp
// endpoint, right after the body read (inboundLen in scope):
endpointLog.LogDebug("endpoint {Path}: enter  remote={Remote}  body-bytes={Bytes}",
    httpCtx.Request.Path, httpCtx.Connection.RemoteIpAddress, inboundLen);
```

This is strictly more correct: the size is logged by the layer that observes it,
at the moment it is true, next to the request it describes — and interleaved
concurrent requests keep their own size with their own `enter` line.

## Decision 2: read the body into a synchronous-scope `using`

With ③ gone, the buffer no longer needs to cross `await`. Collapse the read into a
disposable owner used entirely inside the endpoint's sync section:

```csharp
// one shared helper (replaces three copies of ReadBodyPooledAsync):
internal static async Task<PooledBody> ReadPooledAsync(Stream body, CancellationToken ct)
// PooledBody : IDisposable — wraps an IMemoryOwner<byte> (or a rented byte[]) + the length;
//   exposes ReadOnlyMemory<byte> Memory (the length-trimmed view) and int Length;
//   Dispose() returns the buffer to the pool exactly once.

using var inbound = await InboundBody.ReadPooledAsync(httpCtx.Request.Body, ct);
endpointLog.LogDebug("endpoint … enter … body-bytes={Bytes}", …, inbound.Length);

MessagesRequest? clientBody = JsonSerializer.Deserialize(inbound.Memory.Span, JsonContext.Default.MessagesRequest);
audit.RecordInbound(seq, traceId, method, path, headers, inbound.Memory);
// … build bridgeCtx.Request WITHOUT RawBody …
await runner.RunAsync(pipeline);
// `using` disposes `inbound` at end of scope — but we can dispose EARLIER (see below)
```

**Ownership is now the type's job.** `using` replaces the hand-written
`finally { ArrayPool.Return }` and the two other scattered return sites. The
capacity-vs-length invariant is encapsulated: callers see `inbound.Memory`
(already trimmed to `Length`) and never touch the raw rented array.

### The growth question (why not bare `MemoryPool.Rent`)

`MemoryPool<byte>.Shared.Rent(minSize)` gives an `IMemoryOwner<byte>` but has **no
incremental-grow API** — you must know the size up front, and `Rent` may return a
buffer *larger* than asked, so `owner.Memory.Length` is capacity, not content
length. The current loop grows by doubling because the request body length is
unknown ahead of the read (chunked transfer, no reliable `Content-Length`). Two
viable shapes, both keeping pooling and encapsulating the invariant:

- **A (recommended): a small `PooledBody` type wrapping `ArrayPool<byte>` + a
  doubling grow**, exactly the current algorithm but *owned by the type* — `Dispose`
  returns the (possibly re-grown) final array; `Memory`/`Length` expose the trimmed
  view. The growth logic lives in ONE place instead of three, and ownership is a
  `using`. No behavior change, no new dependency.
- **B: `Content-Length`-hinted single `MemoryPool.Rent`** when the header is present
  and trustworthy, falling back to A otherwise. More moving parts for a marginal win
  (one fewer regrow on the happy path); rejected as over-engineering for this change.

Choose **A**: it is the minimal honest refactor — same algorithm, correct
ownership, one copy, zero dependencies, no LOH risk (still pooled).

### Dispose timing — keep it correct

The body is only needed through `audit.RecordInbound` and `Deserialize`, both
before `RunAsync`. So `inbound` *can* be disposed before the pipeline runs. But two
constraints:

- **`audit.RecordInbound` must have copied by dispose time.** On-trace it does
  `body.ToArray()` synchronously inside the call (a copy the sink owns); off-trace
  it is a no-op. Either way it does not retain `inbound.Memory` past the call — so
  disposing after `RecordInbound` returns is safe.
- **`Deserialize` consumes the span synchronously.** Also done before dispose.

Simplest correct shape: a `using` block (or `using var` at method scope) that spans
the read → deserialize → `RecordInbound`, disposed before or at `RunAsync`. The
change keeps `using var` at handler scope for minimal churn (disposed at handler
exit); an explicit narrower block is an option if we want the buffer freed before
the upstream call. Either preserves correctness because nothing past `RecordInbound`
reads `inbound`.

## Decision 3: delete the dead `bodyPooled` plumbing

`bodyPooled` was the hook for handing a rented buffer to the sink worker to return
after writing. It is unreachable (all callers pass `false`) and its intent — a
buffer shared between the request thread and the async sink worker — is the
double-consumer hazard the copy-based design avoids. Remove:

- the `bodyPooled` parameter on the four `BridgeIoLoggerExtensions` methods;
- the `bool bodyPooled: false` literals in `RequestAudit`'s four `Record*` calls;
- `BridgeIoPayload.BodyPooled`, `Release()`, and the sink worker's
  `finally { payload.Release() }`.

The sink then always treats `Body` as a payload-owned array (which it already is),
and GC reclaims it — no behavior change, one less unreachable branch.

> **Ordering:** Decision 3 is independent of 1/2 (it touches the sink side, not the
> read side) and could ship alone, but it belongs here: it is the last tendril of
> the same "hand a pooled buffer across async" idea that `RawBody` embodied. Doing
> it together leaves no half-removed concept.

## Alternatives considered

| Option | Why not |
|---|---|
| Keep `RawBody`, just fix the pool loop | Leaves the mislayered field and its stale doc; the loop is awkward *because* the buffer must outlive sync — treats the symptom. |
| Replace with `RawBodyLength: int` on `BridgeRequest` | Same mislayering, smaller; the length is stale after the first stage and has no pipeline consumer. Rejected in review. |
| Bare `new byte[]` (drop pooling) | 4 MB+ CC bodies land on the LOH every request (gen2 churn). Pooling via `IMemoryOwner` keeps them off it. |
| `Microsoft.IO.RecyclableMemoryStream` | Solves growth+dispose cleanly but adds a NuGet to a single-file AOT exe; `IMemoryOwner`+doubling does the same with zero deps. |

## Risks / trade-offs

- **Audit fidelity must stay byte-identical.** `RecordInbound` still receives the
  same bytes (the trimmed `inbound.Memory`); on-trace it copies exactly as before.
  Guarded by the existing `RequestAudit`/audit-endpoint tests (`inbound-req` body
  bytes) — they must stay green.
- **Dispose-before-use bug risk.** The one real hazard: disposing `inbound` while
  something still reads it. Mitigated by construction (nothing past `RecordInbound`
  touches it) and a test that drives the endpoint with tracing on and asserts the
  `inbound-req` bytes equal the request — a premature dispose corrupts them.
- **count_tokens divergence.** It keeps its own read (its buffer must reach
  `PostCountTokensAsync`, and it never used `RawBody`); only its dead `bodyPooled`
  args are removed. Called out so a future reader doesn't "unify" it and break the
  upstream POST.
- **AOT.** No reflection, no package. Publish check per `CLAUDE.md`.
