## MODIFIED Requirements

### Requirement: Capability-authenticated Ready commit

Starting a replacement process SHALL NOT by itself commit the update. For each replacement or rollback launch, the updater SHALL create private one-launch readiness context containing an unpredictable token and attempt identity. The bridge SHALL report Ready only after configuration and route validation, hosted-service startup, and successful proxy listener startup, using a local per-attempt signal that includes the expected token, PID, product version, and attempt ID. During an updater-managed target or rollback activation, the bridge SHALL perform no credential load, migration, refresh, device flow, or Copilot authentication exchange before Ready. After the Ready message is successfully sent, the bridge SHALL asynchronously resume the ordinary authentication bootstrap; an authentication failure SHALL be reported without terminating the serving bridge or reversing the installed version. The updater SHALL use a finite readiness timeout, verify that the signal belongs to the process it launched and that the process remains alive, and commit only when the new target version reports Ready. It SHALL keep all rollback material until commit.

#### Scenario: New process starts and immediately exits
- **WHEN** process creation succeeds but the new bridge exits before reporting Ready
- **THEN** the update is not committed and rollback begins

#### Scenario: New bridge cannot bind the configured port
- **WHEN** the replacement fails listener startup
- **THEN** no valid Ready signal is emitted and the updater rolls back

#### Scenario: Stored credential is expired during target activation
- **WHEN** the replacement starts with valid updater readiness context but the stored credential is expired, rejected, unreadable by the new credential format, or its refresh endpoint is unavailable
- **THEN** the replacement performs no credential or authentication operation before Ready and reports Ready after local serving health succeeds
- **AND** authentication resumes after the Ready send and any failure leaves the replacement serving without causing rollback

#### Scenario: Credential-free replacement resumes first-run login
- **WHEN** an updater-managed replacement has no current or legacy credential and successfully sends Ready
- **THEN** it starts the ordinary GitHub device-code flow and displays the login challenge without waiting for an upstream request

#### Scenario: Readiness send fails
- **WHEN** an updater-managed bridge cannot send its Ready message to the updater
- **THEN** it does not start the deferred authentication bootstrap for that activation

#### Scenario: Rollback activation does not depend on authentication
- **WHEN** the updater restores and launches the old bridge with rollback readiness context while GitHub authentication is unavailable
- **THEN** the restored bridge can report Ready after local serving health succeeds without reading, migrating, or refreshing credentials before Ready
- **AND** any authentication failure after the Ready send does not terminate the restored bridge

#### Scenario: Ordinary launch keeps its authentication gate
- **WHEN** the bridge starts without updater readiness context
- **THEN** it retains the normal startup authentication and interactive-login behavior

#### Scenario: Stale or forged readiness marker is present
- **WHEN** a readiness signal has the wrong token, attempt ID, PID, or version
- **THEN** the updater ignores or rejects it and does not commit

#### Scenario: Replacement becomes ready
- **WHEN** the launched target version reports a valid Ready signal and remains alive
- **THEN** the updater commits, removes transaction backups best-effort, and leaves the new bridge serving

#### Scenario: Readiness deadline expires
- **WHEN** the replacement remains alive but does not report Ready before the finite deadline
- **THEN** the updater terminates that replacement and rolls back
