## Context

Codex treats the presence of the HTTP response header `X-Reasoning-Included` as a wire-level accounting contract. When present, the server's reported input usage is authoritative for replayed encrypted reasoning. When absent, Codex adds its own estimate for every historical encrypted reasoning item before the latest user boundary.

The current Copilot `/responses` wire omits that header. The bridge copies Copilot response headers into its pipeline response but does not copy arbitrary headers to ASP.NET's downstream response, and it does not synthesize the missing accounting fact. Long real sessions therefore show two different numbers: Copilot may report roughly 0.5–0.7M active tokens while Codex's private compaction counter reaches the configured 0.9M threshold after adding 0.2–0.4M of historical reasoning again.

This is grounded in two independent observations:

- persisted real Codex 0.147 sessions compacted at internal sums such as `501,881 server total + 401,922 local reasoning estimate = 903,803`;
- a minimal live Copilot A/B request reported 891 input tokens with two replayed reasoning items and 65 without them, proving Copilot usage already charges those items even though the response header is absent.

Codex 0.147.0-alpha.6.6 and 0.148.0-alpha.9 also clear the remembered reasoning-included flag before their next pre-turn compaction check (upstream issue `openai/codex#32483`). The bridge cannot reorder client code. This design fixes the missing bridge signal and proves its effect inside a continuous multi-step turn, while documenting that those client versions may still prematurely compact at a later user-turn boundary until Codex fixes that ordering bug.

## Goals / Non-Goals

**Goals:**

- Make successful native Codex Responses traffic carry the accounting fact Copilot's usage semantics require.
- Keep the synthesized fact strictly at the Codex client edge and keep raw Copilot traces truthful.
- Cover streaming and buffered successful Responses without changing their body or SSE event fidelity.
- Permanently guard both halves of the contract: Copilot still charges replayed reasoning, and the bridge still exposes that fact to Codex.
- Verify with a real headless Codex two-turn session that creates historical encrypted reasoning, executes tools, and stays below the false post-sampling compaction threshold.

**Non-Goals:**

- Patching or replacing Codex's pre-turn flag-reset implementation.
- Changing model context windows, auto-compaction limits, usage numbers, encrypted reasoning items, or request replay.
- Sending an OpenAI-internal compatibility header to Copilot or exposing it on Claude Code routes.
- Claiming the bridge can prevent every premature pre-turn compact in affected Codex releases.

## Decisions

### 1. Synthesize one canonical client-edge header for successful Responses HTTP results

After the pipeline resolves `BackendVendor.CopilotResponses` and returns a 2xx HTTP status, `CodexResponsesEndpoint` will set `X-Reasoning-Included: true` on the ASP.NET response before any buffered bytes or SSE event is written. The same value will be added to the downstream `inbound-resp` audit header set.

The condition is based on the resolved backend and HTTP success, not the requested model family or a guessed slug list. The observed usage rule belongs to Copilot Responses as a protocol surface. Streaming may later end in a failed terminal, but response headers necessarily precede the stream; a failed stream contributes no successful terminal usage for Codex to retain.

Alternative considered: add the header inside `CopilotResponsesStrategy`. Rejected because `BridgeResponse.Headers` is also snapshotted as the raw `upstream-resp` header set. Mutating it would falsely claim Copilot sent the header and destroy the audit evidence that motivated this fix.

Alternative considered: rewrite reported usage downward so Codex's fallback estimate adds back to the right value. Rejected because it falsifies server usage, depends on Codex-version-specific estimators, corrupts cache telemetry, and would undercount whenever the header is honored.

### 2. Preserve strict route and failure isolation

The header is emitted only by `POST /codex/responses`, only after a Responses backend was selected, and only for an upstream 2xx result. `/cc`, non-Responses destinations, request-validation failures, unknown models, first-byte failures, and upstream non-2xx results do not gain it. No request header is added upstream.

This makes the signal a downstream compatibility assertion rather than a global transport default. If Copilot later emits the header itself, the upstream trace records that fact independently while the downstream value remains canonical.

### 3. Keep raw and downstream audit truth separate

The endpoint will snapshot `upstreamResponseHeaders` before client-edge synthesis. The raw upstream audit must continue to show the header absent when Copilot omitted it. The downstream audit must show the synthesized header because that is what Codex received.

Tests will assert both sides in one endpoint execution. This prevents a deceptively green test that checks only an in-memory header dictionary without proving the actual ASP.NET response or the audit boundary.

### 4. Test from the protocol contract and mutation-check the product seam

Offline contract tests will cover streaming success, buffered success, non-2xx isolation, resolved-vendor isolation, actual downstream HTTP headers, and the upstream-versus-inbound audit split. At least one test must fail when the injection line is temporarily removed.

A live `Kind=ApiContract` A/B probe will obtain replayable reasoning from the current Copilot Responses model, submit otherwise identical follow-ups with and without those reasoning items, and require the with-reasoning request to report a positive input-token delta. This guards the backend fact that justifies synthesis; a unit test saying only “the bridge adds a header” would remain green if Copilot's accounting semantics changed.

### 5. Use a controlled two-turn real-client actuator for the bridge-owned leg

Codex's fallback counts encrypted reasoning only after a later user boundary makes it historical, so a single user turn cannot exercise this bug. The real Codex case will therefore use two turns on one thread. Turn one returns a large replayable reasoning item with low authoritative usage. The second turn's pre-turn check remains below a small configured compact limit even under fallback accounting. Its first sampling response then reports usage that is still below the limit but close enough that adding turn one's reasoning estimate would cross it. That response also requests a real tool. Without the header, Codex issues a mid-turn compaction before normal follow-up; with the header, it trusts server usage and continues the tool trajectory.

The xUnit layer remains a thin actuator and writes a run manifest. The semantic verdict uses Codex's own log plus the per-run bridge trace: matching tool calls/results, no compact request before completion, no abort, and zero router/dispatch fatals. A separate ordinary live-Copilot behavior run confirms that the real backend/client path still executes tools with the new response header.

The thresholds deliberately leave the second turn's pre-turn check below the limit despite the current client reset-order bug. This isolates the bridge-owned post-response signal without pretending the external pre-turn bug is fixed.

## Risks / Trade-offs

- **[Copilot accounting semantics change]** → Keep the live paired A/B ApiContract probe; remove or revise synthesis if replayed reasoning stops contributing to reported input usage.
- **[Codex changes or removes the header contract]** → Pin consumer behavior to the requesting client version and refresh the real-client case when adopting a new Codex catalog interval.
- **[Header accidentally appears in raw upstream evidence]** → Inject only after the upstream header snapshot and assert opposite upstream/downstream audit results.
- **[Header leaks to Claude Code or non-Responses routes]** → Gate on both endpoint and resolved backend; add negative route tests.
- **[Current Codex still compacts early at the next user turn]** → Document the upstream reset-order bug explicitly; do not inflate model limits or falsify usage as a workaround.
- **[Streaming fails after headers are sent]** → Retain the header on the 2xx stream; the existing single failed-terminal contract remains authoritative and no completed usage is fabricated.

## Migration Plan

No configuration or data migration is required. Deploy the bridge change and restart the bridge so new Codex requests receive the header. Rollback is a code rollback that removes the synthesized downstream header; persisted Codex sessions remain readable, though affected clients resume conservative/double reasoning accounting.

## Open Questions

- Which future Codex release will contain the pre-turn reset-order fix is external and does not block the bridge-owned correction.
