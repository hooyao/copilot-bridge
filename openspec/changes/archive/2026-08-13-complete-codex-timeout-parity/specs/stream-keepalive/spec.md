## ADDED Requirements

### Requirement: Client timeout policy remains independent from keepalive

Keepalive injection SHALL coexist with whatever timeout configuration the user
selected in each client, but introducing or configuring keepalive SHALL NOT
authorize the bridge to add, derive, clamp, or overwrite a client timeout.

Keepalive is a runtime downstream activity mechanism, not a timeout policy. It can
refresh a client idle watchdog only after the first genuine upstream event and
only while complete ping events reach the downstream socket. It SHALL never reset
the bridge's upstream-event idle deadline. If injection is disabled, whole-response
buffering prevents delivery, the ping interval cannot precede the client watchdog,
or the bridge itself stalls, the user's client timeout remains the independent
fallback.

Startup SHALL report these conditions without changing either configuration. When
keepalive is effective it SHALL say that the client idle watchdog is being
refreshed for the post-start event-gap phase; it SHALL NOT say that the client
timeout was removed or that the whole turn has a bridge-owned deadline. When it is
ineffective, equal bridge/client deadlines SHALL be reported as a race.

#### Scenario: Connection commands preserve static client defence

- **WHEN** either client has an explicit idle timeout and the operator runs its bridge connection command
- **THEN** the client timeout remains unchanged
- **AND** runtime keepalive settings remain independent.

#### Scenario: Active post-start keepalive refreshes the client only

- **WHEN** a stream has emitted its first upstream event and the bridge sends pings during later upstream silence
- **THEN** the client idle watchdog is refreshed
- **AND** the bridge stream-idle deadline continues from the last genuine upstream event.

#### Scenario: First-event wait is not described as keepalive-protected

- **WHEN** upstream response headers arrived but no upstream SSE event has yet been parsed
- **THEN** no synthetic ping is sent
- **AND** startup documentation does not claim that keepalive protects that phase.

#### Scenario: Buffering prevents runtime protection

- **WHEN** the active detector set eagerly buffers the complete streaming response
- **THEN** no ping reaches the client while the buffer is being filled
- **AND** the report identifies client keepalive protection as inactive for that path.

#### Scenario: Equal unprotected deadlines are a race

- **WHEN** bridge and client idle values are equal and keepalive is ineffective
- **THEN** the report says both deadlines race at that value
- **AND** it does not select the bridge as the deterministic winner.

## REMOVED Requirements

### Requirement: Client-side timeout configuration remains a second line of defence

**Reason**: The earlier requirement still authorized the bridge to write a derived
client timeout. Runtime keepalive and static client policy are now independent.

**Migration**: Use `Client timeout policy remains independent from keepalive`;
existing client timeout values are preserved and startup reports the relationship.

#### Scenario: Legacy keepalive-driven timeout write is retired

- **WHEN** keepalive is enabled or reconfigured
- **THEN** no client timeout value is added, derived, clamped, or overwritten.

## MODIFIED Requirements

### Requirement: The bridge decides when a stalled turn ends

During a post-first-event gap where complete keepalives reach the client before
its watchdog, the bridge SHALL remain the party that ends a genuinely stalled
upstream. When the stream-idle budget fires, `/cc` SHALL receive its configured
retryable Anthropic error (or configured truncation), while `/codex` SHALL receive
exactly one `response.failed` terminal.

This authority is conditional. It SHALL NOT be extended to the first-event wait,
whole-response buffering, disabled/late keepalive, or a bridge process that cannot
deliver the ping; in those cases the client watchdog remains an independent bound.

#### Scenario: Healthy deep-thinking turn outlives the client watchdog

- **WHEN** a real Claude Code or Codex client has received the first upstream event, complete pings reach it before its watchdog, and upstream silence lasts longer than that watchdog but less than the bridge's stream-idle budget, then resumes and completes
- **THEN** the client does not abort the stream
- **AND** the client receives the complete turn, including every upstream event emitted after the silence.

#### Scenario: Genuinely stalled upstream is still ended by the bridge

- **WHEN** the upstream goes silent and never resumes while downstream keepalives are enabled
- **THEN** the bridge, not the client, ends the turn when its stream-idle budget expires
- **AND** an Anthropic client receives the configured error/truncation while a Codex client receives one `response.failed`, rather than either client receiving an endless stream of keepalives.
