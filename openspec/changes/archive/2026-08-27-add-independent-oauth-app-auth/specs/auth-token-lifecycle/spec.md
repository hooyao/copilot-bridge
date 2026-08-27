## MODIFIED Requirements

### Requirement: One versioned executable-local credential file

The system SHALL use only encrypted `<exe-dir>/github_credentials.dat` as the runtime
credential authority. Its decrypted source-generated JSON SHALL contain a required
version that determines the complete credential semantics without consulting the
filename or token prefix. Unknown versions SHALL fail closed without mutation.

#### Scenario: Version 1 is loaded

- **WHEN** `version=1` is read
- **THEN** the service treats it as a legacy Copilot Plugin credential and preserves
  complete access/refresh/deadline/identity/generation state.

#### Scenario: Version 2 is loaded

- **WHEN** `version=2` is read
- **THEN** the service treats it as a GitHub CLI OAuth direct credential.

#### Scenario: Version 3 is loaded

- **WHEN** `version=3` is read
- **THEN** the service treats it as a Copilot Plugin exchanged credential
- **AND** requires the issuing OAuth App ID in `oauth_client_id`.

#### Scenario: Version 4 is loaded

- **WHEN** `version=4` is read
- **THEN** the service treats it as an explicit custom OAuth App direct credential
- **AND** requires a non-empty issuing client ID in `oauth_client_id`.

#### Scenario: Unknown version is loaded

- **WHEN** the file carries any unsupported version
- **THEN** authentication fails with an actionable unsupported-format error
- **AND** no credential file is rewritten or deleted.

## ADDED Requirements

### Requirement: Replacement login follows the configured OAuth provider

The stock configuration SHALL expose a conspicuous Authentication provider section
whose `UseCustomAppId` default is false and whose `CustomAppId` default is
`Ov23liSD97ZYGfIEHAZE`. Interactive login SHALL use the official Copilot Plugin App and
write exchanged version 3 while the switch is false. When the switch is true, login
SHALL use the configured custom OAuth App without `gh.exe` or a client secret and write
direct version 4 with its issuing client ID. Existing credentials SHALL retain their
recorded provider semantics until an explicit login replaces them.

#### Scenario: Stock configuration is safe and visible

- **WHEN** the shipped `appsettings.json` is inspected
- **THEN** its top-level Authentication section shows `UseCustomAppId` as false
- **AND** prepopulates `CustomAppId` with `Ov23liSD97ZYGfIEHAZE`
- **AND** explains that the setting selects the next login provider and requires restart/login to replace an existing credential.

#### Scenario: Default Device Flow uses the official provider

- **WHEN** `UseCustomAppId` is false and the bridge begins interactive login
- **THEN** it sends the official Copilot Plugin client ID and scope `read:user`
- **AND** sends no client secret
- **AND** successful authorization writes exchanged version 3.

#### Scenario: Custom Device Flow uses the configured provider

- **WHEN** `UseCustomAppId` is true and the bridge begins interactive login
- **THEN** it sends the configured `CustomAppId` and scope `read:user`
- **AND** it sends no client secret.

#### Scenario: Custom Device Flow is disabled

- **WHEN** GitHub rejects the device-code request with `device_flow_disabled`
- **THEN** login reports that bounded OAuth error code and the HTTP status
- **AND** it does not expose the OAuth response body or any credential material.

#### Scenario: Enabled custom provider is missing

- **WHEN** `UseCustomAppId` is true and `CustomAppId` is null, empty, or whitespace
- **THEN** configuration validation fails before Device Flow or serving begins
- **AND** the error identifies `Authentication:CustomAppId` without exposing credentials.

#### Scenario: Official provider cannot be labelled as custom direct auth

- **WHEN** `UseCustomAppId` is true and `CustomAppId` is the official Copilot Plugin client ID
- **THEN** configuration validation fails before Device Flow or serving begins
- **AND** the error directs the operator to disable `UseCustomAppId` for the official provider.

#### Scenario: User completes custom OAuth authorization

- **WHEN** custom Device Flow returns a complete access/refresh token response
- **THEN** the bridge atomically writes version 4 with the configured client ID and every
  returned token/deadline/scope field
- **AND** clears terminal rejection state.

#### Scenario: Version 4 obtains a Copilot lease

- **WHEN** AuthService receives a version-4 credential lease
- **THEN** it uses the GitHub access token directly as bearer at
  `https://api.githubcopilot.com`
- **AND** sends `Copilot-Integration-Id: copilot-developer-cli` while versions 1–3
  and compatible version 2 continue sending `vscode-chat`
- **AND** makes no request to `/copilot_internal/v2/token`.

#### Scenario: Existing credential survives upgrade

- **WHEN** a valid version-1, version-2, or version-3 file is loaded by the new binary
- **THEN** its version-specific exchange/direct behavior remains unchanged
- **AND** the file is not rewritten merely because version 4 exists.

#### Scenario: Provider setting changes while a credential exists

- **WHEN** the operator changes `UseCustomAppId` or `CustomAppId` without running
  `auth login`
- **THEN** the existing encrypted credential continues using and refreshing with its
  recorded issuer and version semantics
- **AND** the new setting controls only the next interactive login.

#### Scenario: Concurrent first use observes no credential

- **WHEN** multiple callers in one process request authentication before any credential
  exists
- **THEN** exactly one Device Flow runs and every waiter uses the version selected by
  the same startup configuration snapshot.

### Requirement: Project OAuth direct credentials rotate and recover safely

The system SHALL preserve and rotate the project OAuth App's expiring access token and
refresh token through CredentialService using the recorded client ID. A version-4
direct lease SHALL refresh before a known access deadline and SHALL perform no more
than one forced credential rotation and one exact authentication replay after a CAPI
authentication rejection. A non-refreshable rejected direct credential SHALL fail
terminally rather than replaying the same bearer.

#### Scenario: Known project access-token expiry approaches

- **WHEN** a version-4 access deadline enters the configured safety window
- **THEN** exactly one refresh operation obtains and atomically persists the rotated
  access token, refresh token, and deadlines before a caller receives the credential
- **AND** the refresh request uses the recorded project OAuth client ID without a client secret.

#### Scenario: Project refresh rotates the token pair

- **WHEN** GitHub accepts a version-4 refresh token and returns replacement access and
  refresh tokens
- **THEN** the new generation contains both replacement values
- **AND** the spent access and refresh tokens are no longer retained.

#### Scenario: Project refresh credential is rejected

- **WHEN** GitHub reports that the version-4 refresh token is invalid, expired, or revoked
- **THEN** the bridge preserves the last committed encrypted record, stops automatic
  refresh looping, and requires interactive login.

#### Scenario: Refreshable version 4 receives CAPI 401

- **WHEN** an authenticated CAPI endpoint returns 401 for the current refreshable
  version-4 direct lease
- **THEN** the bridge rotates the GitHub credential once and rebuilds the request with
  the newer direct lease
- **AND** sends no more than one exact authentication replay.

#### Scenario: Non-refreshable direct credential receives CAPI 401

- **WHEN** an authenticated CAPI endpoint returns 401 for a direct credential with no
  usable refresh token
- **THEN** the bridge marks that credential identity terminal and requires login
- **AND** does not resend the request with the same bearer.

#### Scenario: Version 4 receives ambiguous CAPI 403

- **WHEN** an authenticated CAPI endpoint returns the first 403 for a version-4 lease
- **THEN** the bridge obtains an already-newer or freshly rotated direct lease and
  sends at most one exact replay
- **AND** only a replayed 403 is classified as terminal policy or entitlement.

#### Scenario: Project direct replay preserves request semantics

- **WHEN** a POST carrying body bytes, routing headers, and a consumed retry budget
  crosses version-4 authentication recovery
- **THEN** both sends carry identical business bytes and headers while each uses the
  appropriate credential generation
- **AND** authentication recovery does not reset the transient retry budget.

#### Scenario: Concurrent login replaces the rejected provider version

- **WHEN** rejection recovery observes that another process replaced the rejected
  credential with a newer credential using different direct/exchanged semantics
- **THEN** the replacement is dispatched according to its own recorded version
- **AND** no Copilot Plugin GitHub token is sent directly to CAPI and no custom direct
  token is sent to the internal exchange endpoint.

### Requirement: Project OAuth direct authentication is proven before publication

The project SHALL NOT report or publish the independent OAuth authentication change as
verified until the custom App credential reaches live Copilot through a real headless
client and both Windows Native AOT executables have been produced.

#### Scenario: Real Codex exercises version 4

- **WHEN** a real Codex client performs a multi-step tool task through a bridge
  subprocess using a freshly authorized encrypted version-4 credential
- **THEN** the trace contains a matching tool call/output round trip and direct
  credential-version-4 lifecycle evidence
- **AND** client stdout contains no abort and its dispatch log contains zero router or
  incompatible-payload fatal rows.

#### Scenario: Real Codex exercises version-4 rejection recovery

- **WHEN** a real Codex client performs a complex tool task while the bridge injects
  the first CAPI 403 using a separately authorized, marker-confirmed, single-use
  refreshable version-4 credential source
- **THEN** the bridge rotates that credential with its recorded client ID and sends one
  successful authentication replay to live Copilot
- **AND** the client completes the tool round trip with no abort or dispatch fatal
- **AND** the harness rejects installed or reusable success-test credential sources for
  this rotation-consuming scenario.

#### Scenario: Native AOT artifacts are published

- **WHEN** verification is green on Windows
- **THEN** Release win-x64 Native AOT publication produces both `copilot-bridge.exe`
  and `copilot-updater.exe`
- **AND** the bridge binary contains no OAuth client secret.

## REMOVED Requirements

### Requirement: Replacement login creates the explicit-provider Copilot Plugin version

**Reason**: Fresh login now follows an explicit configuration choice: the default
official provider still writes version 3, while custom opt-in writes direct version 4.

**Migration**: Existing version-3 Copilot Plugin credentials remain supported and
continue exchanging exactly as before. With the stock false setting, future login also
keeps version 3; only an explicit login while the custom switch is enabled writes
version 4.
