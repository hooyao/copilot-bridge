## ADDED Requirements

### Requirement: Configured and effective durations are separate facts

The startup report SHALL print normal durations in concise human units such as
`15s`, `5m`, and `10m`; it SHALL NOT repeat storage forms such as `300000ms` when
`5m` is exact. An explicit configured duration, a missing key, a built-in default,
and a known client-effective duration after a floor/cap SHALL remain
distinguishable.

The bridge SHALL apply only client interpretation rules grounded in the installed
or explicitly named client version/current source. If it cannot establish a
version-sensitive rule, it SHALL print the configured human duration and label
effective behavior unknown/version-dependent rather than guessing. It SHALL NOT
write an interpreted value back to the client. Raw storage values remain available
to detailed status/diagnostic output, not the normal startup inventory.

#### Scenario: Explicit value uses a human duration

- **WHEN** a client stores `900000` milliseconds
- **THEN** startup shows `15m, explicit`
- **AND** does not redundantly show `900000ms`.

#### Scenario: Missing value uses a named default

- **WHEN** a source-confirmed client key is genuinely absent
- **THEN** the report shows `unset -> <duration>*`
- **AND** the legend identifies `*` as a client default.

#### Scenario: Configured value differs from known effective value

- **WHEN** a known client floor or cap changes the stored value's effect
- **THEN** the report prints both human durations and names the floor/cap, for example `configured 1m -> effective 5m`.

#### Scenario: Invalid value is not ordinary absence

- **WHEN** a key is present but malformed or semantically invalid
- **THEN** the report identifies that configured state as invalid/unknown
- **AND** does not silently present it as a normally absent default.

### Requirement: Retry and attempt scopes are reported

The startup report SHALL distinguish values that apply to one bridge send, one
client request attempt, one stream attempt, or one SSE event gap. It SHALL print
configured/default retry counts wherever the global client configuration makes
them knowable, but SHALL NOT expand those counts into additional-attempt
arithmetic in the startup console.

For the bridge it SHALL report the configured transient network retry count. For
Codex it SHALL report raw `request_max_retries` and `stream_max_retries` counts.
For Claude Code it SHALL report retry count as unknown when startup cannot derive
it from the inspected global file/client version. One concise footer SHALL state
that timeouts apply per attempt, retry starts a new attempt, and no fixed
whole-turn limit follows. Detailed total-attempt formulas remain in documentation.

#### Scenario: Codex defaults expose retry counts

- **WHEN** the global bridge provider omits both retry keys
- **THEN** the report shows four built-in request retries and five built-in stream retries
- **AND** does not add total-attempt arithmetic to either line.

#### Scenario: Explicit zero retry is preserved

- **WHEN** either Codex retry key is explicitly zero
- **THEN** the report shows zero retries
- **AND** does not replace zero with a default.

#### Scenario: Bridge timeout ends only one retryable attempt

- **WHEN** the bridge emits a retryable timeout terminal and the client can retry it
- **THEN** the scope note states that one attempt ended
- **AND** does not label that timeout as the whole-turn deadline.

#### Scenario: Unknown Claude retry policy remains unknown

- **WHEN** the effective Claude retry count cannot be obtained from the global file and established client facts
- **THEN** the report prints `not visible at bridge startup`
- **AND** makes no whole-turn maximum claim.

### Requirement: Global client configuration visibility is explicit

The startup report SHALL use global client files as an observable baseline and
SHALL label them `global only`. It SHALL state that Claude Code repo/process-env
overrides and Codex project/profile/CLI overrides are not included. It SHALL NOT
call global values definitive or attempt to resolve one project directory for a
long-running multi-project bridge.

#### Scenario: Codex global baseline is labelled

- **WHEN** global `~/.codex/config.toml` is readable
- **THEN** the Codex heading names that path and says `global only`
- **AND** the footer lists project/profile/CLI overrides as invisible.

#### Scenario: Project override may exist

- **WHEN** a project `.codex/config.toml` contains a higher-precedence value
- **THEN** startup continues to show only the global baseline
- **AND** does not claim that the global value is what that project client will use.

#### Scenario: Claude global baseline is labelled

- **WHEN** global Claude settings are readable
- **THEN** the Claude heading names that path and says `global only`
- **AND** repo settings and process environment are listed as invisible.

### Requirement: Timeout configuration inventory is reported per phase at startup

The bridge SHALL emit one startup inventory headed
`Timeouts (observed configuration; startup does not rewrite values)`. It SHALL
contain a Bridge section, a Claude Code global-only section, a Codex global-only
section, one concise attempt/turn note, one visibility line, and a default legend.

The Bridge section SHALL report:

- upstream response-header timeout per send attempt (the option historically
  named `FirstByteTimeoutSeconds`);
- upstream parsed-SSE-event gap per gap;
- downstream keepalive interval and its after-first-event boundary;
- transient network retry count;
- the absence of a buffered-body timeout after upstream headers.

The Claude section SHALL use the exact labels `SSE event idle` for
`CLAUDE_STREAM_IDLE_TIMEOUT_MS` and `SSE byte idle` for
`CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS`, followed by request timeout in plain language
(`normal` and `after stream error` where absent defaults differ) and retry
visibility. The Codex section SHALL use the exact label `SSE event idle` for
`stream_idle_timeout_ms`, followed by request retries, stream retries, and the
absence of a whole-request cap.

The startup console SHALL NOT contain a multi-line calculated section. Detailed
header-plus-first-event, keepalive-authority, buffered-body, and retry arithmetic
belong in `docs/timeout-chain.md`. The console's single note SHALL communicate only
the load-bearing scope fact: timeouts apply per attempt, retries start new
attempts, and therefore there is no fixed whole-turn limit.

Reading either global client file SHALL remain best-effort and non-fatal. An
unreadable client retains an `unknown` section and does not suppress other facts.

#### Scenario: Client idle labels expose equivalent and additional layers

- **WHEN** both client sections are rendered
- **THEN** Claude Code contains `SSE event idle` and `SSE byte idle`
- **AND** Codex contains `SSE event idle` but no fabricated byte-idle row.

#### Scenario: Complete inventory is emitted once

- **WHEN** the bridge starts with readable global client files
- **THEN** all three source sections, the concise scope note, caveat, and legend appear exactly once
- **AND** startup proceeds normally.

#### Scenario: Upstream headers are named precisely

- **WHEN** the bridge header budget and stream-idle budget are both four minutes
- **THEN** the bridge source line says `upstream response headers 4m / send attempt`
- **AND** the startup console does not add a header-plus-first-event formula.

#### Scenario: Buffered body hole is visible

- **WHEN** response headers arrive and a true buffered body is then read
- **THEN** the Bridge section says `buffered body              no limit after headers`
- **AND** detailed mode arithmetic remains in the timeout reference rather than the startup console.

#### Scenario: Codex has no whole-request timeout

- **WHEN** the Codex section is rendered
- **THEN** it says `whole request              no limit`
- **AND** no invented timeout key/default appears.

#### Scenario: Client file is unavailable

- **WHEN** one global client file is missing, malformed, unreadable, or not bridge-pointed
- **THEN** only that section is unknown with a concise reason
- **AND** the Bridge and other client sections remain complete.

#### Scenario: Disabled bridge phase is explicit

- **WHEN** a bridge budget is zero or negative
- **THEN** its source line says disabled/no bound using the configured value
- **AND** no substitute duration is calculated.

### Requirement: Observed client watchdog races and undercuts are reported

The bridge SHALL warn when the observed global client idle watchdog would end the
relevant phase before the bridge deadline and effective keepalive cannot refresh
it. The warning is observational: it SHALL name the client source, configured and
known effective human values, bridge source/value, phase scope, and the global-only caveat. It
SHALL NOT direct the operator to a connection command that rewrites timeout values.

When post-start keepalive is effective, the report SHALL say that the client idle
watchdog is refreshed for that phase while the bridge event-gap deadline continues.
It SHALL NOT extend that statement to the pre-first-event wait, buffered delivery,
whole request, or whole turn. Equal deadlines without effective keepalive SHALL
be reported as a race.

Codex global provider recognition still requires matching provider name,
`/codex` base URL suffix, and `wire_api = "responses"`. A present invalid timeout
is not replaced by the built-in default; defaults apply only to genuine absence.

#### Scenario: Observed client idle is shorter

- **WHEN** a globally observed client idle value is shorter than the bridge event-gap budget and keepalive is ineffective
- **THEN** the report warns that the client can end that attempt first
- **AND** names the client file/key rather than a bridge-derived replacement.

#### Scenario: Equal values without protection race

- **WHEN** bridge and client idle values are equal and keepalive is ineffective
- **THEN** the report says `race at <value>`
- **AND** does not choose the bridge as winner.

#### Scenario: Active pings suppress only the applicable warning

- **WHEN** pings are delivered after the first upstream event before the client idle watchdog
- **THEN** no post-start event-gap undercut warning is emitted
- **AND** the report still retains pre-first-event, buffering, request-cap, and retry caveats.

#### Scenario: Connection command is not remediation

- **WHEN** an undercut is reported
- **THEN** the warning tells the operator to review the named native client/bridge settings
- **AND** does not claim `config claude-code` or `config codex` will choose a timeout.

### Requirement: Client request caps are reported as attempt-scoped residual bounds

The bridge SHALL report Claude Code's request timeout in one plain-language line.
When the key is absent and the source-confirmed defaults differ, that line SHALL
say `normal <value>; after stream error <value>`. An explicit `API_TIMEOUT_MS`
that governs both SHALL be shown once as a human duration, not as a bridge-owned
value.

These request caps run concurrently with bridge inactivity phases but do not
become a whole-turn cap when client retries can start another request. The report
SHALL therefore state which attempt they bound and SHALL NOT claim they outlast or
are derived from bridge budgets. Codex SHALL explicitly report no whole-request
bound.

#### Scenario: Claude timeout key is absent

- **WHEN** global Claude settings omit `API_TIMEOUT_MS`
- **THEN** one request-timeout line shows the source-confirmed `normal` default and distinct `after stream error` default
- **AND** marks both as built-in values.

#### Scenario: Claude timeout key is explicit

- **WHEN** global Claude settings contain a valid explicit `API_TIMEOUT_MS`
- **THEN** the exact human duration appears once on the request-timeout line
- **AND** it is labelled per request attempt, not per turn.

#### Scenario: No whole-turn minimum is fabricated

- **WHEN** any retry layer can restart a request/stream attempt
- **THEN** the scope note says there is no fixed whole-turn limit
- **AND** no minimum of unrelated phase values is presented as the turn deadline.

## REMOVED Requirements

### Requirement: Timeout bounds are reported per phase at startup

**Reason**: The earlier table described derived client values and winner
calculations as though they were authoritative turn bounds. The replacement is an
observed, source-labelled configuration inventory.

**Migration**: Use `Timeout configuration inventory is reported per phase at
startup`; detailed phase composition remains in `docs/timeout-chain.md`.

#### Scenario: Legacy winner table is retired

- **WHEN** the bridge starts
- **THEN** it prints the source-labelled inventory instead of a derived turn winner.

### Requirement: A client watchdog that would fire first is warned about

**Reason**: The earlier requirement assumed bridge-managed Claude values and did
not cover Codex, byte idle, equality races, or the pre-first-event keepalive gap.

**Migration**: Use `Observed client watchdog races and undercuts are reported`;
review the named native client key and bridge setting rather than rerunning a
connection command.

#### Scenario: Legacy managed-timeout remediation is retired

- **WHEN** an observed client watchdog can undercut a bridge phase
- **THEN** the warning names the native settings rather than prescribing a connection command.

### Requirement: The client's wall-clock cap is reported as a residual bound

**Reason**: The earlier wording did not distinguish normal and after-stream-error
request attempts or make retry resets explicit.

**Migration**: Use `Client request caps are reported as attempt-scoped residual
bounds`; no client cap is presented as a whole-turn guarantee.

#### Scenario: Legacy whole-request interpretation is retired

- **WHEN** retries can start a new request or stream attempt
- **THEN** the report does not present one attempt cap as a whole-turn deadline.
