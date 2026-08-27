## Why

When a credential-free installation accepts a startup update, the replacement bridge reports readiness without authentication (correctly protecting the update transaction) but never resumes the normal first-run device flow afterward. The same empty-credential path also creates a historical `github_credentials.v2.dat.lock` even though there is no legacy credential to migrate.

## What Changes

- Resume the ordinary authentication bootstrap after an updater-managed process has successfully sent `Ready`, without making authentication part of the update commit gate.
- Preserve the visible first-run device-code prompt after an update and keep post-readiness authentication failures non-fatal to the serving bridge.
- Avoid creating the historical v2 migration lock when no legacy credential or historical lock exists, while retaining ordered cross-version locking whenever legacy state is present.
- Add contract tests for post-readiness ordering, failure isolation, and credential-free filesystem behavior.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `transactional-self-update`: Resume interactive authentication after the replacement has reported local readiness, without allowing authentication to decide commit or rollback.
- `auth-token-lifecycle`: Keep a credential-free lookup side-effect free with respect to the historical v2 lock while preserving migration coordination for existing legacy state.

## Impact

- Startup lifecycle: `BridgeStartupHostedService` and `UpdateReadinessReporter`.
- Credential persistence: `CredentialStore` migration-lock acquisition.
- Unit tests and the auto-update/token-storage documentation.
- No public HTTP API, configuration, credential format, or update wire-format change.
