## Why

When Claude Code is routed to a Copilot Responses backend, a mid-stream upstream fault is currently terminated with the bridge-internal `stop_reason: "error"` marker. Claude Code accepts that as an ordinary completed text turn, does not retry, and silently stops its agent loop even when the model had just promised to perform more work.

The production incident `20260713-024829-0074` demonstrates the failure: Copilot left an assistant commentary item in progress without a Responses terminal, the stream-idle budget fired, and the bridge returned a completed Anthropic stream carrying the private marker instead of the retryable Anthropic error required by the `/cc` contract.

## What Changes

- Surface a mid-stream fault according to the downstream client protocol, not merely the selected upstream backend protocol.
- Ensure `/cc` requests routed to `/responses` receive the configured retryable Anthropic SSE error on a stream-idle timeout, so Claude Code enters its streaming retry/fallback path.
- Translate a successful non-streaming Responses fallback body into an Anthropic
  Messages response on cross-routed `/cc` requests. Claude Code 2.1.207 recovers
  from a streaming error by issuing `stream:false`; without this leg it rejects
  the raw Responses object as an empty or malformed HTTP 200.
- Keep Codex clients on the Responses-native `response.failed` terminal contract.
- Prevent the T3-to-T4 private failure marker from crossing the Claude Code edge under any routing combination.
- Record caught Responses-stream faults, including the timeout phase and error, in `/cc` request summaries and trace artifacts instead of reporting a misleading clean `200`.
- Add contract-derived regression coverage and a real Claude Code-to-GPT behavior test that exercises a stalled Responses stream and verifies the client retries rather than ending the task.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `upstream-timeout`: Make the existing client-protocol-specific mid-stream timeout contract hold when `/cc` is cross-routed to a Responses backend, while preserving the Codex `response.failed` surface.
- `observability`: Require internally caught upstream stream faults to remain visible in the request summary and trace error fields at the downstream endpoint that owns the request.

## Impact

- Affects the Responses backend strategy, Claude Code and Codex outbound/endpoint fault handling, and the boundary between the T3 Responses-to-IR translator and downstream adapters.
- Successful streaming bytes remain unchanged. A `/cc` request cross-routed to
  Responses with `stream:false` now receives the Anthropic protocol it requested
  instead of the previously invalid raw Responses object.
- Adds unit, API-contract, and real-client behavior coverage. No new dependency or JSON DTO is expected.
