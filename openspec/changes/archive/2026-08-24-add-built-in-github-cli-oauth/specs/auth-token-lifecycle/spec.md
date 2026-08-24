## ADDED Requirements

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

#### Scenario: Unknown version is loaded

- **WHEN** the file carries any unsupported version
- **THEN** authentication fails with an actionable unsupported-format error
- **AND** no credential file is rewritten or deleted.

### Requirement: Both legacy files migrate transactionally

When the new file is absent, the system SHALL migrate both historical formats under
one cross-process mutation lock. It SHALL prefer a valid
`github_credentials.v2.dat`, otherwise use a valid `github_token.dat`. It SHALL
atomically commit and re-open the new version-1 file before deleting both old files.

#### Scenario: Complete v2 record migrates

- **WHEN** the old v2 file decrypts and parses successfully
- **THEN** every access/refresh/deadline/type/scope/identity/generation field is
  preserved in version 1
- **AND** the raw mirror cannot override it.

#### Scenario: Raw token fallback migrates

- **WHEN** v2 is missing or unreadable and the raw token decrypts
- **THEN** a non-refreshable version-1 credential with unknown expiry is committed.

#### Scenario: Commit verification succeeds

- **WHEN** the new file is atomically written and normal readback matches the source
- **THEN** both `github_credentials.v2.dat` and `github_token.dat` are deleted
- **AND** only `github_credentials.dat` remains as credential data.

#### Scenario: Migration fails before verification

- **WHEN** protection, write, replace, readback, parsing, or comparison fails
- **THEN** both legacy credential files remain untouched and usable by the prior binary.

#### Scenario: Two processes migrate concurrently

- **WHEN** two bridge processes observe no new file
- **THEN** one migrates under the stable lock and the other reloads the committed file
  without deleting or overwriting a newer credential.

#### Scenario: Legacy cleanup is interrupted after verified commit

- **WHEN** the new file is verified but deletion of only one legacy file succeeds
- **THEN** the verified new file remains authoritative
- **AND** a later load retries deletion of every residual legacy credential under the
  same ordered locks.

### Requirement: Credential management is an independent service

The system SHALL encapsulate paths, encryption, migration, version dispatch, OAuth
login, refresh, terminal rejection, status, and logout inside `CredentialService`.
Callers SHALL request an immutable credential lease and SHALL NOT read files, decrypt
records, infer providers, or choose migration sources.

#### Scenario: AuthService needs a credential

- **WHEN** `AuthService` constructs a Copilot lease
- **THEN** it obtains the current credential lease only through CredentialService.

#### Scenario: CLI requests status or logout

- **WHEN** authentication CLI commands inspect or delete credentials
- **THEN** they call CredentialService and never enumerate credential paths themselves.

#### Scenario: Logout encounters unreadable credential bytes

- **WHEN** the operator runs `auth logout` while any current or legacy credential file
  cannot be decrypted or parsed
- **THEN** CredentialService deletes every exact credential path without requiring a
  successful load first.

### Requirement: Working legacy credentials are preserved until terminal rejection

A migrated version-1 credential SHALL continue its existing proactive/reactive refresh
contract and SHALL NOT trigger interactive login merely because a newer version exists.
Only typed terminal rejection SHALL require replacement login; transient failures SHALL
preserve the credential.

#### Scenario: Migrated legacy credential remains accepted

- **WHEN** GitHub and Copilot continue accepting version 1
- **THEN** bridge requests continue without login or format replacement.

#### Scenario: Refreshable version 1 approaches expiry

- **WHEN** its access deadline enters the safety window
- **THEN** CredentialService rotates and atomically rewrites version 1 with all new state.

#### Scenario: Version 1 is terminally rejected

- **WHEN** GitHub rejects it and no bounded refresh can recover
- **THEN** CredentialService preserves the file, marks that identity terminal in-process,
  and returns an actionable login-required error without automatic Device Flow.

### Requirement: Replacement login creates the newest direct version

Interactive login SHALL use built-in GitHub CLI OAuth Device Flow without `gh.exe` or
a client secret and SHALL atomically replace the single file with version 2. Version 2
SHALL authenticate directly to generic Copilot CAPI without token exchange.

#### Scenario: User logs in after legacy rejection

- **WHEN** the operator runs `auth login`
- **THEN** successful authorization overwrites `github_credentials.dat` with version 2
- **AND** clears terminal rejection state.

#### Scenario: Version 2 obtains a Copilot lease

- **WHEN** AuthService receives a version-2 credential lease
- **THEN** it uses the access token directly as CAPI bearer at
  `https://api.githubcopilot.com` with unknown expiry and no exchange timer.

#### Scenario: Concurrent first use observes no credential

- **WHEN** multiple callers in one process request authentication before any credential
  exists
- **THEN** exactly one Device Flow runs and every waiter uses the committed version-2
  credential.

## MODIFIED Requirements

### Requirement: GitHub access tokens refresh before expiry

The system SHALL refresh a version-1 legacy Copilot Plugin access token with the OAuth
refresh-token grant before a known access-token deadline, using a safety margin, and
SHALL atomically persist the rotated access token, refresh token, and deadlines before
making the new credential visible to callers.

#### Scenario: Known access-token expiry approaches

- **WHEN** a caller needs GitHub authentication and the stored version-1 access token is
  inside the configured refresh safety window
- **THEN** exactly one refresh operation obtains and persists the rotated credential
  before the caller receives an access token.

#### Scenario: Credential remains outside the refresh window

- **WHEN** a caller needs GitHub authentication and the stored version-1 access token is
  not near a known expiry
- **THEN** the system reuses it without an OAuth refresh request.

#### Scenario: Refresh token is expired or rejected

- **WHEN** the refresh-token grant fails because the refresh token is invalid, expired,
  or revoked
- **THEN** the system preserves the last persisted credential, stops automatic refresh
  looping, and returns an actionable error requiring `auth login`.

#### Scenario: Refresh response omits the rotated refresh token

- **WHEN** a refresh grant returns a new access token without the replacement refresh
  token that GitHub's rotation contract requires
- **THEN** the system commits the new access-token generation as non-refreshable and
  does not retain the spent prior refresh token.

#### Scenario: Refresh service is transiently unavailable

- **WHEN** a refresh attempt receives a rate-limit response, server error, timeout, or
  other transient transport failure
- **THEN** the system preserves the committed credential, does not require interactive
  login, and allows the bounded timer/request retry policy to try again later.

### Requirement: GitHub 401 recovery is bounded and classified

The system SHALL treat a GitHub API or legacy token-exchange `401 Bad credentials`
response as rejection of the credential used for that call. If a version-1 credential
has a refresh token, it SHALL perform at most one credential refresh and one replay of
the failed GitHub call; otherwise it SHALL report that interactive GitHub login is
required.

#### Scenario: Token exchange rejects a refreshable GitHub access token

- **WHEN** `/copilot_internal/v2/token` returns 401 for the current version-1 GitHub
  access token and the credential has a refresh token
- **THEN** the system rotates the GitHub credential once and retries the token exchange
  once with the new access token.

#### Scenario: GitHub rejects a legacy access token

- **WHEN** a GitHub call returns 401 for a legacy raw token with no refresh token
- **THEN** the system does not retry indefinitely and reports that the stored GitHub
  credential is invalid and interactive login is required.

#### Scenario: GitHub user lookup rejects a direct credential

- **WHEN** `/user` returns 401 for a version-2 direct credential
- **THEN** the system marks that credential identity terminal and requires `auth login`
  without attempting a Copilot Plugin refresh grant.

#### Scenario: Replayed GitHub call is also unauthorized

- **WHEN** the single replay after credential refresh also returns 401
- **THEN** the system surfaces the second response as a terminal authentication failure
  without another refresh or replay.

### Requirement: Credential rotation is safe under concurrency and failure

The system SHALL single-flight refreshes and first-use login within a process and
coordinate credential mutation across processes sharing the single authoritative path.
Mutation SHALL re-read the authoritative record after acquiring the cross-process lock
and SHALL leave the prior complete record recoverable if persistence fails. Empty path
locks SHALL remain at stable filesystem identities after release and credential deletion.

#### Scenario: Concurrent requests detect the same expiry

- **WHEN** multiple callers in one process concurrently request authentication for an
  expiring credential
- **THEN** one caller performs the refresh and all callers receive the same committed
  credential generation.

#### Scenario: Two bridge processes share a rotating credential

- **WHEN** two processes attempt to refresh the same credential generation
- **THEN** only one consumes that refresh token and the other reloads and uses the newly
  committed generation instead of treating the spent token as a login failure.

#### Scenario: Process stops during rotation

- **WHEN** writing or replacing the refreshed credential fails before commit
- **THEN** the previously committed credential file remains readable and no partially
  written record becomes authoritative.

#### Scenario: Fresh login replaces the observed generation

- **WHEN** a process reports rejection for credential instance A after another process
  has committed fresh-login instance B with the same generation number
- **THEN** the process reloads and uses instance B without rejecting it or consuming its
  refresh token.

#### Scenario: Fresh login races an older refresh

- **WHEN** interactive login and refresh of the prior credential attempt to commit to
  the authoritative path concurrently
- **THEN** both writers use the same path-scoped lock so the fresh login remains
  authoritative and the older refresh cannot overwrite it afterward.

#### Scenario: Logout races a refresh

- **WHEN** logout deletes credentials while another process is refreshing the
  authoritative file
- **THEN** deletion holds the credential locks until the new file and both migration
  inputs are removed, and the older refresh cannot recreate a credential afterward.

#### Scenario: Another process acquires a released credential lock

- **WHEN** one process releases a credential mutation lock and another process later
  acquires that path
- **THEN** the lock file was not unlinked or recreated, so every contender synchronizes
  on the same filesystem object.

### Requirement: Authentication observability never reveals credentials

The system SHALL record secret-free authentication lifecycle events sufficient to
distinguish credential format and migration outcome, GitHub credential failure, GitHub
refresh failure, direct or exchanged Copilot authentication, a recoverable first CAPI
401/403 rejection, a successful authentication replay, and terminal authentication,
policy, quota, or rate-limit outcomes. A first 403 SHALL NOT be labelled definitive
policy/entitlement before the bounded fresh-lease replay completes. CLI diagnostics and
logs SHALL NOT emit an access token, refresh token, Authorization header, token prefix,
token-derived identifier, hash, or decrypted credential payload.

#### Scenario: Migration completes

- **WHEN** a legacy file is migrated and deleted
- **THEN** logs name only source format, destination version, and outcome.

#### Scenario: Status is requested

- **WHEN** the operator runs `auth status`
- **THEN** it reports the single authoritative path and version-owned metadata without
  exposing credential material.

#### Scenario: Authentication refresh succeeds

- **WHEN** a GitHub or Copilot token refresh completes
- **THEN** logs identify the credential layer, trigger, outcome, expiry timing, and API
  host without any credential bytes.

#### Scenario: First CAPI 403 begins bounded recovery

- **WHEN** CAPI returns the first 403 for lease generation N
- **THEN** logs identify status 403, generation N, and the one replay attempt
- **AND** do not yet label the account definitively policy-ineligible.

#### Scenario: CAPI 403 persists after replay

- **WHEN** the refreshed or reused lease replay also returns 403
- **THEN** logs classify the second response as terminal policy/entitlement after
  authentication replay without exposing either lease.

#### Scenario: Copilot status is requested

- **WHEN** the operator runs `auth copilot-status`
- **THEN** the command reports status, expiry, mode, and API base URL without printing
  any portion of the Copilot bearer.

#### Scenario: Authentication fails terminally

- **WHEN** refresh or token exchange cannot recover
- **THEN** the operator-facing error identifies whether GitHub login, Copilot bearer
  acquisition, account policy, or quota requires action without embedding secrets.

#### Scenario: Hosted authentication lifecycle is logged

- **WHEN** the bridge constructs hosted services and performs startup authentication
- **THEN** startup and authentication lifecycle events reach the full rolling log rather
  than a disposed bootstrap logger.

### Requirement: Copilot CAPI authentication rejection triggers one safe replay

The system SHALL classify the first 401 or 403 from an authenticated Copilot CAPI
endpoint against the exact bearer/endpoint lease generation used for that request.
For an exchanged version-1 lease, it SHALL obtain an already-newer or freshly minted
lease, rebuild the request, and replay it at most once. For a direct version-2 lease,
401 SHALL terminally reject the persisted credential without replaying the same bearer,
while 403 SHALL remain ambiguous and MAY republish that direct lease for one bounded
policy/authentication replay. Any replay SHALL preserve the original request body bytes,
non-authentication headers and semantics, shared transient-retry accounting, and
per-send timeout behavior.

One authentication replay SHALL be the total bound even when the two sends return
different rejection statuses. A replayed 401 SHALL be terminal authentication failure;
a replayed 403 SHALL retain terminal policy/entitlement meaning. Statuses other than
401/403 SHALL NOT enter this lease-rejection replay.

#### Scenario: Current exchanged Copilot bearer receives 401

- **WHEN** `/v1/messages`, `/responses`, `/models`, or `/v1/messages/count_tokens`
  returns 401 for the current exchanged version-1 lease
- **THEN** the system disposes that response, rejects the used generation, obtains its
  replacement, and sends one exact replay.

#### Scenario: Current direct Copilot credential receives 401

- **WHEN** an authenticated CAPI endpoint returns 401 for a version-2 direct lease
- **THEN** the system marks the persisted credential identity terminal and requires
  `auth login`
- **AND** does not resend the request with the same rejected bearer.

#### Scenario: Stale rejection follows a terminal current credential

- **WHEN** current credential B has already been marked terminal and a rejection for
  older credential A arrives later
- **THEN** A does not overwrite B's terminal identity or make B usable again.

#### Scenario: Current Copilot bearer receives ambiguous 403

- **WHEN** an authenticated CAPI endpoint returns 403 for the current lease
- **THEN** the system treats the first refusal as ambiguous bearer-or-policy state
- **AND** rejects the used generation, obtains its replacement, and sends one exact
  replay without requiring a bridge restart.

#### Scenario: Another caller already refreshed the rejected generation

- **WHEN** a request reports 401 or 403 for generation N after generation N+1 has
  already been published
- **THEN** the system reuses generation N+1 for the one replay without issuing a
  redundant token exchange.

#### Scenario: Refreshed request remains forbidden

- **WHEN** the one replay after a first 403 also returns 403
- **THEN** the system returns that second response as terminal policy/entitlement
- **AND** performs no further refresh or replay.

#### Scenario: Rejection statuses change across sends

- **WHEN** the first and replayed responses are any sequence of 401 and 403
- **THEN** the system performs no more than one lease refresh/reuse and two authenticated
  sends total.

#### Scenario: Non-authentication status is unchanged

- **WHEN** Copilot returns 400, 402, 429, a validation response, or another status
  outside 401/403
- **THEN** the authentication replay mechanism does not resend the request or
  reinterpret that status as token rejection.

#### Scenario: Replay preserves the request contract

- **WHEN** a POST request carrying body bytes, vision/beta/header overrides, and a
  consumed transient-retry budget crosses a 401 or 403 replay
- **THEN** both sends carry identical business bytes and headers while each uses its own
  lease-paired bearer/endpoint and per-send header timer
- **AND** the authentication replay does not reset the transient-retry budget.

## REMOVED Requirements

### Requirement: Existing credential files remain usable

**Reason:** Multi-file runtime authority and downgrade mirrors are superseded by the
transactional migration requirement and the single versioned credential authority.

**Migration:** On the first current-binary load, migrate either
`github_credentials.v2.dat` or `github_token.dat` into `github_credentials.dat`, verify
the commit, and delete both old credential files. A working migrated credential remains
usable without interactive login.
