## ADDED Requirements

### Requirement: Effective end-to-end timeout is reported at startup

The bridge SHALL report, once at startup, the **effective end-to-end timeout** for
a long-thinking turn — the shortest inactivity bound that will actually fire
across the chain — derived from both sides: its own configured upstream budgets
(`Pipeline:UpstreamTimeout`) and the timeout-governing environment values stored
in Claude Code's settings file. The report SHALL name each contributing bound and
its source, so an operator can see the real limit without reverse-engineering it
from two separate config files.

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
- **THEN** the startup report names the bridge's own budgets as the effective bound
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
covers two cases, which SHALL be treated alike because both produce the same
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

#### Scenario: Client request timeout undercuts the bridge first-byte budget

- **WHEN** the client's stored whole-request timeout is shorter than the bridge's configured first-byte budget
- **THEN** the bridge emits a warning identifying that key, both values, and both remedies.

#### Scenario: A missing client timeout key is warned about, not passed over

- **WHEN** the client settings are readable and pointed at this bridge but a timeout key is absent
- **THEN** the bridge emits the same undercut warning, naming the default bound that applies in its absence and both remedies
- **AND** it does not report that key merely as "unknown".

#### Scenario: No warning when the client outlasts the bridge

- **WHEN** every client timeout value is present and greater than or equal to the corresponding bridge budget
- **THEN** no undercut warning is emitted.
