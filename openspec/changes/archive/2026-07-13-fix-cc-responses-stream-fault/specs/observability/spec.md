## ADDED Requirements

### Requirement: Upstream stream faults are recorded at the client boundary

When an upstream stream faults after response headers have been sent, the bridge SHALL
record that fault on the request summary and trace artifacts owned by the
downstream endpoint, regardless of whether an upstream strategy, an outbound
adapter, or the endpoint itself first observes the fault. A caught fault SHALL
NOT be reported as a clean streaming `200` with `error=(none)`.

If the fault is an inactivity timeout, the summary SHALL record the timeout phase
as `upstream_timeout=stream_idle`. The summary error and trace error field SHALL
identify the upstream fault without including prompt or response content. The
wire status SHALL remain the already-started status.

#### Scenario: Claude Code-to-Responses timeout is visible in the summary

- **WHEN** a `/cc` request routed to a Responses backend hits the stream-idle timeout after downstream response headers have started
- **THEN** the summary retains status `200` and records `upstream_timeout=stream_idle`
- **AND** its error field identifies the upstream timeout instead of reporting `(none)`.

#### Scenario: Claude Code-to-Responses timeout is visible in trace artifacts

- **WHEN** tracing is enabled and a `/cc` request routed to a Responses backend faults after receiving partial upstream bytes
- **THEN** `upstream-resp` retains the exact partial Responses bytes and carries the fault in its error field
- **AND** `inbound-resp` records the client-protocol error or truncation actually delivered.

#### Scenario: Successful cross-routed stream remains a clean success

- **WHEN** a `/cc` request routed to a Responses backend reaches its normal upstream terminal and no fault occurs
- **THEN** the request summary and trace artifacts remain clean and contain no synthesized upstream error.
