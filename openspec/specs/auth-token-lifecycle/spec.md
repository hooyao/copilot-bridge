# auth-token-lifecycle Specification

## Purpose
Define the secure, bounded lifecycle for the persisted GitHub OAuth credential
and the short-lived Copilot bearer/endpoint lease, including compatibility,
rotation, replay, concurrency, Native AOT, and secret-free diagnostics.
## Requirements
### Requirement: Complete GitHub OAuth credential state is preserved securely
The system SHALL preserve the access token, every expiry and rotating refresh-token field returned by GitHub device authorization, and an opaque credential-instance identity in a versioned credential record protected by the existing OS-specific token protector. A fresh device login SHALL mint a new identity while refresh rotation SHALL preserve it. Credential serialization SHALL use the source-generated JSON context, and no credential field SHALL be written in plaintext.

#### Scenario: Device flow returns a refreshable credential
- **WHEN** GitHub completes device authorization with `access_token`, `expires_in`, `refresh_token`, and `refresh_token_expires_in`
- **THEN** the system persists all four values, their derived UTC deadlines, token type, and scope in the encrypted versioned record before reporting login success

#### Scenario: Device flow returns a non-expiring credential
- **WHEN** GitHub completes device authorization without expiry or refresh-token fields
- **THEN** the system persists the access token without inventing expiry metadata and continues to support it as a non-refreshable credential

#### Scenario: Fresh login reuses the initial generation number
- **WHEN** interactive device login replaces an older credential whose generation is also the initial value
- **THEN** the new record has a distinct credential-instance identity so running processes cannot confuse it with the rejected older credential

### Requirement: Existing credential files remain usable
The system SHALL load existing encrypted raw-token files without requiring an immediate login, SHALL prefer a valid versioned credential when both formats exist, and SHALL remove every supported credential representation on logout. A legacy raw token SHALL be treated as having unknown expiry and no refresh capability.

#### Scenario: Upgrade with a valid legacy token
- **WHEN** the versioned credential is absent and the existing encrypted file decrypts to a raw GitHub access token
- **THEN** the system uses that token without rewriting or exposing it and does not force interactive authentication

#### Scenario: Versioned and legacy representations coexist
- **WHEN** a current versioned credential and a legacy-compatible access-token mirror both exist
- **THEN** the system uses the versioned credential as authoritative while keeping the mirror usable by an older bridge binary

#### Scenario: Logout after migration
- **WHEN** the operator runs `auth logout`
- **THEN** the system deletes the authoritative versioned credential, every legacy-compatible mirror, and the in-memory GitHub and Copilot token state

### Requirement: GitHub access tokens refresh before expiry
The system SHALL refresh a GitHub access token with the OAuth refresh-token grant before a known access-token deadline, using a safety margin, and SHALL atomically persist the rotated access token, refresh token, and deadlines before making the new credential visible to callers.

#### Scenario: Known access-token expiry approaches
- **WHEN** a caller needs GitHub authentication and the stored access token is inside the configured refresh safety window
- **THEN** exactly one refresh operation obtains and persists the rotated credential before the caller receives an access token

#### Scenario: Credential remains outside the refresh window
- **WHEN** a caller needs GitHub authentication and the stored access token is not near a known expiry
- **THEN** the system reuses it without an OAuth refresh request

#### Scenario: Refresh token is expired or rejected
- **WHEN** the refresh-token grant fails because the refresh token is invalid, expired, or revoked
- **THEN** the system preserves the last persisted credential, stops automatic refresh looping, and returns an actionable error requiring `auth logout` followed by `auth login`

#### Scenario: Refresh response omits the rotated refresh token
- **WHEN** a refresh grant returns a new access token without the replacement refresh token that GitHub's rotation contract requires
- **THEN** the system commits the new access-token generation as non-refreshable and does not retain the spent prior refresh token

#### Scenario: Refresh service is transiently unavailable
- **WHEN** a refresh attempt receives a rate-limit response, server error, timeout, or other transient transport failure
- **THEN** the system preserves the committed credential, does not require interactive login, and allows the bounded timer/request retry policy to try again later

### Requirement: GitHub 401 recovery is bounded and classified
The system SHALL treat a GitHub `401 Bad credentials` response as rejection of the access token used for that call. If a refresh token is available, it SHALL perform at most one credential refresh and one replay of the failed GitHub call; otherwise it SHALL report that interactive GitHub login is required.

#### Scenario: Token exchange rejects a refreshable GitHub access token
- **WHEN** `/copilot_internal/v2/token` returns 401 for the current GitHub access token and the credential has a refresh token
- **THEN** the system rotates the GitHub credential once and retries the token exchange once with the new access token

#### Scenario: GitHub rejects a legacy access token
- **WHEN** a GitHub call returns 401 for a legacy raw token with no refresh token
- **THEN** the system does not retry indefinitely and reports that the stored GitHub credential is invalid and interactive login is required

#### Scenario: Replayed GitHub call is also unauthorized
- **WHEN** the single replay after credential refresh also returns 401
- **THEN** the system surfaces the second response as a terminal authentication failure without another refresh or replay

### Requirement: Credential rotation is safe under concurrency and failure
The system SHALL single-flight refreshes within a process and coordinate rotating refresh-token use across processes sharing one credential path. Rotation SHALL re-read the authoritative record after acquiring the cross-process lock and SHALL leave the prior complete record recoverable if persistence fails.

#### Scenario: Concurrent requests detect the same expiry
- **WHEN** multiple callers in one process concurrently request authentication for an expiring credential
- **THEN** one caller performs the refresh and all callers receive the same committed credential generation

#### Scenario: Two bridge processes share a rotating credential
- **WHEN** two processes attempt to refresh the same credential generation
- **THEN** only one consumes that refresh token and the other reloads and uses the newly committed generation instead of treating the spent token as a login failure

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
- **WHEN** logout deletes credentials while another process is refreshing either configured v2 path
- **THEN** deletion holds the corresponding path locks until every credential representation is removed, and the older refresh cannot recreate a credential after logout

### Requirement: Copilot bearer and endpoint form one lease
The system SHALL publish each Copilot bearer together with its API base URL, local refresh deadline, hard-expiry diagnostic, and generation as one immutable lease. Callers SHALL never combine a token from one lease with the endpoint from another.

#### Scenario: Background refresh changes the endpoint snapshot
- **WHEN** a refresh publishes a new Copilot token while another request is resolving authentication
- **THEN** each request uses the token and API base URL from one lease generation

#### Scenario: Server and local clocks differ
- **WHEN** the token response contains `refresh_in` and an absolute `expires_at` that disagrees with the local clock
- **THEN** refresh scheduling uses a receipt-time-relative deadline derived from `refresh_in` with a safety margin while retaining the server expiry only for diagnostics

### Requirement: Copilot inference 401 triggers one safe authentication replay
The system SHALL handle a 401 from an authenticated Copilot endpoint by rejecting only the lease used for that request, obtaining the current or newly refreshed lease, and replaying the request at most once. The replay SHALL preserve the original request body bytes and non-authentication request semantics.

#### Scenario: Current Copilot bearer is rejected
- **WHEN** `/v1/messages`, `/responses`, `/models`, or `/v1/messages/count_tokens` returns 401 for the current lease
- **THEN** the system disposes that response, refreshes the rejected lease, rebuilds the request with the new bearer and paired endpoint, and sends one replay

#### Scenario: Another caller already refreshed the rejected lease
- **WHEN** a request reports 401 for generation N after generation N+1 has already been published
- **THEN** the system reuses generation N+1 without issuing a redundant token exchange

#### Scenario: Copilot replay is also unauthorized
- **WHEN** the single replay with the replacement lease also returns 401
- **THEN** the system returns the second 401 without another authentication replay

#### Scenario: Upstream returns a non-authentication status
- **WHEN** Copilot returns a status other than 401
- **THEN** the authentication replay mechanism does not resend the request or reinterpret policy, quota, validation, or rate-limit errors as token expiry

### Requirement: Authentication observability never reveals credentials
The system SHALL record secret-free authentication lifecycle events sufficient to distinguish GitHub credential failure, GitHub refresh failure, Copilot bearer refresh, Copilot inference rejection, and terminal policy or quota errors. CLI diagnostics and logs SHALL NOT emit an access token, refresh token, Authorization header, or token prefix.

#### Scenario: Authentication refresh succeeds
- **WHEN** a GitHub or Copilot token refresh completes
- **THEN** logs identify the credential layer, trigger, outcome, expiry timing, and API host without any credential bytes

#### Scenario: Copilot status is requested
- **WHEN** the operator runs `auth copilot-status`
- **THEN** the command reports status, expiry, and API base URL without printing any portion of the Copilot bearer

#### Scenario: Authentication fails terminally
- **WHEN** refresh or token exchange cannot recover
- **THEN** the operator-facing error identifies whether GitHub login, Copilot bearer acquisition, account policy, or quota requires action without embedding secrets

#### Scenario: Hosted authentication lifecycle is logged
- **WHEN** the bridge constructs hosted services and performs startup authentication
- **THEN** startup and authentication lifecycle events reach the full rolling log rather than a disposed bootstrap logger

### Requirement: Authentication changes remain Native AOT and cross-platform compatible
The credential records, OAuth requests, and responses SHALL use source-generated JSON metadata and the existing runtime OS dispatch for token protection. The change SHALL add no reflection-based serialization, runtime-loaded assembly, or unprotected platform-specific credential path.

#### Scenario: Native AOT build reads a versioned credential
- **WHEN** a published Native AOT binary loads, refreshes, and persists the credential on a supported OS
- **THEN** serialization succeeds through registered JSON metadata and the record remains protected by DPAPI on Windows or the derived-key protector with owner-only file permissions on Linux and macOS
