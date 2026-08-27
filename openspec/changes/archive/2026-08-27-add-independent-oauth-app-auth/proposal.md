## Why

Fresh bridge logins currently have no operator-visible choice between GitHub's official Copilot Plugin OAuth identity and an independent OAuth App. A conspicuous opt-in setting lets operators isolate their login pool and use GitHub's supported user-token authentication directly with Copilot CAPI while preserving the official Plugin path as the safe default.

## What Changes

- Add a prominent `Authentication` section to `appsettings.json` with `UseCustomAppId: false` by default and `CustomAppId: "Ov23liSD97ZYGfIEHAZE"` prefilled.
- Keep fresh interactive login on the official Copilot Plugin App and existing version-3 token-exchange path while `UseCustomAppId` is false.
- When `UseCustomAppId` is true, make the next interactive login use the configured OAuth App ID, Device Flow, and the reviewed `read:user` scope without a client secret.
- Persist a successful custom-App login as a new explicit-provider credential version whose `gho_` access token is a direct Copilot CAPI bearer; it must never enter `/copilot_internal/v2/token`.
- Stamp custom direct CAPI requests with GitHub Copilot SDK's `copilot-developer-cli` integration identity while preserving `vscode-chat` for every existing credential version.
- Preserve and rotate the OAuth App's eight-hour access token and refresh token under the existing encrypted, cross-process-safe credential lifecycle.
- Keep credential versions 1–3 fully readable and behaviorally unchanged for upgrade compatibility.
- Extend contract tests, real-client behavior coverage, diagnostics, and architecture/user documentation for the new direct credential.
- Publish both Windows Native AOT executables after unit, contract, and real-client verification.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `auth-token-lifecycle`: Fresh login gains a configuration-selected custom OAuth direct alternative while the official Copilot Plugin exchanged credential remains the default, including provider-bound refresh and compatibility requirements.

## Impact

- Stock `appsettings.json`, source-generated option binding/validation, and both serve/auth-command composition paths.
- Authentication models and services under `src/CopilotBridge.Cli/Auth/` and `Models/GitHub/`.
- Encrypted credential version dispatch, status output, refresh/rejection semantics, and source-generated JSON contracts.
- Authentication unit tests and the real Codex client behavior harness.
- `README.md`, `docs/copilot-api-research.md`, `docs/pipeline-design.md`, `docs/token-storage.md`, `docs/design.md`, and binary-size history.
- GitHub OAuth App `copilot-bridge`; its Device Flow and token-expiration settings are already enabled, Marketplace publication is not required, and no client secret is shipped.
