# upstream-timeout Specification

## Purpose

Bounds how long the bridge waits on an unresponsive upstream (GitHub Copilot)
while forwarding a request, using two independent **inactivity** budgets — a
first-byte budget over the response-headers phase and a stream-idle budget over
the SSE body — rather than a total-duration cap, so a slow-but-progressing request
is never aborted. Both budgets apply to **both** forward paths: `/cc` (Anthropic
passthrough) and Codex (Responses). This capability defines when each budget
fires, how a fired budget surfaces to the client (a pre-headers `504`; a
mid-stream retryable error on `/cc` or a `response.failed` terminal on Codex) and
to the operator (the request summary and log), and that a client cancellation
always wins the race against a timeout.
## Requirements
### Requirement: First-byte inactivity budget

The bridge SHALL bound the time it waits for Copilot to return response headers
(the first byte) when forwarding a request on either path (`/cc` or Codex). If no
response headers arrive within the configured **first-byte budget**, the bridge
SHALL abort the upstream call and surface a timeout, rather than continuing to
wait. Both paths share one client-layer implementation, so the budget applies
identically to each.

The budget SHALL be an *inactivity* bound over the response-headers phase only;
it SHALL be applied from outside the client's transient-retry loop, so that
retry backoff delays do not consume the budget and each fresh send is granted
the full budget.

The budget SHALL be independently configurable and SHALL be disable-able: a
configured value of zero or less means the bridge imposes no first-byte bound
(reverting to the pre-existing coarse `HttpClient.Timeout` behavior) and incurs
no timer overhead on that path.

#### Scenario: First byte never arrives

- **WHEN** the bridge forwards a request and Copilot returns no response headers within the first-byte budget
- **THEN** the bridge aborts the upstream call within approximately that budget
- **AND** because no response bytes have reached the client, the client receives an HTTP `504 Gateway Timeout`
- **AND** the request summary records the outcome as an upstream first-byte timeout, distinct from a client cancellation.

#### Scenario: First byte arrives before the budget elapses

- **WHEN** Copilot returns response headers before the first-byte budget elapses, however close to it
- **THEN** the bridge does not abort, and forwarding proceeds normally (streaming relay or buffered read).

#### Scenario: Retry backoff does not consume the budget

- **WHEN** the first send fails transiently and the client retries after a backoff delay
- **THEN** the retried send is granted the full first-byte budget, measured from the retry — the backoff delay is not counted against it.

#### Scenario: First-byte budget disabled

- **WHEN** the first-byte budget is configured to zero or less
- **THEN** the bridge imposes no first-byte inactivity bound and allocates no first-byte timer.

### Requirement: Stream inactivity budget

Once an SSE stream from Copilot has started, the bridge SHALL bound the gap
between consecutive upstream events. The budget SHALL be reset on every event the
bridge pulls from the upstream stream, so that a stream which keeps emitting is
never aborted regardless of total length. If the gap between two consecutive
upstream events exceeds the configured **stream-idle budget**, the bridge SHALL
abort the upstream read.

The budget SHALL be independently configurable and disable-able: a configured
value of zero or less means the bridge imposes no stream-idle bound and incurs no
timer overhead on the streaming relay path.

Because response headers have already been sent to the client when a stream-idle
timeout fires, the wire status cannot be rewritten. The surface depends on the
client protocol of the path:

- On the **`/cc` (Anthropic) path**, by default the bridge SHALL end the stalled
  turn with a **retryable signal**: it SHALL inject the same retryable error event
  the response guards use (an `overloaded_error` SSE event) and then end the
  stream, so that Claude Code re-attempts the turn rather than committing a silent
  partial. (Verified against Claude Code 2.1.207: with the legacy disable switch
  absent, a mid-stream error tombstones the partial attempt and issues a
  non-streaming fallback request; the bridge's client-configuration removes that
  switch.) An operator SHALL be able to configure the bridge
  to instead end the stream as a plain truncation (no error event).
- On the **Codex (Responses) path**, the bridge SHALL end the stalled turn through
  the same failed-terminal channel it already uses for a mid-stream upstream fault:
  it SHALL flush a `response.failed` terminal so the Codex client sees a
  well-formed terminated stream, rather than an Anthropic `overloaded_error`
  envelope the Codex protocol does not understand.

#### Scenario: Upstream stalls mid-stream (default: retryable)

- **WHEN** Copilot emits one or more SSE events and then produces no further event for longer than the stream-idle budget
- **THEN** the bridge aborts the upstream read within approximately that budget of the last event
- **AND** by default the bridge injects a retryable error event (`overloaded_error`) and ends the stream, so Claude Code re-attempts the turn
- **AND** the request summary records the outcome as an upstream stream-idle timeout, distinct from a client cancellation.

#### Scenario: Upstream stalls mid-stream (truncation configured)

- **WHEN** the operator has configured the stream-idle timeout to truncate rather than signal a retry, and upstream then stalls beyond the budget
- **THEN** the bridge ends the stream with no synthetic error event (the already-sent `200` stands)
- **AND** the request summary still records an upstream stream-idle timeout.

#### Scenario: Codex path stalls mid-stream

- **WHEN** the Codex/Responses upstream emits one or more events and then goes silent beyond the stream-idle budget
- **THEN** the bridge aborts the read and flushes a `response.failed` terminal (its existing mid-stream-fault channel), not an Anthropic `overloaded_error` event
- **AND** the request summary records an upstream stream-idle timeout.

#### Scenario: Upstream keeps emitting

- **WHEN** Copilot emits SSE events with every inter-event gap shorter than the stream-idle budget, for any total number of events and any total duration
- **THEN** the bridge never aborts on the stream-idle budget and relays every event.

#### Scenario: Stream-idle budget disabled

- **WHEN** the stream-idle budget is configured to zero or less
- **THEN** the bridge imposes no stream-idle bound and allocates no per-event timer, and the streaming relay is byte-identical to the no-timeout path.

### Requirement: Timeout is distinguished from client cancellation

The bridge SHALL distinguish an upstream inactivity timeout (the bridge aborted
the upstream because it stalled) from a client cancellation (the caller aborted
the request). The two SHALL surface differently: a client cancellation continues
to be reported as such, while an upstream timeout is reported as an upstream
timeout in both the request summary and the operator log, so an operator is not
misled into diagnosing a bridge regression or a client hang-up when the cause was
an unresponsive upstream.

#### Scenario: Client cancels while upstream is healthy

- **WHEN** the caller aborts the request and no inactivity budget has been exceeded
- **THEN** the bridge reports a client cancellation, not an upstream timeout.

#### Scenario: Upstream stalls while the client is still waiting

- **WHEN** an inactivity budget is exceeded while the caller is still connected
- **THEN** the bridge reports an upstream timeout, not a client cancellation, and the log line names it as an upstream inactivity timeout with the phase (first-byte or stream-idle) and the elapsed idle time.

### Requirement: The forward hot paths are not regressed

Adding the timeout SHALL NOT alter the bytes the bridge forwards upstream or
relays downstream on either forward path. When both budgets are enabled and
upstream is responsive, the forwarded request body, the outbound headers, and the
relayed events SHALL be identical to the pre-change behavior on both the `/cc`
passthrough path and the Codex/Responses translation path; the only added work is
arming and resetting the inactivity timers.

#### Scenario: Enabled but upstream responsive (`/cc`)

- **WHEN** both budgets are enabled and the `/cc` upstream responds within them
- **THEN** the forwarded upstream body and the downstream event sequence are identical to the behavior with the timeout absent.

#### Scenario: Enabled but upstream responsive (Codex)

- **WHEN** both budgets are enabled and the Codex/Responses upstream responds within them
- **THEN** the translated (T3) downstream event sequence is identical to the behavior with the timeout absent.
