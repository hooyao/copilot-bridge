## Context

The bridge currently understands three encrypted credential semantics: migrated Copilot Plugin exchange (v1), compatible GitHub CLI direct CAPI (v2), and explicit-provider Copilot Plugin exchange (v3). Fresh login writes v3 with GitHub's official Copilot Plugin client identity and then calls the identity-sensitive `/copilot_internal/v2/token` endpoint.

The project-owned OAuth App `copilot-bridge` is now registered with client ID `Ov23liSD97ZYGfIEHAZE`. GitHub shows Device Flow enabled and token expiration enabled, so a successful authorization returns an eight-hour `gho_` access token plus a rotating refresh token. The client ID is intentionally public; Device Flow needs no client secret. GitHub's Copilot SDK documentation and maintainer sample accept user OAuth tokens from a custom OAuth App directly, while this repository has already live-proven the direct CAPI lease shape with a GitHub CLI `gho_`. The operator requires this provider to be an explicit, highly visible configuration opt-in; default installations must continue using the official Copilot Plugin App.

## Goals / Non-Goals

**Goals:**

- Expose a clear configuration switch between the official and custom OAuth identities.
- Isolate bridge logins in a custom App's user/application/scope token pool when opted in.
- Use the custom OAuth token directly at generic Copilot CAPI without the internal exchange endpoint.
- Rotate GitHub's expiring access/refresh pair safely and preserve bounded rejection recovery.
- Keep v1, v2, and v3 credentials readable with their existing behavior.
- Prove the new path with contract tests, a real headless client, and both Windows Native AOT executables.

**Non-Goals:**

- Changing the default provider away from GitHub's official Copilot Plugin App.
- Publishing or monetizing the OAuth App through GitHub Marketplace.
- GitHub App installation-token or organization-billed server-to-server authentication.
- Shipping a client secret or changing Copilot request/response translation.
- Rewriting existing credentials automatically merely because v4 exists.

## Decisions

### Authentication options make custom identity an explicit opt-in

The stock configuration adds a top-level `Authentication` section before the ordinary server settings so it is difficult to miss:

```json
"Authentication": {
  "UseCustomAppId": false,
  "CustomAppId": "Ov23liSD97ZYGfIEHAZE"
}
```

An adjacent `_comment` explains the two wire paths and that a change applies only to the next `auth login`. Source-generated options binding and startup validation reject `UseCustomAppId=true` with either a blank ID or the official Copilot Plugin client ID, which cannot be relabelled as version-4 direct authentication. The same options loader is used by the long-running server and standalone `auth` commands, preventing the CLI from silently ignoring appsettings. GitHub device-code request failures are decoded into a bounded OAuth error code and HTTP status, so a custom App with Device Flow disabled reports `device_flow_disabled` instead of a generic 400 without echoing the response body.

When the switch is false, fresh login keeps the official Copilot Plugin client ID, v3 persistence, and token exchange. When true, `GitHubAuthClient` uses the configured custom ID with `read:user`, form-encoded RFC 8628 messages, and no client secret, and CredentialService writes v4. Existing credentials keep their recorded issuer/semantics until explicit login; changing the switch never rewrites a working credential.

Alternative: hard-switch every release user to the project App. Rejected because the operator requires opt-in and the official path is the current compatibility default.

### A new v4 record owns project OAuth direct semantics

Successful custom-App login writes credential version 4 with the access token, deadlines, rotating refresh token, scope, credential identity/generation, and exact configured `oauth_client_id`. Version 4 is classified as direct alongside v2, but only v4 is expected to carry provider-bound refresh state. Store validation requires a non-empty explicit client ID but does not compare it to the current configuration: an operator may change `CustomAppId`, while an existing encrypted credential must keep refreshing with the issuer that actually minted it until the next explicit login. Versions 1–3 are unchanged.

Alternative: repurpose v3. Rejected because v3's frozen meaning is Copilot Plugin exchange; changing it would silently reinterpret installed credentials and violate version-owned semantics.

### V4 access tokens go directly to generic CAPI

`AuthService` publishes the v4 GitHub access token as the bearer paired with `https://api.githubcopilot.com`. It never calls `/copilot_internal/v2/token`. Live verification found that the existing VS Code integration identity authenticates the custom token but exposes only eight legacy models and rejects `gpt-5.6-sol` as `model_not_supported`; GitHub Copilot SDK's default `copilot-developer-cli` identity unlocks the current model and completes the real tool loop. The immutable Copilot lease therefore carries an integration ID: only v4 uses `copilot-developer-cli`; versions 1–3 and compatible v2 remain `vscode-chat`. Routing header overrides still apply after this provider default. A known access deadline produces a refresh deadline five minutes earlier and an in-process timer; an unknown deadline retains the existing indefinite direct lease behavior.

Alternative: exchange the custom token. Rejected because the internal endpoint is App-identity-sensitive, a valid GitHub CLI token returns 403 there, and GitHub's supported custom OAuth flow passes the user token directly to Copilot.

### CredentialService rotates v4 with its recorded issuer

The existing single-flight and cross-process refresh transaction is extended to v4. Refresh uses the persisted project client ID, atomically commits both newly rotated tokens and deadlines, and never retains a spent refresh token. V1 keeps its implicit historical issuer; v3 keeps its explicit official issuer; v2 remains the non-refreshable compatibility form.

### Direct rejection distinguishes refreshable v4 from legacy v2

A refreshable v4 credential rejected by CAPI receives one forced credential rotation and one exact authentication replay, using the same total replay bound as exchanged credentials. A non-refreshable direct credential receiving 401 becomes terminal and requires login. A first 403 remains ambiguous; it gets at most one fresh/reused direct lease replay before a second 403 is classified as policy/entitlement. Recovery can observe a credential that another process replaced through an explicit login; the recovered record is therefore dispatched by its own version-owned direct/exchanged semantics in either direction instead of inheriting the rejected lease's mode. The immutable Copilot lease carries enough credential metadata to make this decision without exposing token bytes.

### Verification guards both authentication and downstream execution

Unit contracts first assert the Device Flow client ID/scope and actionable failure code, v4 persistence and validation, absence of token exchange, proactive/reactive refresh, concurrency, and secret-free status. The new tests are mutation-checked against provider/version/direct-dispatch decisions. One `ClientBehavior` case stages a freshly authorized encrypted v4 credential and drives real Codex through a multi-step tool loop; a second injects the first CAPI 403 and requires a real version-4 OAuth rotation plus one successful authentication replay before applying the client-log verdict. The Windows AOT bridge and updater are published only after these gates pass.

## Risks / Trade-offs

- **Raw direct CAPI is less formally documented than the SDK wrapper** → require live `/models`, real inference/tool execution, and client-owned log evidence before publish.
- **Applying the SDK integration ID globally would change existing wire identity** → carry it on the v4 lease and keep every older credential on `vscode-chat`; contract-test both values at the actual CAPI request.
- **The ten-token limit still exists inside the new App's own pool** → reuse the encrypted credential and run Device Flow only for explicit login or terminal recovery.
- **Organization third-party OAuth policy can block authorization** → preserve GitHub's actionable authorization failure and document the administrator-approval case.
- **A configured custom ID can be blank or point to an App without Device Flow** → validate blank values before serving/login and surface GitHub's actionable `device_flow_disabled` error for a real but misconfigured App.
- **The public default custom client ID can be copied by others** → this is expected for a Device Flow public client; no secret is embedded, and GitHub still requires user consent and applies user entitlement/policy.
- **Older binaries cannot interpret v4 after an explicit new login** → unknown-version handling remains fail-closed and non-mutating; downgrading then requires reauthentication with the older binary's provider.

## Migration Plan

1. Ship the disabled-by-default Authentication settings, shared options loader, and v4 read/validation/direct/refresh support in the same binary.
2. Leave existing v1–v3 files untouched and usable until the user explicitly logs in again or no credential exists.
3. With the switch off, fresh login continues writing v3. With it on, fresh login atomically replaces the authority with v4 only after GitHub returns a complete OAuth response.
4. Verify the v4 credential through direct CAPI and a real Codex tool loop.
5. Publish the bridge and updater together. Rollback before a v4 login is transparent; rollback afterward preserves the file but requires an older-provider login.

## Open Questions

None. Direct-CAPI acceptance by the custom App is an explicit verification gate, not an unresolved design choice.
