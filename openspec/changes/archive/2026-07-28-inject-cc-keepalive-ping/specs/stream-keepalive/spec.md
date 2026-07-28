## ADDED Requirements

### Requirement: Downstream keepalive during upstream silence

The bridge SHALL synthesize and flush a protocol-valid keepalive event to an
Anthropic-protocol client (`/cc`) whenever the upstream has produced no event for
longer than a configured **keepalive interval** while relaying an SSE stream, and
SHALL repeat one keepalive per elapsed interval for as long as the silence
continues.

The keepalive SHALL be a well-formed Anthropic `ping` event — an event the
Anthropic Messages streaming protocol defines as carrying no content and being
valid at any point between `message_start` and `message_stop`. It SHALL NOT be
placed before the stream's first upstream event, so that a stream which never
starts remains governed by the first-byte budget rather than being made to look
started.

Injection SHALL be **silence-triggered, not periodic**: while upstream events
arrive at gaps shorter than the interval, the bridge SHALL inject nothing and
SHALL arm no keepalive timer. The purpose is that the presence of a keepalive in
the relayed stream is itself evidence that upstream was silent.

The keepalive SHALL carry no usage, content, or message-state semantics: it SHALL
NOT open, modify, or close a content block, SHALL NOT alter the accumulated usage
the bridge reports for the request, and SHALL NOT change the sequence of upstream
events the client receives — it is interleaved between them, never in place of
one.

#### Scenario: Upstream goes silent mid-stream

- **WHEN** the upstream has emitted at least one event and then produces no event for longer than the keepalive interval
- **THEN** the bridge flushes one `ping` event to the client at approximately that interval
- **AND** continues flushing one `ping` per elapsed interval while the silence lasts
- **AND** when the upstream resumes, its next event is relayed unchanged and in order after the injected pings.

#### Scenario: Upstream keeps emitting

- **WHEN** every gap between consecutive upstream events is shorter than the keepalive interval
- **THEN** the bridge injects no keepalive at all, and the downstream event sequence is exactly the relayed upstream sequence.

#### Scenario: Keepalives do not alter reported usage or content

- **WHEN** a stream containing injected keepalives completes
- **THEN** the usage the bridge reports for the request is identical to the usage it would report for the same upstream stream without injection
- **AND** no content block was opened, extended, or closed by a keepalive.

#### Scenario: Silence before the first upstream event

- **WHEN** the upstream has returned response headers but has not yet emitted any SSE event, and that silence exceeds the keepalive interval
- **THEN** the bridge injects no keepalive
- **AND** the phase remains governed by the first-byte and stream-idle budgets.

### Requirement: Keepalives do not extend the bridge's own timeout budget

A bridge-synthesized keepalive SHALL be treated as an event the bridge *sent
downstream*, never as an event it *received upstream*. Injecting a keepalive
SHALL NOT reset, extend, or suspend the stream-idle budget, which SHALL continue
to measure the gap between genuine **upstream** events.

This is the load-bearing constraint of the capability: keepalives make the client
stop judging silence, so if they also fed the bridge's own budget, nothing would
be judging it and a genuinely hung upstream would stream pings forever.

#### Scenario: Silence longer than the stream-idle budget

- **WHEN** the keepalive interval is shorter than the stream-idle budget and the upstream stays silent past the stream-idle budget
- **THEN** the bridge injects keepalives during the silence
- **AND** the stream-idle budget still fires at approximately its configured duration measured from the last **upstream** event, not from the last injected keepalive
- **AND** the request summary records an upstream stream-idle timeout.

#### Scenario: Keepalive interval configured longer than the stream-idle budget

- **WHEN** the keepalive interval is greater than or equal to the stream-idle budget
- **THEN** the stream-idle budget fires before any keepalive would be due, and the bridge surfaces the timeout exactly as it does without this capability.

### Requirement: The bridge decides when a stalled turn ends

The bridge SHALL be the party that ends a stalled turn. When the stream-idle
budget fires on a `/cc` stream in which the bridge has been injecting keepalives,
the bridge SHALL end the turn by the same surface it uses without keepalives: by
default injecting the retryable `overloaded_error` event and ending the stream, or
ending as a plain truncation when the operator has configured that.

The client SHALL NOT be the party that ends a healthy long-thinking turn: for any
upstream silence shorter than the bridge's stream-idle budget, the relayed stream
SHALL contain enough activity to keep an Anthropic client's idle watchdogs from
firing, provided the keepalive interval is configured shorter than the client's
watchdog bound.

#### Scenario: Healthy deep-thinking turn outlives the client watchdog

- **WHEN** a real Claude Code client with its default idle watchdog bound is streaming a turn, and the upstream goes silent for longer than that bound but less than the bridge's stream-idle budget, then resumes and completes
- **THEN** the client does not abort the stream
- **AND** the client receives the complete turn, including every upstream event emitted after the silence.

#### Scenario: Genuinely stalled upstream is still ended by the bridge

- **WHEN** the upstream goes silent and never resumes
- **THEN** the bridge — not the client — ends the turn when its stream-idle budget expires
- **AND** the client receives the configured retryable error event (or a truncation), not an endless stream of keepalives.

### Requirement: Injected keepalives are distinguishable from upstream events

The bridge SHALL make it possible for an operator to tell a synthesized keepalive
apart from an event Copilot actually sent. The captured raw upstream response
SHALL contain only bytes Copilot sent, so it SHALL NOT contain injected
keepalives; the record of what the client received SHALL either include them
marked as bridge-originated or otherwise let an operator distinguish them.

Without this, the capability would destroy the diagnostic it was built around: a
silent upstream would no longer be observably silent, and a genuine hang would be
indistinguishable from deep thinking in the trace.

#### Scenario: Trace of a stream with injected keepalives

- **WHEN** tracing is enabled and the bridge injects keepalives into a `/cc` stream
- **THEN** the captured raw upstream response contains no injected keepalive
- **AND** the record of the downstream stream allows the injected keepalives to be identified as bridge-originated.

### Requirement: Keepalive injection is configurable and disable-able

The keepalive interval SHALL be operator-configurable alongside the upstream
inactivity budgets, and SHALL be disable-able: a configured value of zero or less
means the bridge injects no keepalive, arms no keepalive timer, and incurs no
per-stream overhead for this capability.

When disabled, the `/cc` streaming relay SHALL be byte-identical to its behavior
without this capability.

#### Scenario: Keepalive disabled

- **WHEN** the keepalive interval is configured to zero or less and upstream goes silent
- **THEN** no keepalive is injected and no keepalive timer is allocated
- **AND** the downstream event sequence is byte-identical to the behavior with this capability absent.

### Requirement: Client-side timeout configuration remains a second line of defence

Introducing keepalive injection SHALL NOT remove or relax the timeout-governing
values the bridge writes into the client's configuration, nor the startup warning
that fires when a client bound would undercut a bridge budget.

Keepalive injection is a *runtime* mitigation delivered per stream; the client
configuration is a *static* one. They cover different failure modes — a keepalive
that is never sent (injection disabled, an unconfigured intermediary, a bridge
that itself stalls) leaves only the client's own bound between a healthy turn and
an abort — so the two SHALL coexist.

#### Scenario: Client configuration is still written and still warned about

- **WHEN** keepalive injection is enabled and the operator runs the client configuration command, or the bridge starts with a client bound shorter than its stream-idle budget
- **THEN** the client timeout values are written as before, and the undercut warning is emitted as before.
