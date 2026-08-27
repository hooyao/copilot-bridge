## MODIFIED Requirements

### Requirement: Credential rotation is safe under concurrency and failure
The system SHALL single-flight refreshes and first-use login within a process and coordinate credential mutation across processes sharing the single authoritative path. Mutation SHALL re-read the authoritative record after acquiring the cross-process lock and SHALL leave the prior complete record recoverable if persistence fails. The authoritative path lock SHALL remain at a stable filesystem identity after release and credential deletion. The historical v2 lock SHALL be acquired only when a legacy credential input or that lock already exists; once created, it SHALL remain at a stable filesystem identity.

#### Scenario: Concurrent requests detect the same expiry
- **WHEN** multiple callers in one process concurrently request authentication for an expiring credential
- **THEN** one caller performs the refresh and all callers receive the same committed credential generation

#### Scenario: Two bridge processes share a rotating credential
- **WHEN** two processes attempt to refresh the same credential generation
- **THEN** only one consumes that refresh token and the other reloads and uses the newly committed generation instead of treating the spent token as a login failure

#### Scenario: A legacy file reappears while refresh waits for the unified lock

- **WHEN** an older bridge recreates a legacy credential file after refresh has loaded the unified authority but before it acquires the unified mutation lock
- **THEN** refresh reloads only `github_credentials.dat` while holding that lock and does not re-enter migration or self-deadlock
- **AND** a later unlocked load removes the residual legacy file.

#### Scenario: Process stops during rotation
- **WHEN** writing or replacing the refreshed credential fails before commit
- **THEN** the previously committed credential file remains readable and no partially written record becomes authoritative

#### Scenario: Fresh login replaces the observed generation
- **WHEN** a process reports rejection for credential instance A after another process has committed fresh-login instance B with the same generation number
- **THEN** the process reloads and uses instance B without rejecting it or consuming its refresh token

#### Scenario: Fresh login races an older refresh
- **WHEN** interactive login and refresh of the prior credential attempt to commit to the same authoritative path concurrently
- **THEN** both writers use the same path-scoped lock so the fresh login remains authoritative and the older refresh cannot overwrite it afterward

#### Scenario: Logout races a refresh
- **WHEN** logout deletes credentials while another process is refreshing the authoritative file
- **THEN** deletion holds the authoritative lock and, when legacy state exists, the historical lock until the new file and both migration inputs are removed, and the older refresh cannot recreate a credential after logout

#### Scenario: Another process acquires a released credential lock
- **WHEN** one process releases an authoritative or pre-existing historical credential mutation lock and another process later acquires that path
- **THEN** the lock file was not unlinked or recreated, so every contender synchronizes on the same filesystem object

#### Scenario: Credential-free lookup does not introduce historical state
- **WHEN** neither the unified credential, either legacy credential input, nor the historical v2 lock exists
- **THEN** checking authentication returns no credential without creating `github_credentials.v2.dat.lock`

#### Scenario: First login on a fresh installation avoids the historical lock
- **WHEN** the first login saves a unified credential and no legacy credential input or historical v2 lock exists
- **THEN** the credential is committed under the authoritative lock without creating `github_credentials.v2.dat.lock`
