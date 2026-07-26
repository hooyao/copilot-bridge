## ADDED Requirements

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
