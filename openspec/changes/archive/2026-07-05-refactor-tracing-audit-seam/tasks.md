# Tasks — Refactor the tracing / upstream-audit plumbing behind one seam

> Land order matters: P1 and P2 are coupled (stashing passthrough bytes changes
> when `UpstreamWireBody` is null), so the seam (§2), the stash (§3), and the
> null-overload removal (§4) ship together. Baseline self-check first (§1) —
> agent worktrees can fork stale main, so confirm the anchors before editing.

## 1. Baseline + failing contract tests (RED first)

> All tests are **from-contract**: state the invariant in a comment ("given X the
> audit must be Y, because Z"), assert observable bytes/events, and mutation-check
> (break product code → RED). A new test that greens on first run is suspect.
> The existing `UpstreamResponseCaptureContractTests` /
> `UpstreamResponseAuditEndpointTests` already cover several rows below — **extend
> them, do not duplicate**. New rows get new `[Fact]`s in the same files.

- [x] 1.1 **Anchor check.** Confirmed HEAD matched the anchors before editing
  (line numbers had drifted +1: endpoint serialize at `:220`, Codex stash at `:61`,
  both strategies had `_tracingEnabled`, three endpoints had `tracingEnabled`).

### 1.a Zero-overhead-when-off (the P1 invariant)

- [x] 1.a.1 `/cc` streaming, tracing OFF — `Cc_Streaming_TracingOff_NoAudit_NoWireBody_NoCapture`
  (zero events, null `UpstreamWireBody`, null capture, client still sees the
  stream). Mutation-checked: unconditional stash → RED.
- [x] 1.a.2 `/cc` buffered, tracing OFF — `Cc_Buffered_TracingOff_NoAudit_NoRawStash`
  (adds `RawUpstreamResponseBody == null`). Mutation-checked.
- [x] 1.a.3 `/codex` tracing OFF — `Codex_TracingOff_NoAudit` (end-to-end through the
  real endpoint + strategy). Codex streaming/buffered strategy-level parity remains
  covered by `UpstreamResponseCaptureContractTests.Codex_TracingOff_Streaming_NoCapture`.
- [~] 1.a.4 `count_tokens` tracing OFF — routed through the seam (§4.3); a dedicated
  end-to-end zero-event test is deferred (count_tokens has no strategy/capture path;
  the seam gate is the same code proven by 1.a.1–1.a.3).
- [~] 1.a.5 **No second serialize off-trace** — enforced structurally (the `?? Serialize`
  fallback is deleted, §4.1/§4.2; the only serialize is the strategy's POST body).
  A serialize-counter probe was intentionally NOT added to product code; the
  deletion + `Cc_*_TracingOff` (null wire body, zero events) covers the observable
  contract without an intrusive counter.
- [~] 1.a.6 **No inbound `.ToArray()` off-trace** — the copy now lives inside
  `RequestAudit.RecordInbound` behind `if (!Enabled) return;` (proven RED by the
  seam-gate mutation). A direct allocation probe was not added (same rationale as
  1.a.5: observable via zero-events).

### 1.b Audit fidelity when ON — exact wire bytes, never the IR

- [x] 1.b.1 `/cc` `upstream-req` == exact POSTed bytes — `Cc_TracingOn_UpstreamReqEqualsExactPostedBytes`.
  Mutation-checked: skip stash → empty `upstream-req` → RED.
- [x] 1.b.2 `/codex` `upstream-req` == exact POSTed Responses (T2) bytes —
  `Codex_TracingOn_UpstreamReqEqualsPostedResponsesBytes`. Mutation-checked.
- [x] 1.b.3 `/codex` `inbound-req` is the untranslated Codex request, NOT the IR —
  `Codex_TracingOn_InboundReqIsRawCodexRequest_NotIr` + `/cc` sibling
  `Cc_TracingOn_InboundReqIsRawClientBytes_NotIr`.
- [x] 1.b.4 `/codex` `inbound-req` vs `upstream-req` — `Codex_TracingOn_InboundAndUpstreamAreResponses_NeitherIsIr`.
  NOTE: the original "always byte-unequal" phrasing was WRONG — a from-contract
  test proved T1∘T2 is an identity for a trivial request, so inequality is
  request-dependent. The test (and the design/spec) now assert the true invariant:
  both are Responses, neither is the IR (`max_tokens` absent from both).
- [x] 1.b.5 `upstream-resp` == Copilot's raw pre-stage bytes — existing
  `UpstreamResponseCaptureContractTests` (both endpoints, both modes) + endpoint-level
  `UpstreamResponseAuditEndpointTests.Streaming_TracingOn_UpstreamRespBodyEqualsRawSse`,
  all still green post-refactor.
- [x] 1.b.6 `upstream-resp` survives model rewrite — existing
  `TracingOn_Buffered_CaptureKeepsCopilotModel_AfterRewrite` +
  `Buffered_AfterModelRewrite_UpstreamRespKeepsCopilotBytes`, still green.
- [x] 1.b.7 On-trace tee does not perturb the client stream — existing
  `TracingOn_Streaming_ClientEventsIdenticalToNoTee`, still green.

### 1.c Edge cases — errors, faults, boundaries

- [x] 1.c.1 Deserialize failure (400) — `Cc_DeserializeFailure_AuditsInboundOnly_NoUpstream`
  (inbound-req has raw bad bytes, inbound-resp present, no upstream-*) +
  `Cc_DeserializeFailure_TracingOff_NoAudit`.
- [~] 1.c.2 Unsupported server tool short-circuit — deferred; same early-return
  audit shape as 1.c.1 (inbound-only), which is covered.
- [~] 1.c.3 `UnknownModelException` (400) — deferred; covered structurally by the
  `Target`-gated `upstream-*` emission (no `Target.Endpoint` ⇒ no upstream audit),
  same gate as 1.c.1.
- [~] 1.c.4 Transient upstream disconnect (502) pre-headers — deferred; the
  `upstream-resp` `error` field plumbing is unchanged by the refactor.
- [x] 1.c.5 `/cc` mid-stream fault — existing `Cc_MidStreamFault_PartialCaptureKept_AndFaultPropagates`,
  still green.
- [x] 1.c.6 `/codex` mid-stream fault — existing `Codex_MidStreamFault_Swallowed_ButFaultSurfaced_AndPartialKept`,
  still green.
- [~] 1.c.7 Client cancellation — deferred (preserve-behavior; the cancel path
  rethrows before the finally audit, unchanged by the refactor).
- [x] 1.c.8 Empty / zero-length body — `Cc_EmptyUpstreamBody_TracingOn_NoCrash_ZeroLengthAudit`.
- [x] 1.c.9 Header redaction flows through the seam — `Cc_TracingOn_SensitiveHeadersFlowToSinkForRedaction`
  (the seam preserves the header for the sink to redact; sink redaction itself is
  covered by existing sink tests).
- [~] 1.c.10 Shared trace-id/seq across the four artifacts — deferred; the endpoints
  pass the same `seq`/`traceId` literals to every `Record*` (unchanged wiring),
  and the four-artifact `kinds` set is asserted by `Cc_Streaming_TracingOn_EmitsFourArtifacts`.

### 1.d Toggle symmetry

- [x] 1.d.1 Same request ON vs OFF → identical wire output —
  `Cc_Streaming_OnVsOff_ClientBytesIdentical`.

## 2. Introduce the `RequestAudit` seam (P3)

- [x] 2.1 Added `Pipeline/RequestAudit.cs`: scoped service, `bool Enabled`, four
  `Record*` no-op-when-off wrappers (delegating to `BridgeIoLoggerExtensions`),
  `NewCapture()`/`NewEventList()`. `RecordInbound` takes a `ReadOnlyMemory<byte>`
  and copies only when enabled.
- [x] 2.2 Registered `services.AddScoped<RequestAudit>()` next to `BridgeContext`.
  Full suite green (incl. the DI `ValidateOnBuild` startup test).
- [x] 2.3 Injected `RequestAudit` into both strategies; deleted `_tracingEnabled` +
  the `IOptions<TracingOptions>` ctor param; `_audit.Enabled` / `_audit.NewCapture()`.

## 3. Stash the wire bytes on both strategies (P1)

- [x] 3.1 `CopilotMessagesPassthroughStrategy`: `if (_audit.Enabled) ctx.Response.UpstreamWireBody = body;`
  right after the existing serialize — reuses the POSTed bytes, no new serialize.
- [x] 3.2 `CopilotResponsesStrategy`: gated the existing `UpstreamWireBody` set on
  `_audit.Enabled`, matching the buffered raw-capture sibling.

## 4. Route endpoint audit through the seam; collapse the P2 overload

- [x] 4.1 `ClaudeCodeMessagesEndpoint`: `audit.NewEventList()`; deleted the
  `?? SerializeToUtf8Bytes(...)` fallback (read `UpstreamWireBody ?? []` under an
  `audit.Enabled` guard); all four emissions + the inbound `.ToArray()` via
  `audit.Record*`; removed the now-dead `upstreamBodyPooled`/`responseBodyPooled`.
- [x] 4.2 `CodexResponsesEndpoint`: same treatment.
- [x] 4.3 `ClaudeCodeCountTokensEndpoint`: its five emissions via `audit.Record*`
  (the inbound `.ToArray()` stays — it feeds the summary model-probe + the forwarded
  body, so it is not audit-only here; documented in a comment).
- [x] 4.3b `ClaudeCodeModelsEndpoint` (`GET /cc/v1/models`): found during review to
  emit its two audit payloads UNCONDITIONALLY (no gate) — the exact drift P3
  prevents. Routed through `audit.RecordInbound`/`RecordInboundResponse` too, so ALL
  four audit-emitting endpoints now share the one seam; removed the dead `ModelsTag`.
- [x] 4.4 Confirmed no source reads `UpstreamWireBody` to infer backend identity —
  the only reads are the two endpoints' audit body (`UpstreamWireBody ?? []`), via
  the seam. Routing identity stays on `bridgeCtx.Target.Vendor`.
- [x] 4.5 Corrected the `BridgeContext.cs` `UpstreamWireBody` doc to "the captured
  upstream wire bytes; non-null iff tracing is on and a strategy ran", and that it
  is NOT a routing signal (that is `Target.Vendor`).

## 5. Verification

- [x] 5.1 `dotnet test --filter "Category!=Integration"` green — **559 passed, 0
  failed** (545 pre-existing incl. the extended `UpstreamResponseCaptureContractTests`
  / `UpstreamResponseAuditEndpointTests`, + 14 new §1 tests). Release build clean, 0
  warnings.
- [x] 5.2 **Edge-case matrix.** Every landed row is a from-contract `[Fact]`;
  mutation-checked the three load-bearing ones (passthrough stash gate, Codex stash
  gate, seam `Enabled` gate) → all RED under mutation, restored. Deferred rows
  (`[~]` above) are covered structurally or by existing tests, with the rationale
  recorded inline. One row (1.b.4) was CORRECTED by a failing from-contract test —
  the "always byte-unequal" claim was wrong; the true invariant (neither is the IR)
  is now asserted in test, design, and spec.
- [x] 5.3 AOT publish per the `CLAUDE.md` PowerShell block: `Generating native code`
  → `PUBLISH_EXIT: 0`, no `vswhere` error, no new IL/trim warnings. Fresh
  `publish\copilot-bridge.exe`, ~12 MB (in the expected ≈11 MB range).
- [ ] 5.4 Live smoke (non-8765 port): one CC→opus and one CC→gpt-5.5 request with
  tracing ON; diff the four JSON files against captures taken from before the
  refactor — byte-identical (esp. Codex `inbound-req` vs `upstream-req`). Then one
  request each with tracing OFF; confirm no trace files are written. **[deferred —
  needs live Copilot creds; the offline end-to-end tests through the real endpoints
  + strategies cover the artifact byte-shapes.]**
- [x] 5.5 Folded the durable facts into `docs/observability-design.md` (the home)
  and pointed `pipeline-design.md §9` at it: one `RequestAudit` seam; `upstream-req`
  is the exact POSTed bytes; each artifact is the wire on its boundary, never the
  IR; off-trace is allocation-free on the request path.
