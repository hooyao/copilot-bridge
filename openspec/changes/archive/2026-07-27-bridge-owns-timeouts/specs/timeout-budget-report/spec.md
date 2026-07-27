## ADDED Requirements

### Requirement: Timeout bounds are reported per phase at startup

The bridge SHALL report, once at startup, which timeout bounds apply to a
long-thinking turn, from both sides: its own configured upstream budgets
(`Pipeline:UpstreamTimeout`) and the timeout-governing environment values stored
in Claude Code's **global** settings file. The report SHALL name each contributing
bound and its source, so an operator can see the limits without
reverse-engineering them from two separate config files.

Bounds SHALL be reported **per phase**, and the report SHALL NOT reduce them to a
single minimum. They do not compete over the same interval: the first-byte budget
is disarmed the moment response headers arrive, the stream-idle bounds then govern
each silent gap, and the client's whole-request cap is wall-clock across all of
them. A single minimum would therefore misreport the real exposure — with a 60 s
first-byte budget and a 600 s stream-idle budget it would announce 60 s for a turn
whose exposure after headers is 600 s.

The report SHALL NOT present that value as the definitive end-to-end bound. Only
global client settings are readable from startup: a project-scoped
`settings.local.json` overrides them and belongs to the Claude session's own
directory, which a bridge serving many repositories cannot identify. The report
SHALL therefore label the value as coming from global settings and state that a
project-scoped override may be shorter and is not visible — labelling it
"effective" would make the diagnostic confidently wrong for exactly those users.

Reading the client settings SHALL be **best-effort and non-fatal**: a missing,
unreadable, or malformed client settings file, or a file not pointed at this
bridge, SHALL NOT fail startup or suppress the report. In that case the bridge
SHALL report its own budgets and state that the client-side values are unknown.
"Unknown" SHALL apply only when the file itself could not be read or does not
concern this bridge — a readable, bridge-pointed file that simply lacks a
timeout key is a known-bad configuration, not an unknown one, and is covered by
the warning requirement below.

A bridge budget that is disabled (configured zero or less) SHALL be reported as
imposing no bound, and SHALL NOT be treated as the shortest bound.

#### Scenario: Client is configured to outlast the bridge

- **WHEN** the bridge starts and Claude Code's settings hold timeout values that are all longer than the bridge's configured budgets
- **THEN** the startup report names the bridge's own budgets for each phase they govern
- **AND** the report is emitted exactly once, at startup, and startup proceeds normally.

#### Scenario: Client settings are absent or unreadable

- **WHEN** the bridge starts and Claude Code's settings file does not exist, cannot be read, or is not parseable
- **THEN** startup completes successfully
- **AND** the report still states the bridge's own budgets, marking the client-side contribution as unknown rather than omitting the report or failing.

#### Scenario: A disabled bridge budget is not counted as the shortest bound

- **WHEN** a bridge inactivity budget is configured to zero or less
- **THEN** the report describes that budget as imposing no bound
- **AND** the effective bound is derived from the remaining bounds rather than from the disabled one.

### Requirement: A client watchdog that would fire first is warned about

The bridge SHALL emit a startup warning whenever Claude Code's configuration
would abort a turn before the bridge's own inactivity budget applies. This
applies to the client bound that is DERIVED from a budget (the streaming idle
key); the wall-clock whole-request cap is covered separately below, because no
finite value of it can be guaranteed to outlast an inactivity budget. It covers
two cases, which SHALL be treated alike because both produce the same
outcome — the client kills a healthy in-progress turn and the bridge's budget
never gets to decide:

1. A stored timeout value **shorter** than the bridge budget it is meant to
   outlast.
2. A **missing** value. Absence is not benign: with the bridge's own
   `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` key in effect, the client falls
   back to a first-party default measurably shorter than a deep-thinking turn
   needs. Absence is also the state of every installation configured before this
   capability existed, so it is the common case rather than an edge case.

The warning SHALL name the offending key, the effective client bound (the stored
value, or the known default that applies when absent), and the bridge budget it
undercuts.

The warning SHALL state **both** ways to fix it, so the operator can choose:

- run the bridge's Claude Code configuration command, which writes the derived
  values; or
- set the environment variable themselves to at least the named value.

When every client value is present and outlasts the corresponding bridge budget,
the bridge SHALL NOT emit that warning.

#### Scenario: Client idle watchdog undercuts the bridge stream-idle budget

- **WHEN** the client's stored stream-idle value is shorter than the bridge's configured stream-idle budget
- **THEN** the bridge emits a warning identifying that key, both values, and both remedies — the configuration command and the environment variable the operator can set directly
- **AND** startup proceeds — the warning does not abort the bridge.


#### Scenario: A missing client timeout key is warned about, not passed over

- **WHEN** the client settings are readable and pointed at this bridge but a timeout key is absent
- **THEN** the bridge emits the same undercut warning, naming the default bound that applies in its absence and both remedies
- **AND** it does not report that key merely as "unknown".

#### Scenario: No warning when the client outlasts the bridge

- **WHEN** every client timeout value is present and greater than or equal to the corresponding bridge budget
- **THEN** no undercut warning is emitted.


### Requirement: The client's wall-clock cap is reported as a residual bound

The bridge SHALL report Claude Code's whole-request timeout (`API_TIMEOUT_MS`) as
a **residual wall-clock bound** — one the bridge cannot out-wait — rather than as
a value that outlasts its budgets.

The distinction is not cosmetic. The bridge's budgets bound *inactivity*, so a
healthy turn that keeps emitting has no total duration at all, and a stalled turn
may legitimately consume the first-byte budget and then one or more stream-idle
gaps before any bridge timer fires. No finite wall-clock value can therefore be
guaranteed to outlast them, and any derivation that implies otherwise is false.

Accordingly the bridge SHALL NOT warn that this key "undercuts" a budget: such a
warning would fire on correct configurations and no value would silence it.

#### Scenario: A client whole-request cap shorter than a bridge budget

- **WHEN** the client's stored whole-request timeout is shorter than a bridge inactivity budget
- **THEN** the bridge reports it as a wall-clock cap that ends the turn at the client regardless of the budgets
- **AND** emits no undercut warning for that key.

#### Scenario: The written value is not presented as a guarantee

- **WHEN** the bridge reports the whole-request cap
- **THEN** the report does not state or imply that the client is guaranteed to outlast the bridge on that bound.

#### Scenario: The reported bound is not labelled as definitive

- **WHEN** the bridge reports the shortest bound it can see from its budgets and the global client settings
- **THEN** the report identifies the client contribution as coming from global settings
- **AND** states that a project-scoped override may be shorter and is not visible from startup.

#### Scenario: Bounds governing different phases are not collapsed into one number

- **WHEN** the first-byte budget and the stream-idle budget differ
- **THEN** the report states each against the phase it governs
- **AND** it does not present a single minimum across them as the bound for the turn.
