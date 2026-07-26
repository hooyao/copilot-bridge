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
timeout fires, the wire status cannot be rewritten. The timeout surface SHALL be
selected from the **downstream client protocol**, independently of whether routing
selected the Anthropic or Responses upstream backend:

- On every **`/cc` (Anthropic client) path**, including Claude Code cross-routed to
  a Copilot Responses model, by default the bridge SHALL end the stalled turn with
  a **retryable signal**. It SHALL inject the same retryable error event the
  response guards use (an `overloaded_error` SSE event) and then end the stream, so
  Claude Code re-attempts the turn rather than committing a silent partial. A
  Responses-to-IR private failure marker SHALL NOT be exposed as an Anthropic
  `message_delta.stop_reason`, and the bridge SHALL NOT synthesize an apparently
  successful `message_stop` for the fault. An operator SHALL be able to configure
  the bridge to instead end the stream as a plain truncation with no error event.
- On every **Codex (Responses client) path**, the bridge SHALL end the stalled turn
  through the failed-terminal channel: it SHALL flush one `response.failed`
  terminal so the Codex client sees a well-formed terminated stream, rather than
  an Anthropic error envelope.

#### Scenario: Native Anthropic upstream stalls on the Claude Code path

- **WHEN** a `/cc` request uses the Anthropic upstream, Copilot emits one or more SSE events, and then produces no further event for longer than the stream-idle budget
- **THEN** the bridge aborts the upstream read within approximately that budget of the last event
- **AND** by default the bridge injects a retryable `overloaded_error` event and ends the stream
- **AND** the request summary records an upstream stream-idle timeout, distinct from a client cancellation.

#### Scenario: Responses upstream stalls on the Claude Code path

- **WHEN** a `/cc` request is routed to a Responses model, Copilot leaves an output item in progress without emitting `response.completed`, `response.failed`, or `response.incomplete`, and then produces no event for longer than the stream-idle budget
- **THEN** the bridge aborts the upstream read and injects the configured retryable Anthropic error event
- **AND** the downstream stream contains neither the private `stop_reason: "error"` marker nor a synthetic normal `message_stop` for that fault
- **AND** Claude Code observes a streaming error and re-attempts or falls back according to its configured streaming-error policy.

#### Scenario: Claude Code performs a non-streaming fallback after the error

- **WHEN** Claude Code receives the retryable error from a `/cc` request routed to Responses and reissues the turn with `stream:false`
- **THEN** the bridge translates the successful buffered Responses object to an Anthropic Messages response
- **AND** text and tool-use blocks remain executable by Claude Code
- **AND** a raw Responses object does not cross the `/cc` edge.

#### Scenario: Responses upstream stall is configured to truncate on the Claude Code path

- **WHEN** the operator configures stream-idle action `Truncate`, a `/cc` request is routed to a Responses model, and upstream stalls beyond the budget
- **THEN** the bridge ends the stream with no synthetic error event
- **AND** it does not expose the private failure marker as an Anthropic stop reason
- **AND** the request summary still records an upstream stream-idle timeout.

#### Scenario: Responses upstream stalls on the Codex path

- **WHEN** a Codex request receives one or more Responses events and upstream then goes silent beyond the stream-idle budget
- **THEN** the bridge aborts the read and emits exactly one `response.failed` terminal, not an Anthropic `overloaded_error` event
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

### Requirement: The bridge is the sole idle actor for a configured client

The bridge's stream-idle budget SHALL be the only inactivity bound acting on a
turn for a client the bridge configures. Where a supported client ships its own
client-side idle abort, the bridge's client-autoconfiguration SHALL disable it
(for Claude Code: `API_FORCE_IDLE_TIMEOUT` = `"0"`), so that a stalled turn is
ended by the bridge's configured budget and action rather than by an
uncoordinated client timer the operator cannot tune.

The bridge's stream-idle default SHALL NOT be justified by reference to a
client-side watchdog value. Any documentation of that default SHALL describe the
real client mechanism or omit the comparison; it SHALL NOT cite a client
environment variable the bridge has not verified to exist.

Rationale: the previous default was documented as sitting "below Claude Code's
own watchdog `CLAUDE_STREAM_IDLE_TIMEOUT_MS`, default 90s". That variable does not
appear in current Claude Code documentation. The real mechanism,
`API_FORCE_IDLE_TIMEOUT`, aborts after 5 minutes and is active on non-Anthropic
providers — an entirely different value and trigger, so the recorded reasoning for
the default was unsound. Correcting the rationale is independent of the default's
numeric value, which this change leaves unchanged.

#### Scenario: A configured Claude Code client has no competing idle timer

- **WHEN** Claude Code has been configured by `config claude-code`
- **AND** an upstream stream goes idle past the bridge's stream-idle budget
- **THEN** the turn is ended by the bridge's configured stream-idle action
- **AND** the client does not independently abort the turn on its own idle timeout

#### Scenario: Documentation of the stream-idle default cites a real mechanism

- **WHEN** the stream-idle budget's default is explained in configuration comments
  or design documentation
- **THEN** the explanation does not cite `CLAUDE_STREAM_IDLE_TIMEOUT_MS`
- **AND** any client-side timeout it references is one that current client
  documentation defines

### Requirement: The upstream token-less stream cap is documented

The repository SHALL document the Copilot backend's server-side cap on a stream
that has produced no token, including the measured value, how it terminates, and
the fact that it is not defeatable by any bridge or client configuration.

The cap is an upstream property, not bridge behavior: Copilot closes such a
connection after roughly 300 seconds with a clean EOF — no `error` event and no
`message_stop`. Because a bridge stream-idle timeout and this cap present
similarly to an operator (a stalled stream that never completes), the distinction
SHALL be recorded so that a genuine upstream cap is not mistaken for a tunable
bridge budget. Documentation SHALL state the operational consequence: a model
whose extended thinking exceeds the cap before emitting a first token fails
deterministically, and the available remedy is reducing reasoning effort or prompt
size, not raising a timeout.

#### Scenario: An operator investigating a stalled turn can distinguish the causes

- **WHEN** an operator consults the documentation after a turn that produced
  `message_start` and no token
- **THEN** the documentation describes the ~300s upstream cap, its clean-EOF
  termination, and the measurements establishing it
- **AND** it states that raising the bridge's stream-idle budget does not extend it

