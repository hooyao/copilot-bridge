## MODIFIED Requirements

### Requirement: Downstream keepalive during upstream silence

The bridge SHALL synthesize and flush a downstream-protocol-compatible keepalive whenever the upstream has produced no event for longer than a configured **keepalive interval** while relaying an SSE stream to an Anthropic-protocol client (`/cc`) or a Responses-protocol Codex client (`/codex`), and SHALL repeat one keepalive per elapsed interval for as long as the silence continues.

For `/cc`, the keepalive SHALL be a well-formed Anthropic `ping` event. For `/codex`, it SHALL be a complete SSE data event with `event: ping` and `data: {"type":"ping"}`. The Codex form MUST cause the client's parsed-event wait to complete and MUST be ignored as an unknown Responses event without changing response state. A comment-only frame such as `: ping` MUST NOT be used because Codex's event-source parser discards it before the timed event wait completes.

A keepalive SHALL NOT be placed before the stream's first upstream event, so that a stream which never starts remains governed by the first-byte and stream-idle budgets rather than being made to look started.

Injection SHALL be **silence-triggered, not periodic**: while upstream events arrive at gaps shorter than the interval, the bridge SHALL inject nothing and SHALL arm no keepalive timer beyond the shared pending-read deadline management. The presence of a keepalive in the relayed stream is itself evidence that upstream was silent.

The keepalive SHALL carry no usage, content, or response-state semantics: it SHALL NOT open, modify, or close a content/output item, SHALL NOT alter accumulated usage, and SHALL NOT change the order of upstream events. It is interleaved between them, never in place of one.

#### Scenario: Upstream goes silent mid-stream

- **WHEN** the upstream has emitted at least one event and then produces no event for longer than the keepalive interval
- **THEN** the bridge flushes one keepalive in the downstream client's protocol at approximately that interval
- **AND** continues flushing one keepalive per elapsed interval while the silence lasts
- **AND** when the upstream resumes, its next event is relayed unchanged and in order after the injected keepalives.

#### Scenario: Codex counts the complete ping event as activity

- **WHEN** a real Codex Responses stream remains upstream-silent longer than Codex's configured parsed-event idle timeout but shorter than the bridge's stream-idle budget
- **THEN** complete `ping` data events make Codex's timed next-event reads complete before their deadline
- **AND** Codex ignores the unknown `ping` type and continues the same response without changing semantic state.

#### Scenario: Upstream keeps emitting

- **WHEN** every gap between consecutive upstream events is shorter than the keepalive interval
- **THEN** the bridge injects no keepalive at all, and the downstream event sequence is exactly the relayed upstream sequence.

#### Scenario: Keepalives do not alter reported usage or content

- **WHEN** a stream containing injected keepalives completes on either downstream protocol
- **THEN** the usage the bridge reports is identical to the same upstream stream without injection
- **AND** no content block, output item, or response lifecycle state was opened, extended, or closed by a keepalive.

#### Scenario: Silence before the first upstream event

- **WHEN** the upstream has returned response headers but has not yet emitted any SSE event, and that silence exceeds the keepalive interval
- **THEN** the bridge injects no keepalive
- **AND** the phase remains governed by the first-byte and stream-idle budgets.

### Requirement: Keepalives do not extend the bridge's own timeout budget

A bridge-synthesized keepalive SHALL be treated as an event the bridge *sent downstream*, never as an event it *received upstream*. Injecting a keepalive on either client protocol SHALL NOT reset, extend, or suspend the stream-idle budget, which SHALL continue to measure the gap between genuine **upstream** events.

The shared timeout manager SHALL retain one pending upstream read across keepalive ticks and SHALL calculate the upstream-idle and downstream-keepalive deadlines from independent timestamps using the same monotonic clock sample. A keepalive win SHALL update only downstream activity. If both deadlines are equal, the upstream-idle deadline SHALL win.

This is the load-bearing constraint of the capability: keepalives make the client stop judging silence, so if they also fed the bridge's budget, nothing would be judging it and a genuinely hung upstream would stream pings forever.

#### Scenario: Silence longer than the stream-idle budget

- **WHEN** the keepalive interval is shorter than the stream-idle budget and the upstream stays silent past the stream-idle budget on `/cc` or `/codex`
- **THEN** the bridge injects keepalives during the silence
- **AND** the stream-idle budget still fires at approximately its configured duration measured from the last **upstream** event, not from the last injected keepalive
- **AND** the request summary records an upstream stream-idle timeout.

#### Scenario: Keepalive interval configured longer than the stream-idle budget

- **WHEN** the keepalive interval is greater than or equal to the stream-idle budget
- **THEN** the stream-idle budget wins before any keepalive is emitted, including an exact deadline tie
- **AND** the bridge surfaces the timeout exactly as it does without this capability.

#### Scenario: Pending upstream read survives a keepalive tick

- **WHEN** the keepalive deadline wins while one upstream move is still pending
- **THEN** the timeout manager reports a keepalive without cancelling or restarting that move
- **AND** the same move supplies the next upstream event or is cancelled and awaited only when the upstream-idle budget or client cancellation ends it.

### Requirement: The bridge decides when a stalled turn ends

The bridge SHALL be the party that ends a stalled turn. When the stream-idle budget fires while keepalives are being injected, the downstream failure surface SHALL remain selected by client protocol: `/cc` receives its configured retryable Anthropic error (or configured truncation), while `/codex` receives exactly one `response.failed` terminal.

The client SHALL NOT be the party that ends a healthy long-thinking turn: for any upstream silence shorter than the bridge's stream-idle budget, the relayed stream SHALL contain enough activity to keep the applicable client's idle watchdog from firing, provided the keepalive interval is configured shorter than that watchdog.

#### Scenario: Healthy deep-thinking turn outlives the client watchdog

- **WHEN** a real Claude Code or Codex client is streaming a turn and upstream silence lasts longer than that client's idle watchdog but less than the bridge's stream-idle budget, then resumes and completes
- **THEN** the client does not abort the stream
- **AND** the client receives the complete turn, including every upstream event emitted after the silence.

#### Scenario: Genuinely stalled upstream is still ended by the bridge

- **WHEN** the upstream goes silent and never resumes while downstream keepalives are enabled
- **THEN** the bridge, not the client, ends the turn when its stream-idle budget expires
- **AND** an Anthropic client receives the configured error/truncation while a Codex client receives one `response.failed`, rather than either client receiving an endless stream of keepalives.

### Requirement: Injected keepalives are distinguishable from upstream events

The bridge SHALL make it possible for an operator to tell a synthesized keepalive apart from an event Copilot actually sent on both downstream protocols. The captured raw upstream response SHALL contain only bytes Copilot sent, so it SHALL NOT contain injected keepalives; the record of what the client received SHALL mark bridge-originated keepalives without classifying a byte-identical upstream event as injected.

#### Scenario: Trace of a stream with injected keepalives

- **WHEN** tracing is enabled and the bridge injects keepalives into a `/cc` or `/codex` stream
- **THEN** the captured raw upstream response contains no injected keepalive
- **AND** the downstream event record marks each synthesized keepalive as bridge-originated
- **AND** an upstream event with identical text but a different origin remains marked as upstream.

### Requirement: Keepalive injection is configurable and disable-able

The keepalive interval SHALL be operator-configurable alongside the upstream inactivity budgets and SHALL apply to both streaming downstream protocols. A configured value of zero or less means the bridge injects no keepalive, arms no keepalive deadline, and incurs no per-stream overhead for this capability.

When disabled, each streaming relay SHALL be identical to its behavior without this capability except for behavior independently required by response translation and inspection.

#### Scenario: Keepalive disabled

- **WHEN** the keepalive interval is configured to zero or less and upstream goes silent on `/cc` or `/codex`
- **THEN** no keepalive is injected and no keepalive timer is allocated
- **AND** the downstream event sequence is identical to the behavior with this capability absent.

### Requirement: Client-side timeout configuration remains a second line of defence

Introducing keepalive injection SHALL NOT remove or relax the timeout-governing
values the bridge writes into Claude Code's configuration. The startup report SHALL
also read Codex's active provider timeout without rewriting it.

Keepalive injection is a *runtime* mitigation delivered per stream; the client
configuration is a *static* one. They cover different failure modes — a keepalive
that is never sent (injection disabled, an unconfigured intermediary, a bridge
that itself stalls) leaves only the client's own bound between a healthy turn and
an abort — so the two SHALL coexist. An undercut warning SHALL be emitted when the
client would fire first and live keepalive injection is ineffective. When pings are
reaching the client, the report SHALL identify the bridge as the runtime authority
rather than falsely warning that the refreshed client watchdog fires first.

#### Scenario: Client configuration is still written and still warned about

- **WHEN** the operator runs the Claude Code configuration command, or the bridge starts with a client bound shorter than its stream-idle budget while keepalive is off or inactive
- **THEN** Claude Code timeout values are written as before, and an unprotected undercut still produces the actionable warning
- **AND** an active keepalive is reported as runtime protection rather than as a client-first timeout.

## ADDED Requirements

### Requirement: Synthetic keepalives do not participate in native Responses fidelity groups

On a native `/codex` stream, a synthesized keepalive SHALL bypass the private semantic sequence used to authorize restoration of each original upstream Responses event. It SHALL be emitted between fidelity groups without consuming an upstream ordinal, altering an expected semantic sequence, or causing a clean stream to fail closed. Every genuine upstream event before and after it SHALL retain its original order and full JSON value fidelity.

#### Scenario: Ping between two native upstream events

- **WHEN** one original Responses event is restored, upstream is silent long enough for a keepalive, and a second original Responses event then arrives
- **THEN** Codex receives the first original event, one bridge-originated `ping`, and the second original event in that order
- **AND** both original event payloads remain value-identical to upstream
- **AND** the keepalive does not cause a synthetic `response.failed` or consume a native ledger ordinal.

#### Scenario: Detector withholding overlaps the silence

- **WHEN** response inspection is withholding a scannable semantic block while a native Codex keepalive becomes due
- **THEN** the keepalive is relayed immediately rather than buffered with the block
- **AND** the withheld upstream semantics and their native carrier are later processed together under the existing fidelity decision.
