# Tasks — Retire `RawBody` and the inbound-buffer debt

> Pure internal refactor: no audit-artifact bytes, trace-file names, or wire bytes
> change. The existing `RequestAudit`/audit-endpoint/capture-contract tests are the
> fidelity guard — they must stay green throughout. Land order: build the shared
> read helper (§1), rewire the two pipeline endpoints (§2), delete `RawBody` +
> sink the log (§3), delete `bodyPooled` plumbing (§4), then verify (§6).

## 1. Shared pooled-read helper with owned lifetime

- [x] 1.1 Add an `InboundBody.ReadPooledAsync(Stream, CancellationToken)` helper
  returning a disposable `PooledBody` (`IDisposable`) that wraps a rented
  `ArrayPool<byte>` buffer + the real length, exposes `ReadOnlyMemory<byte> Memory`
  (trimmed to length) and `int Length`, grows by doubling (the current algorithm),
  and returns the buffer to the pool exactly once on `Dispose` (idempotent). The
  capacity-vs-length invariant is encapsulated — callers only see `Memory`/`Length`.
- [x] 1.2 Unit-test the helper directly: (a) a body smaller than the initial rent
  round-trips byte-exact via `Memory`; (b) a body larger than the initial rent
  (forces ≥1 doubling) round-trips byte-exact; (c) `Length` is the content length,
  not the rented capacity; (d) double-`Dispose` does not double-return (no throw,
  no pool corruption — assert via a second rent getting a clean buffer or just that
  it does not throw). Mutation-check (b): break the grow copy → RED.

## 2. Rewire the two pipeline endpoints to the `using` read

- [x] 2.1 `ClaudeCodeMessagesEndpoint`: replace `ReadBodyPooledAsync` + the manual
  `finally { ArrayPool.Return }` with `using var inbound = await InboundBody.ReadPooledAsync(...)`.
  Deserialize from `inbound.Memory.Span`; `audit.RecordInbound(..., inbound.Memory)`.
  Remove the endpoint's private `ReadBodyPooledAsync` and the `ArrayPool.Return`
  line. Confirm nothing past `RecordInbound` reads `inbound`.
- [x] 2.2 `CodexResponsesEndpoint`: same rewrite; remove its private
  `ReadBodyPooledAsync` and `ArrayPool.Return`.
- [x] 2.3 Add the inbound size to each endpoint's `enter` debug line
  (`body-bytes={inbound.Length}`), so the size is observed at the boundary.

## 3. Delete `RawBody` and drop the pipeline length read

- [x] 3.1 `BridgeContext.cs`: delete `BridgeRequest.RawBody` and its (stale)
  doc-comment. `BridgeRequest` keeps `Method`/`Path`/`Body`/`Headers`.
- [x] 3.2 Remove `RawBody = inboundBytesView` from both endpoint `BridgeRequest`
  constructions (§2 already dropped the standalone view; confirm no residual use).
- [x] 3.3 `PipelineRunner.cs`: drop `body-bytes={BodyBytes}` /
  `_ctx.Request.RawBody.Length` from the pipeline-start debug line.
- [x] 3.4 `rg "RawBody"` over `src/` — zero hits outside unrelated same-named
  symbols (the Playground client tuple field, the `RawUpstreamResponseBody` capture
  test) which are distinct and stay.

## 4. Delete the dead `bodyPooled` plumbing

- [x] 4.1 `BridgeIoLoggerExtensions.cs`: remove the `bool bodyPooled` parameter from
  `LogInboundRequest` / `LogInboundResponse` / `LogUpstreamRequest` /
  `LogUpstreamResponse` and stop setting `BridgeIoPayload.BodyPooled`.
- [x] 4.2 `RequestAudit.cs`: drop the `bodyPooled: false` argument from the four
  `Record*` delegations.
- [x] 4.3 `BridgeIoPayload.cs`: delete the `BodyPooled` field, `Release()`, and the
  `ArrayPool` using/doc that only `Release()` needed.
- [x] 4.4 `BridgeIoSink.cs`: remove the worker's `finally { payload.Release() }`
  (the payload's `Body` is a plain owned array; GC reclaims it).
- [x] 4.5 `ClaudeCodeCountTokensEndpoint.cs` + `ClaudeCodeModelsEndpoint.cs`: drop
  the now-removed `bodyPooled` arguments from their audit calls (count_tokens keeps
  its own `inboundBuf` read — it must reach `PostCountTokensAsync` and never used
  `RawBody`; do NOT unify it).

## 5. Docs

- [x] 5.1 `docs/pipeline-design.md`: remove `RawBody` from the `BridgeRequest`
  sketch and the "keep the pooled buffer alive for the pipeline's `RawBody` view"
  sentence in §9 buffer-ownership; note the buffer is now released in the endpoint's
  sync section.
- [x] 5.2 `docs/observability-design.md`: update the buffer-ownership paragraph
  (§11) that references the `RawBody` view; the audit copy is unchanged.

## 6. Verification

- [x] 6.1 `dotnet test --filter "Category!=Integration"` green — the existing
  `RequestAudit*`/`UpstreamResponseAuditEndpointTests`/`UpstreamResponseCaptureContractTests`
  assert `inbound-req` byte fidelity; they must stay green (proof the buffer-lifetime
  change preserved audit output). Plus the new §1.2 helper tests. Build clean (0
  warnings).
- [x] 6.2 Add a dispose-safety regression: drive `ClaudeCodeMessagesEndpoint` with
  tracing ON through the real read + seam, assert the logged `inbound-req` bytes
  equal the request bytes byte-for-byte. Mutation-check: dispose `inbound` before
  `RecordInbound` → corrupted/empty `inbound-req` → RED.
- [x] 6.3 AOT publish per the `CLAUDE.md` PowerShell block; no new IL/trim warnings;
  published `.exe` mtime advanced; eyeball size unchanged (no new dependency).
- [x] 6.4 Grep-confirm the concept is gone: `rg "bodyPooled|BodyPooled|RawBody"` over
  `src/` returns only the intentionally-distinct `RawUpstreamResponse*` names.
