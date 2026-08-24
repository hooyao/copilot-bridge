## Why

The bridge has accumulated two encrypted credential formats,
`github_credentials.v2.dat` and `github_token.dat`, and authentication code knows
their paths, precedence, provider inference, refresh metadata, and migration details.
That makes a working credential fragile during upgrades and leaks credential-storage
policy into `AuthService` and CLI callers.

Production security-log evidence also proved that repeated Copilot Plugin device
logins hit GitHub's ten-token user/application/scope limit (`max_for_app`). A new
login must eventually move the bridge to a GitHub CLI OAuth `gho_` direct credential,
but an upgrade must not discard a still-working legacy credential merely to do so.

## What Changes

- Introduce one authoritative encrypted file beside the bridge executable:
  `github_credentials.dat`.
- Make the encrypted plaintext a versioned credential envelope. Version 1 represents
  the legacy Copilot Plugin credential shape; version 2 represents the GitHub CLI
  OAuth direct credential. The version, not filename or token prefix, selects behavior.
- On first load, transactionally migrate `github_credentials.v2.dat` and
  `github_token.dat` into version 1 of the new file. Prefer the complete v2 record;
  fall back to the raw mirror only when v2 is missing or unreadable.
- Re-open and validate the committed new file before deleting both exact legacy files.
- Continue using and refreshing the migrated version-1 credential until GitHub
  terminally rejects it. Do not force login merely because its format is old.
- After terminal rejection, require interactive login; built-in GitHub CLI OAuth Device
  Flow creates a version-2 `gho_` credential in the same file and direct CAPI uses it.
- Move every path, protection, migration, refresh, login, rejection, and logout detail
  behind an independent `CredentialService`. `AuthService` consumes only its abstract
  credential lease.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `auth-token-lifecycle`: Replace multi-file runtime credential management with a
  single versioned store, transactional destructive migration, and an independent
  credential service while retaining built-in GitHub CLI OAuth/direct CAPI.

## Impact

This changes the persisted credential contract, startup migration, authentication
ownership, refresh and rejection routing, logout cleanup, CLI diagnostics, tests, and
documentation. It adds no runtime dependency and keeps Native AOT/source-generated
JSON requirements.
