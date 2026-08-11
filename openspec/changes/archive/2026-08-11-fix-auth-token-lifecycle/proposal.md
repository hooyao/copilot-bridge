## Why

Some users can authenticate and use Copilot initially, but later every GitHub and Copilot call fails with `401 Bad credentials`; restarting the bridge does not recover, while deleting `github_token.dat` and completing device login does. The bridge currently persists only the GitHub access token, discards OAuth expiry/refresh metadata, and also keeps using a rejected short-lived Copilot bearer after an upstream 401, so expiring or revoked credentials cannot recover without interactive login.

## What Changes

- Persist a versioned, encrypted GitHub OAuth credential envelope containing the access token and any returned expiry and rotating refresh-token metadata, while continuing to read legacy raw-token files.
- Refresh an expiring GitHub access token before expiry and retry refresh once when GitHub rejects an access token, with single-flight and crash-safe rotation semantics.
- Treat a Copilot inference 401 as rejection of the bearer used for that request: invalidate that bearer, obtain a fresh token/endpoint pair, and replay the unauthenticated request at most once.
- Keep the Copilot bearer and its API base URL in one atomic lease so a refresh cannot pair a token with another snapshot's endpoint.
- Add secret-safe authentication diagnostics and refresh outcome logging; stop printing any Copilot token prefix from diagnostic commands.
- Add contract tests for expiry, refresh-token rotation, legacy storage, concurrency, 401 recovery, retry bounds, and secret redaction, followed by a real headless gpt-5.6 client run through the changed authentication path.

## Capabilities

### New Capabilities

- `auth-token-lifecycle`: Defines secure persistence, proactive and reactive refresh, bounded 401 recovery, concurrency, compatibility, and observability requirements for GitHub OAuth credentials and Copilot bearer tokens.

### Modified Capabilities

None.

## Impact

- Authentication facade and GitHub device/token exchange code under `src/CopilotBridge.Cli/Auth/`.
- Copilot request construction and authenticated retry behavior under `src/CopilotBridge.Cli/Copilot/`.
- Encrypted credential storage, adding an authoritative versioned record while retaining `github_token.dat` as a backward-compatible raw-token mirror and leaving the OS-specific protection boundary unchanged.
- Source-generated JSON registrations, CLI authentication diagnostics, logging, unit contracts, Playground API-contract coverage, and real-client behavior verification.
- No new runtime dependency and no relaxation of Native AOT, source-generated JSON, or token-at-rest protections.
