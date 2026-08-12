## MODIFIED Requirements

### Requirement: Timeout bounds are reported per phase at startup

The bridge SHALL report, once at startup, which timeout bounds apply to long-thinking turns for each supported downstream client. It SHALL report its configured upstream budgets (`Pipeline:UpstreamTimeout`) alongside:

- the timeout-governing values stored in Claude Code's global settings; and
- the active global Codex provider's `stream_idle_timeout_ms`, using Codex's source-confirmed 300,000 ms default when the bridge provider is active and the key is absent.

The report SHALL identify the client and source of each value. Claude Code and Codex SHALL have separate idle-gap rows because their configuration files, defaults, and additional wall-clock bounds differ.

Bounds SHALL be reported per phase and SHALL NOT be collapsed into one global minimum. The first-byte budget is disarmed when response headers arrive; stream-idle bounds govern each later silent gap; Claude Code's whole-request cap is wall-clock across all phases.

The idle-gap termination calculation SHALL account for runtime keepalive management. When keepalive injection can reach the live stream (the interval is positive and strictly shorter than a positive bridge stream-idle budget, and whole-response buffering is disabled), the report SHALL identify the bridge stream-idle budget as the party that ends an upstream-silent gap: repeated downstream pings refresh the client watchdog without resetting the bridge budget. When keepalive is off or inactive, the report SHALL identify the shorter active numeric bound. A disabled bridge budget SHALL be described as imposing no bound and SHALL NOT be treated as zero.

Reading either client configuration SHALL be best-effort and non-fatal. A missing, unreadable, malformed, or non-bridge-pointed file SHALL NOT fail startup or suppress the other report rows; its client contribution SHALL be shown as unknown.

#### Scenario: Client is configured to outlast the bridge

- **WHEN** the bridge starts with readable global Claude Code and Codex configurations pointed at it
- **THEN** the startup report names separate Claude Code and Codex idle bounds and their sources
- **AND** names the bridge first-byte and stream-idle budgets for the phases they govern
- **AND** the report is emitted exactly once and startup proceeds normally.

#### Scenario: Codex uses its default provider idle timeout

- **WHEN** the active Codex provider is `copilot-bridge` and omits `stream_idle_timeout_ms`
- **THEN** the Codex row reports 300 seconds as a client default rather than unknown
- **AND** identifies it as a default, not an explicit stored value.

#### Scenario: Client settings are absent or unreadable

- **WHEN** either global client config does not exist, cannot be parsed, or does not select the bridge
- **THEN** startup completes successfully and the bridge plus other readable client rows remain present
- **AND** only the unavailable client contribution is marked unknown with a concise reason.

#### Scenario: Active keepalive makes the bridge authoritative

- **WHEN** a client's numeric idle watchdog is shorter than the bridge stream-idle budget but live keepalive injection is effective
- **THEN** that client's idle-gap row identifies the bridge budget as the termination authority
- **AND** does not warn that the refreshed client watchdog numerically undercuts the bridge.

#### Scenario: Inactive keepalive exposes the shorter watchdog

- **WHEN** keepalive is disabled, cannot reach the client because responses are whole-buffered, or is not shorter than the bridge idle budget
- **THEN** each idle-gap row identifies the shorter active bridge/client numeric bound
- **AND** existing actionable warnings remain applicable where the bridge manages the client setting.

#### Scenario: A disabled bridge budget is reported as imposing no bound

- **WHEN** the bridge stream-idle budget is configured to zero or less
- **THEN** the report describes it as imposing no bound
- **AND** if live keepalives remain enabled, the idle-gap result is reported as unbounded because the client watchdog is refreshed and no bridge deadline exists
- **AND** the disabled value is never treated as a zero-duration bound.

### Requirement: A client watchdog that would fire first is warned about

The bridge SHALL emit the existing Claude Code configuration warning when its idle watchdog would abort before the bridge budget **and live keepalive injection is not effective**. This includes an explicitly shorter value or the known shorter default selected by a missing managed key. The warning SHALL name the key, both bounds, and both existing remedies.

When live keepalive injection is effective, the startup report SHALL show that the bridge owns the idle-gap termination and SHALL NOT claim that the refreshed client watchdog will abort first. Static client configuration remains a second line of defence, but its raw numeric value is not the runtime winner while pings are reaching the client.

The Codex row SHALL expose a shorter unprotected watchdog directly in the table. This change does not make the bridge rewrite Codex's provider timeout setting.

#### Scenario: Client idle watchdog undercuts the bridge stream-idle budget

- **WHEN** Claude Code's effective stream-idle value is shorter than the bridge budget and keepalive is off or inactive
- **THEN** the bridge emits the existing actionable warning naming the key, both values, and both remedies.

#### Scenario: A missing client timeout key is warned about, not passed over

- **WHEN** Claude Code's readable bridge-pointed settings omit a managed timeout key, the known client default undercuts the bridge budget, and keepalive is off or inactive
- **THEN** the bridge emits the same warning naming the default bound and both remedies
- **AND** it does not report the missing key merely as unknown.

#### Scenario: No warning when the client outlasts the bridge

- **WHEN** every client timeout value is present and greater than or equal to the corresponding bridge budget
- **THEN** no undercut warning is emitted.

#### Scenario: Active pings prevent a false undercut warning

- **WHEN** Claude Code's raw watchdog value is shorter than the bridge budget but live keepalive injection is effective
- **THEN** the idle-gap row identifies the bridge as termination authority
- **AND** the bridge does not warn that Claude Code will abort first.
