## Context

The bridge uses an Anthropic-shaped hub IR. A Claude Code request routed to a GPT model takes the following response path:

`Copilot /responses` → T3 `ResponsesToAnthropicStream` → Claude Code outbound adapter → `/cc` client.

A Codex request to the same backend adds T4 after T3:

`Copilot /responses` → T3 → T4 `IrToResponsesOutboundAdapter` → Codex client.

Today `CopilotResponsesStrategy` catches every exception while reading the upstream stream so it can synthesize an IR terminal. On a fault, T3 sets the private stop reason `"error"`; T4 recognizes that marker and emits `response.failed`. That is correct only when T4 is present. On Claude Code-to-GPT routing there is no T4, so the private marker crosses the Claude client edge as an ordinary `message_delta`, followed by `message_stop`.

Production request `20260713-024829-0074` makes the consequence concrete. The raw Responses stream had continuous sequence numbers, left a commentary message item `in_progress`, emitted no Responses terminal, and then went idle. The 60-second budget fired, but Claude Code received a complete text message with `stop_reason: "error"`. Claude Code 2.1.206 persisted that message and ended the turn. The reconstructed Claude Code source independently explains the mechanism: runtime SSE parsing assigns `message_delta.stop_reason` without enum validation, while the agent loop continues only when a real `tool_use` block arrives; an SSE `event:error`, by contrast, throws and enters the streaming retry/fallback path.

Real-client verification against Claude Code 2.1.207 refined the last mechanism:
the recovery path is a **non-streaming fallback request** (`stream:false`), not a
second streaming request. With `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1`, the
streaming error is surfaced immediately because its mid-body exception has no
retryable HTTP status. With fallback enabled, Claude tombstones the partial turn
and re-requests it non-streaming. The pre-change bridge forwarded the successful
Responses JSON object verbatim to `/cc`, which Claude rejected as an empty or
malformed HTTP 200. Therefore client recovery requires both the retryable SSE
error and a valid Anthropic buffered response on the fallback leg.

The existing contract already says that mid-stream timeouts are shaped by client protocol: Anthropic clients receive a retryable error and Responses clients receive `response.failed`. The implementation currently shapes the failure too early, according to the upstream strategy.

## Goals / Non-Goals

**Goals:**

- Select the failure wire shape at the downstream client boundary.
- Make Claude Code-to-Responses stream-idle timeouts enter Claude Code's retry/fallback path.
- Preserve the Codex guarantee of one well-formed `response.failed` terminal.
- Make a cross-routed timeout visible in the request summary and trace error fields.
- Preserve successful-stream bytes and the zero-overhead tracing-off contract.
- Support Claude Code's actual `stream:false` recovery request on a `/cc` to
  Responses cross-route, including tool-use responses.

**Non-Goals:**

- Explain or fix why Copilot's GPT backend occasionally leaves a Responses item in progress.
- Change the 60-second default based on a single censored observation; the trace cannot show whether upstream would have resumed after the bridge cancelled it.
- Retry an already-started upstream request inside the bridge.
- Change Claude Code or the Anthropic SDK.
- Alter routing, effort mapping, model profiles, or successful translation semantics.

## Decisions

### D1 — The downstream adapter/endpoint owns failure protocol selection

T3 SHALL translate successful Responses events into the hub IR, but it SHALL NOT turn a read fault into an apparently normal Anthropic terminal. A fault remains exceptional until it reaches the client edge:

- Claude Code edge: `ClaudeCodeMessagesEndpoint` applies the existing `StreamIdleAction` and `StreamIdleSignal` policy and, by default, writes Anthropic `event: error` framing.
- Codex edge: T4 catches the exceptional IR stream, emits `response.failed`, then rethrows so the endpoint can record the failure.

This matches the architecture: only the downstream edge knows which protocol the caller speaks.

Alternative considered: teach T3 which client invoked it. Rejected because it couples an upstream strategy to endpoint identity and creates a second routing axis inside the hub.

Alternative considered: let `ClaudeCodeOutboundAdapter` rewrite `stop_reason: "error"`. Rejected because the adapter would need timeout configuration and exception detail, and a normal terminal would already have falsely committed the partial turn before the rewrite could express a real streaming error.

### D2 — Propagate read faults; do not encode exceptions as IR stop reasons

`CopilotResponsesStrategy` SHALL dispose the upstream response and raw stream but allow a stream-read exception, including `UpstreamTimeoutException(StreamIdle)`, to propagate out of its asynchronous event stream. It SHALL not call `FlushTerminal(failed: true)` for that exception.

T4 already has the required catch-and-flush structure: it manually enumerates the IR stream, emits `response.failed` on a fault, and rethrows. The Codex endpoint SHALL treat a rethrown mid-stream timeout as an already-started failed stream, retain status `200`, and record the timeout rather than assuming every timeout reaching its catch is pre-header.

This removes the need to use the private `ErrorStopReason` for exceptions. If it remains for a narrow translator-only purpose, the Claude Code edge SHALL fail closed rather than relay it.

Alternative considered: keep swallowing the exception and inspect `BridgeResponse.UpstreamStreamFault` after relay. Rejected because T3 has already emitted the private terminal before the endpoint learns of the fault; appending an error after `message_stop` is the wrong event order and still leaks an invalid stop reason.

### D3 — Convert an upstream `response.failed` terminal into a typed stream failure

An explicit upstream `response.failed` is a failure event, not a successful IR terminal. T3 SHALL retain its diagnostic detail in a bounded, content-free typed exception/fault and surface it through the same downstream-boundary mechanism:

- `/cc` emits an Anthropic error event rather than a private stop reason.
- `/codex` emits `response.failed`.

The bridge SHALL never include prompts, tool arguments, or response text in the exception or operator log. If the upstream failure code can be classified without guessing, it may select an existing error signal; otherwise it uses the bridge's ordinary upstream-error surface.

Alternative considered: leave explicit `response.failed` on the private marker while changing only timeouts. Rejected because the same marker would remain capable of leaking on the same cross-routed path.

### D4 — Preserve the configured timeout action on `/cc`

For `UpstreamTimeoutException(StreamIdle)`, the Claude endpoint reuses its existing mapping:

- `Retry`: write exactly one configured retryable `event:error` after the already-relayed content.
- `Truncate`: write no synthetic error event.

Neither action writes `message_delta.stop_reason: "error"` or a synthetic `message_stop` for the fault. First-byte timeouts remain real HTTP 504 responses.

### D5 — Record faults from the relay outcome before writing the summary/audit

Both endpoints SHALL set `summary.Error`, `summary.UpstreamTimeout`, and the audit error from the exception observed while draining the client-facing stream. The already-started HTTP status remains 200. Raw upstream capture is sealed only after relay ends, preserving the partial bytes through the last upstream event.

The current `BridgeResponse.UpstreamStreamFault` side channel may be removed if all faults propagate, or retained only for paths that still intentionally catch; it SHALL not be the sole source of truth for a fault that the endpoint directly catches.

### D6 — Keep the timeout default unchanged in this change

The incident proves incorrect failure shaping, but not that 60 seconds is necessarily too short: the observation is right-censored because the bridge cancelled the upstream read at the budget. This change therefore fixes deterministic recovery and observability first. A later evidence-gathering change may measure inter-event gaps and revisit the default.

### D7 — Test from the contract and include the real client

Unit/API-contract tests SHALL begin from observable behavior:

- a `/cc` request cross-routed to Responses emits partial text, stalls, and then produces exactly one retryable Anthropic error with no private marker or synthetic normal terminal;
- `Truncate` emits neither error nor private marker;
- a Codex client receives exactly one `response.failed`;
- successful streams are unchanged;
- summaries and traces record the fault.

The regression test SHALL be mutation-checked by restoring the current swallow-and-flush behavior and observing failures.

Final acceptance requires a real `claude.exe` headless multi-step task through the `CcToGpt` scenario. A controllable upstream test leg SHALL exercise the actual stall/fault path; the verdict comes from the Claude transcript: the failed attempt must not commit as a completed partial turn, a subsequent request must occur, and the task must continue to successful tool execution. A real Copilot run that never stalls is useful health evidence but does not exercise this fix.

### D8 — Translate the Claude non-streaming fallback at the client edge

The `/cc` outbound adapter SHALL recognize a successful buffered Responses object
on a cross-routed request and convert it to an Anthropic Messages response. This is
downstream protocol ownership: Codex continues to receive the original Responses
object, while Claude receives text/tool-use blocks, stop reason, model, and usage
in Anthropic shape. Upstream error envelopes remain error envelopes and are not
misclassified as successful messages.

The conversion uses `JsonDocument` plus `Utf8JsonWriter` and carries only modeled
Responses fields, preserving Native AOT constraints without adding reflection
serialization. Streaming success bytes remain untouched.

## Risks / Trade-offs

- **T4 emits `response.failed` and then the Codex endpoint mishandles the rethrown timeout** → make the catch response-start-aware and pin exactly one terminal plus status 200 in an endpoint test.
- **An error is written after an Anthropic normal terminal** → T3 must not synthesize the terminal on the fault path; assert event ordering and absence of both private marker and `message_stop`.
- **Retry duplicates a tool that began before the stall** → preserve the existing Claude Code configuration that disables non-streaming fallback when streaming guards are active, and test a stall before tool dispatch for this incident contract. Do not add bridge-side replay.
- **A typed upstream failure leaks response content into logs** → carry only bounded code/category metadata and use existing bridge-authored client messages.
- **Successful hot path regresses** → retain byte/event equality tests with the fault machinery dormant and tracing off.
- **The upstream may legitimately remain silent for more than 60 seconds** → deterministic retry prevents silent task termination; timeout tuning remains operator-configurable and explicitly outside this change.

## Migration Plan

1. Update `docs/pipeline-design.md` to state that downstream client edges select failure framing on cross-model routes.
2. Land contract tests that fail against the current private-marker behavior.
3. Change T3 fault propagation and adapt both endpoints/T4 without changing successful event translation.
4. Run unit and API-contract suites, then the path-exercising real Claude Code behavior scenario.
5. Roll back by reverting the change; no data or configuration migration is involved.

## Open Questions

- Explicit upstream `response.failed` uses the bounded `api_error` client surface;
  only its safe machine code is retained. It never maps to a normal Anthropic stop
  reason or logs the upstream generated message.

## Verification Record

- Real-client manifest:
  `tests/behavior-runs/manifests/cc-to-gpt-stream-fault-recovery-20260713-040600-535.json`
- Bridge base URL used `http://localhost:56648` (OS-assigned, not 8765).
- Claude Code 2.1.207 exited 0 after four main-agent upstream requests.
- Fault request id: `20260713-040556-0002`. Its Claude-facing trace contains
  partial commentary followed by exactly one `event:error`; it contains no
  `bridge_*`, no `stop_reason: "error"`, and no `message_stop`.
- Recovery request `20260713-040558-0003` is Claude's non-streaming fallback and
  carries an Anthropic `tool_use` translated from the buffered Responses object.
- Claude's transcript contains successful Bash and Read tool results and ends with
  `cc-stream-recovery-canary-64129`; the failed partial commentary is absent.
