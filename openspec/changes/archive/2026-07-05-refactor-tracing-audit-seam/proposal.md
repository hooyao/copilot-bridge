# Refactor the tracing / upstream-audit plumbing behind one seam

## Why

The per-request audit/tracing capture is functionally correct but structurally
scattered. The single concept "are we tracing this request?" is expressed five
different ways across six files, and audit-only work runs on the hot request path
even when tracing is OFF. Three coupled problems (full map: `docs/scratch/handover-tracing-audit-refactor.md`,
verified against HEAD):

- **P1 — audit-only work on the off-trace hot path.** On a CC→Copilot-Anthropic
  passthrough, the request body is serialized once by the strategy (to POST
  upstream) and **again** by the endpoint for the `upstream-req` audit — the
  second `SerializeToUtf8Bytes` is unguarded, so it runs even when tracing is off
  and its output is discarded (`ClaudeCodeMessagesEndpoint.cs:219-220`). It is not
  the only such waste: the endpoint also copies the whole inbound body
  (`inboundBytesView.ToArray()`, `:83`) and emits four `BridgeIoPayload`s
  unconditionally, all of which the logging pipeline drops when tracing is off
  (the sink is unregistered *and* bridge-IO events are excluded from console/file
  — `SerilogBootstrapper.cs:98-115`). Real CC requests routinely exceed 4 MB, so
  this is a multi-megabyte per-request tax paid in production where tracing is
  off.
- **P2 — overloaded null semantics.** `BridgeResponse.UpstreamWireBody == null`
  currently conflates "which backend ran" (null ⇒ passthrough), "was translation
  done", and — once P1 is fixed — "is tracing on". The `/cc` and `/codex`
  endpoints infer the routing decision from this nullable audit field
  (`ClaudeCodeMessagesEndpoint.cs:219`, `CodexResponsesEndpoint.cs:162`), when the
  authoritative signal is `bridgeCtx.Target.Vendor`. The Codex strategy also sets
  the field **unconditionally** (`CopilotResponsesStrategy.cs:61`), inconsistent
  with the tracing-gated buffered capture right below it (`:103`).
- **P3 — no single home for the flag.** Tracing is gated via a DI factory
  returning `null`, a ctor `bool` field (×2 strategies), an endpoint local (×3
  endpoints), `?:` ternaries, `if` guards, and — the bug surface — **no guard at
  all** (the two P1 sites). There is no one place that says "here is how a request
  gets traced", so a new capture site can silently ignore the flag. That is
  exactly how P1 happened.

None are user-facing bugs today. Fixing them kills wasted work on the hot path,
makes the audit path legible, and gives tracing one obvious home so the next
capture addition can't drift.

## What Changes

- **Introduce a per-request `RequestAudit` scoped seam** that owns the one
  `Enabled` flag and the four audit emissions (`RecordInbound`,
  `RecordUpstreamRequest`, `RecordUpstreamResponse`, `RecordInboundResponse`) plus
  the two trace-only buffer factories (`NewCapture`, `NewEventList`). Every method
  is a cheap no-op when disabled: no payload allocated, no body copied, nothing
  reaches the logger. On-trace the emissions delegate to the existing
  `BridgeIoLoggerExtensions`, so the four artifacts stay **byte-identical**.
- **Both strategies stash the wire bytes they already computed, gated on
  `audit.Enabled`.** The passthrough strategy gains a one-line stash of the bytes
  it POSTs (`CopilotMessagesPassthroughStrategy`); the Codex strategy's existing
  stash becomes tracing-gated (folding the `:61`/`:103` inconsistency away). The
  endpoints then read `Response.UpstreamWireBody` for the `upstream-req` audit and
  **never re-serialize** — killing P1's redundant serialize and, as a bonus,
  guaranteeing `upstream-req` is the *exact* bytes handed to the Copilot client
  rather than a re-serialization that could theoretically diverge.
- **Collapse the P2 overload.** `UpstreamWireBody` now means exactly "the captured
  upstream wire bytes; present iff tracing is on". Routing identity is read from
  `bridgeCtx.Target.Vendor` (where the endpoints already read it for the summary),
  never inferred from a nullable audit field. The field doc is corrected.
- **Route the endpoints' inbound/upstream audit + the trace-only buffers through
  the seam**, so the unconditional `.ToArray()` and the four unconditional
  payload emissions become no-ops when tracing is off. `count_tokens` adopts the
  same `Record*` methods for the gating win (it has no strategy/capture concern).
- **Guard the invariants with a from-contract edge-case matrix.** Beyond the two
  headline invariants — (1) zero-overhead-when-off: a driven request emits zero
  bridge-IO events, allocates no capture/list, and re-serializes nothing when
  tracing is off; (2) audit fidelity: on-trace `upstream-req` equals the exact
  bytes the Copilot client received and the four artifacts match a pre-refactor
  golden — the tests cover both endpoints × both modes (streaming/buffered) × both
  trace states, plus: each artifact is the wire on its boundary and never the IR
  (Codex `inbound-req` ≠ `upstream-req`, neither is the IR); error/early-return
  paths (400 deserialize, unsupported tool, unknown model) audit only the boundary
  reached; mid-stream faults on both endpoints keep the partial `upstream-resp`
  and surface the error; model-rewrite still preserves Copilot's original
  `upstream-resp`; header redaction and shared trace-id/seq survive the seam. See
  `tasks.md §1` for the full row list; every row is mutation-checked.

## Non-goals

- No change to trace-file naming, the audit JSON shape, the sink, or the
  redaction set.
- No change to routing, translation (T1–T4), detection, or any request/response
  **wire** bytes on either the on- or off-trace path.
- Not moving the capture *result* slots (`RawUpstreamResponseBody`,
  `RawUpstreamResponseCapture`) off `BridgeResponse` — they are the well-tested
  strategy→endpoint channel and stay put; only their *gating* is unified.
- `ActivityTrackingOptions` / summary-line format (owned by the shipped
  `fix-summary-trace-id-collision` change) are untouched.

## Impact

- Affected specs: `observability` (ADD two requirements — trace capture is
  zero-overhead when disabled; `upstream-req` records the exact POSTed bytes).
- Affected code: new `Pipeline/RequestAudit.cs` + DI registration
  (`BridgeServiceCollectionExtensions.cs`); `CopilotMessagesPassthroughStrategy.cs`
  and `CopilotResponsesStrategy.cs` (inject `RequestAudit`, drop the
  `_tracingEnabled` field, gate the stash); `ClaudeCodeMessagesEndpoint.cs`,
  `CodexResponsesEndpoint.cs`, `ClaudeCodeCountTokensEndpoint.cs` (route audit
  through the seam, delete the `?? serialize` fallback and the unconditional
  `.ToArray()`); `BridgeContext.cs` (correct the `UpstreamWireBody` doc); new +
  extended unit tests.
- AOT: no reflection introduced; all serialization stays on `JsonContext`.
  `RequestAudit` is an ordinary scoped service (same shape as `BridgeContext`).
