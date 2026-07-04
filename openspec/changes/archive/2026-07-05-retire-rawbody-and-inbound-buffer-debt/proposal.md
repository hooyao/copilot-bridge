# Retire `RawBody` and the inbound-buffer debt

## Why

`BridgeRequest<TBody>.RawBody` (`ReadOnlyMemory<byte>`) is a wire-layer concept
mislayered onto pipeline-internal state, and it drags three coupled defects behind
it. All four share one root cause: **the inbound body is forced to stay alive
across `await` only to feed `RawBody` — a field the pipeline never actually
reads.**

- **`RawBody` is a mislayered, stale-documented field.** It lives on
  `BridgeRequest`, the type that models *"the request as the pipeline sees it"*,
  and its type (`ReadOnlyMemory<byte>`) promises *"any stage can read the original
  client bytes."* That promise contradicts the hub-IR architecture
  (`CLAUDE.md`: *"Translation/ is pure functions over DTOs"* — stages operate on
  the IR, not wire bytes), so the field is a standing invitation to bypass the IR.
  Its doc-comment claims *"preserved for the audit log"* — **false**: the audit
  (`RequestAudit.RecordInbound`) reads the endpoint's local view, never `RawBody`.
  Its only real reader is one `LogDebug` in `PipelineRunner` that reads
  `RawBody.Length`.
- **Carrying the inbound length into the pipeline is meaningless.** The one reader
  wants "how big was the request." But every pipeline runs body-mutating stages —
  even the cc→opus hot path sanitizes/normalizes `Body` — so the *inbound* wire
  length goes stale the instant the pipeline starts. A value that is wrong by the
  time anyone downstream could use it does not belong in pipeline state; the
  inbound size is an **endpoint-boundary observation** and belongs there.
- **`ReadBodyPooledAsync` is awkward and duplicated.** It returns a
  `(byte[] Buffer, int Length)` tuple whose `Buffer.Length` (rented capacity)
  differs from the real `Length` — leaking a fragile invariant to every caller. It
  rents but does not return; ownership is handed across the method boundary to the
  caller's `finally`, with return sites scattered (grow, catch, caller-finally). It
  hand-rolls a doubling growth loop that `MemoryStream`/pooled writers already
  implement. And it is copy-pasted across three endpoints.
- **`bodyPooled` is dead plumbing.** The parameter on `BridgeIoLoggerExtensions`,
  the `BridgeIoPayload.BodyPooled` field, `Release()`, and the sink worker's
  `payload.Release()` exist to let the sink accept a rented buffer and return it
  after writing — but all four `Record*` callers pass `false`, so the pool-return
  path is unreachable. Its design intent (hand a rented buffer across async to the
  sink worker) is the very double-consumer hazard the current "copy, non-pooled"
  design avoids.

None are user-facing bugs; the audit output and wire behavior are unchanged. The
point is to delete a mislayered concept, collapse a fragile duplicated read into
one correct helper, and remove unreachable plumbing — so the request context type
becomes honest (only what the pipeline consumes) and the pooled buffer's lifetime
shrinks to where it is actually used.

## What Changes

- **Delete `BridgeRequest.RawBody`.** Remove the field, its stale doc-comment, and
  the two endpoint assignments (`RawBody = inboundBytesView`).
- **Sink the `body-bytes` debug line to the endpoint.** `PipelineRunner`'s start
  line drops `body-bytes={RawBody.Length}`; the inbound size is logged at the
  endpoint `enter` line where `inboundLen` is already in scope — the layer that
  actually observes it. `count_tokens` and `/models` are unaffected (they never
  populate `RawBody`).
- **Collapse the inbound read into a synchronous-scope `using`.** Replace the
  hand-rolled `ReadBodyPooledAsync` growth loop with one shared helper that returns
  a disposable owner (`IMemoryOwner<byte>`); the endpoint reads the body, uses it
  synchronously (deserialize + `audit.RecordInbound`), and disposes it — the pooled
  buffer never crosses `await`. Ownership is expressed by `using`, not a scattered
  `finally`.
- **Delete the dead `bodyPooled` plumbing.** Drop the parameter from the four
  `BridgeIoLoggerExtensions` methods (and the internal `RequestAudit` calls), the
  `BridgeIoPayload.BodyPooled` field, `Release()`, and the sink worker's
  `payload.Release()` call.
- **Refresh the two `docs/` explanations** that describe keeping the pooled buffer
  alive for the `RawBody` view (`pipeline-design.md`, `observability-design.md`).

## Non-goals

- No new dependency. (`Microsoft.IO.RecyclableMemoryStream` was considered and
  rejected — `IMemoryOwner<byte>` from the framework's pool solves it with zero
  additions to a single-file AOT exe.)
- No change to audit artifact bytes, trace-file naming, or any request/response
  wire bytes — this is pure internal structure.
- Not touching `count_tokens`'s own buffer beyond the `bodyPooled` argument: its
  `inboundBuf` genuinely must survive to `PostCountTokensAsync`, so its read stays
  as-is (it never used `RawBody`); only its dead `bodyPooled: false` arguments go.
- Not introducing an LOH regression: bodies stay pooled/`IMemoryOwner`-backed, not
  bare `new byte[]`.

## Impact

- Affected specs: `pipeline-request-isolation` (the request-context shape — `RawBody`
  removed as pipeline-visible state) and `observability` (reaffirm audit
  fidelity/zero-overhead are preserved by the buffer-lifetime change).
- Affected code: `Pipeline/BridgeContext.cs` (drop `RawBody`), `PipelineRunner.cs`
  (drop the length read), `ClaudeCodeMessagesEndpoint.cs` + `CodexResponsesEndpoint.cs`
  (`using`-scoped read, drop `RawBody =`, endpoint-level size log), a shared inbound
  read helper, `BridgeIoLoggerExtensions.cs` + `BridgeIoPayload.cs` + `BridgeIoSink.cs`
  (`bodyPooled` removal), `RequestAudit.cs` (drop `bodyPooled: false`), `ClaudeCodeCountTokensEndpoint.cs`
  (drop dead `bodyPooled` args only), docs.
- AOT: no reflection, no new package; publish sanity-check per `CLAUDE.md` still
  required.
