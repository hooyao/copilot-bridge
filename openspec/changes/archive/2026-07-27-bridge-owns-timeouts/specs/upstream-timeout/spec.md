## MODIFIED Requirements

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

The configured budget SHALL be the sole bound on that phase. The shared upstream
HTTP client SHALL NOT impose a coarse whole-request timeout of its own, because
such a cap also bounds the **buffered (non-streaming) response body** — the path
Claude Code uses to recover a failed streaming turn — and would silently truncate
a legitimately slow turn at the coarse cap regardless of the configured budget.

The budget SHALL be independently configurable and SHALL be disable-able: a
configured value of zero or less means the bridge imposes **no** first-byte bound
and incurs no timer overhead on that path. With the coarse client-level timeout
removed, disabling the budget leaves that phase genuinely unbounded rather than
falling back to a coarse cap.

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

#### Scenario: A buffered response slower than the former coarse cap still completes

- **WHEN** a non-streaming request's response takes longer than the previously hard-coded coarse client timeout but the configured first-byte budget has not elapsed
- **THEN** the bridge does not abort the call, and the complete buffered body is returned to the client.

#### Scenario: First-byte budget disabled

- **WHEN** the first-byte budget is configured to zero or less
- **THEN** the bridge imposes no first-byte inactivity bound and allocates no first-byte timer, and no coarse client-level timeout bounds the call in its place.

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
