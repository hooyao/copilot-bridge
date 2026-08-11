## Context

The bridge has two credential tiers:

1. A GitHub user access token obtained by OAuth device flow and stored encrypted in `github_token.dat`.
2. A short-lived Copilot bearer obtained from `GET /copilot_internal/v2/token` and cached in memory with the returned CAPI endpoint.

The production failure is now localized. In the failed state, `auth whoami`, `auth copilot-status`, and `debug list-models --all` all return GitHub REST `401 Bad credentials`; restarting keeps using the same invalid on-disk GitHub token, while a new device login temporarily recovers. The current `AccessTokenResponse` drops `expires_in`, `refresh_token`, and `refresh_token_expires_in`, and `TokenStore` stores only the access-token string. Independently, `CopilotClient` returns an inference 401 without invalidating the bearer that caused it.

Post-implementation field capture on the affected account resolved one uncertainty:
the newly issued v2 credential reports `refreshable: False`, unknown access expiry,
and no refresh-token expiry. GitHub did not return any refresh metadata for this
login. Therefore the original approximately one-hour invalidation is not proven
to be natural OAuth expiry; if it recurs, it is active GitHub-side revocation or
an authentication-service failure. The bridge can classify and stop retrying such
a terminal non-refreshable generation, but cannot silently mint a replacement
without interactive device authorization.

This field shape is not evidence that the token exchange failed. The bridge uses
the same `Iv1.b507a08c87ecfe98` client id and `read:user` scope as the checked-in
Copilot protocol reference, whose successful device-flow response contract
contains only `access_token`, `token_type`, and `scope`. GitHub's OAuth App device
flow documentation shows the same response. GitHub documents `expires_in` plus a
rotating `refresh_token` only for GitHub App user tokens when expiring user tokens
are enabled (eight-hour access token and six-month refresh token). The affected
login succeeded, and a subsequent GitHub user lookup and Copilot-token exchange
both succeeded; there simply is no refresh grant available for this credential.

The apparent one-hour delay can be a visibility delay rather than the GitHub
credential's lifetime: a Copilot bearer already minted from a valid GitHub token
continues working until its own refresh deadline. If the underlying GitHub token
is revoked in the meantime, the bridge first observes that revocation when it
next calls `/copilot_internal/v2/token`. GitHub's documented revocation causes
include credential exposure, user/app/third-party revocation, enterprise action,
and exceeding ten tokens for one user/application/scope combination. The GitHub
security log's `oauth_authorization.destroy` event is the next server-side
evidence to check; none of these causes can be inferred from elapsed time alone.

The official VS Code Copilot client treats the full GitHub/Copilot lifecycle as refreshable state: it keeps a five-minute safety window, uses `refresh_in` relative to receipt time to avoid clock-skew failures, and clears a Copilot bearer after an authenticated upstream 401/403. The bridge needs equivalent lifecycle guarantees without adding a runtime dependency, exposing credentials, or breaking Native AOT.

## Goals / Non-Goals

**Goals:**

- Preserve and rotate all GitHub OAuth fields needed for silent refresh.
- Continue using existing encrypted raw-token files and retain a practical downgrade path.
- Make refresh single-flight, cross-process safe, and crash-safe.
- Keep each Copilot bearer inseparable from the endpoint returned with it.
- Recover once from a genuine upstream 401 without hiding a terminal authentication failure.
- Make authentication timing and failure layer observable without logging credential material.
- Provide deterministic contract tests and a real-client verification path that exercises the changed authentication behavior.

**Non-Goals:**

- Bypassing Copilot plan, organization, SSO, model-policy, trade, billing, or quota restrictions.
- Retrying 400, 402, 403, 429, or other non-401 inference responses as authentication failures.
- Persisting the short-lived Copilot bearer across process restarts.
- Changing the official GitHub Copilot OAuth client id or requesting broader scopes.
- Avoiding interactive login after both the GitHub access token and refresh token are unusable.

## Decisions

### 1. Store a versioned credential record and keep a legacy access-token mirror

Introduce a source-generated `GitHubCredentialRecord` containing a pinned format version, access token, optional access expiry, optional refresh token and refresh expiry, token type, scope, and credential generation. Protect the complete serialized record with the same `ITokenProtector` selected today; only the plaintext shape changes.

The authoritative v2 record will use a distinct filename next to the existing `github_token.dat`. A successful device login or refresh writes the v2 record and then updates `github_token.dat` as an encrypted raw-access-token compatibility mirror. New binaries prefer v2 at either supported location, then fall back to the legacy primary/fallback lookup. This costs one additional small encrypted file but lets an older binary keep reading the latest access token after downgrade; a single-file in-place format change would make every older binary send JSON as its bearer and fail immediately.

Alternatives considered:

- **Replace `github_token.dat` with JSON in place:** simplest, but makes rollback require immediate re-login and turns auto-update rollback into an auth outage.
- **Keep metadata only in a sidecar and the access token only in the legacy file:** preserves old readers, but makes the authoritative credential split across two files and complicates crash recovery. The v2 record therefore contains the complete credential; the legacy file is only a mirror.

### 2. Commit credential rotation under a path-scoped cross-process lock

Each credential load records its authoritative path and generation. Refresh acquires an in-process `SemaphoreSlim`, then a bounded file lock associated with that authoritative v2 path. After acquiring both, it reloads the record and skips the network call if another process already committed a newer usable generation. This is required because GitHub refresh tokens rotate and a spent refresh token cannot safely be consumed twice.

Writes use protect-then-atomic-replace in the credential directory: build and encrypt the complete record in memory, write a restrictive temporary file, flush it, and atomically move it over the v2 target. Only after v2 commit does the code refresh the legacy mirror. A mirror failure is logged but does not roll back the authoritative credential. Temporary filenames are explicit and directory-scoped; no broad cleanup is performed.

Alternatives considered:

- **Process-local locking only:** permits two installed bridge copies to consume the same rotating refresh token concurrently.
- **A global named mutex:** harder to make collision-free and permission-correct across Windows, Linux, and macOS than a lock scoped to the actual credential path.

### 3. Model GitHub refresh as part of the AuthService facade

Extend the device-flow response DTO with the OAuth expiry and refresh fields and add the refresh-token grant to `GitHubAuthClient`. No client secret is sent because GitHub's device flow permits refresh with `client_id`, `grant_type=refresh_token`, and `refresh_token`.

GitHub's documented rotation contract says a successful refresh returns a new
access token and a new refresh token, while the submitted refresh token and prior
access token stop working. Therefore an anomalous successful response that has a
new access token but omits the replacement refresh token must never retain the
spent prior token. The bridge commits the new access token as a non-refreshable
generation and logs only `refreshable=false`; interactive login will be required
before its next expiry.

`AuthService` remains the sealed facade. Callers do not read files or call OAuth helpers. It obtains a usable GitHub credential before Copilot token exchange, refreshes proactively when a known access expiry is within a five-minute safety window, and performs one reactive refresh when GitHub returns `401 Bad credentials`. A rejected refresh token produces a typed terminal error instructing the operator to logout/login; it never enters a timer or request hot loop.

`auth whoami`, startup authentication, `auth copilot-status`, and debug model discovery use this same facade so their results cannot disagree about whether a stored credential can refresh.

### 4. Use receipt-relative Copilot deadlines and immutable auth leases

Replace the separate string return plus `CopilotApiBaseUrl` read with an immutable `CopilotAuthLease` returned by `GetCopilotTokenAsync`. The lease contains token, API base URL, local refresh deadline, server hard-expiry value for diagnostics, and a monotonically increasing process-local generation. This preserves the invariant that callers only ask `AuthService` for authentication while eliminating the token/endpoint race.

For scheduling, record the response receipt time and derive the effective local lifetime from `refresh_in`, with the same safety shape as the official client (`receipt + refresh_in + 60 seconds`, refreshed five minutes before that effective expiry). The absolute server `expires_at` remains visible for diagnostics but is not the sole freshness clock. Request-time freshness checks remain authoritative even if the background timer was delayed or failed. Inject `TimeProvider` into the internal implementation for deterministic tests.

Alternatives considered:

- **Keep returning a token string and read the endpoint property separately:** allows a refresh between reads to combine different generations.
- **Trust only `expires_at`:** repeats the known clock-skew failure that the official client explicitly avoids.

### 5. Reject a lease by generation and replay one 401 exactly once

Authenticated Copilot sends are built from one lease. If an authenticated endpoint returns 401, `CopilotClient` disposes the response and asks `GetCopilotTokenAsync` to reject that lease generation. `AuthService` refreshes only when the rejected generation is still current; if another caller already published a newer generation, it returns that lease without a duplicate exchange.

The client rebuilds `HttpRequestMessage` and content from the original `ReadOnlyMemory<byte>` and replays once. A 401 means the request was not authenticated, so one replay is safe; the strict one-replay cap prevents loops if the response actually represents account policy or a persistently invalid credential. This auth replay is counted separately from connection-layer retries but all loops remain explicitly bounded. The helper covers messages, Responses, models, and count-tokens surfaces. Other HTTP statuses retain existing behavior.

Alternatives considered:

- **Only refresh on the next user request:** matches older VS Code behavior but unnecessarily fails the current turn even though the bridge owns replayable bytes.
- **Retry all 401/403 responses:** risks turning organization-policy 403s into repeated token exchanges; only 401 receives same-request replay.

### 6. Use typed failures and secret-free diagnostics

Replace string-matched token failures with bounded typed errors carrying credential layer, HTTP status, and action category. Refresh logs include trigger (`deadline`, `github_401`, `copilot_401`, or timer), outcome, duration, expiry delta, generation, and API host. They never include tokens, refresh tokens, authorization headers, response bodies that may echo credentials, or token hashes.

`auth copilot-status` stops printing `Copilot token (head)`. `auth status` may report storage format, authoritative path, refreshability, and expiry timestamps. Operator errors distinguish invalid GitHub login from Copilot bearer acquisition, policy, and quota failures.

The full Serilog pipeline must be activated while DI constructs the ordered
hosted-service list, not from the first hosted service's `StartAsync`. Generic
Host constructs every hosted service before invoking any `StartAsync` method;
waiting until then leaves `BridgeStartupHostedService` and `AuthService` holding
loggers bound to the disposed bootstrap instance, silently dropping exactly the
startup/auth lifecycle events required here. The first registered hosted
service's constructor is therefore the logger-replacement barrier.

### 7. Build verification around the behavioral contract

Unit tests use fake OAuth/Copilot handlers, an injectable credential path/protector, and `TimeProvider` to cover expiry, rotation, legacy loading, crash-safe commit, concurrency, and bounded replay. Tests are written from the scenarios in `auth-token-lifecycle/spec.md` and mutation-checked by breaking refresh/invalidation branches.

Playground API-contract coverage sends captured request bytes through a scripted first-401/then-success upstream. A DEBUG-only, one-shot real-upstream seam deliberately substitutes an invalid Copilot bearer for the first gpt-5.6 request, allowing a real headless Codex multi-turn, multi-tool task to observe a genuine Copilot 401, refresh, replay, and successful client dispatch. The seam is absent from Release/AOT builds, and the verdict includes the Codex client log as required by the repository acceptance contract.

## Risks / Trade-offs

- **GitHub may omit refresh fields for some accounts** → Store them as optional; non-expiring and legacy tokens continue to work, while a rejected non-refreshable token produces an actionable login error.
- **Rotating refresh tokens create multi-process races** → Use a path-scoped cross-process lock, generation re-check, and authoritative atomic v2 commit.
- **The compatibility mirror can lag if its write fails** → The current binary keeps using committed v2 state and logs the downgrade limitation; logout/login remains the recovery for an old binary.
- **A server could return 401 after partially processing a request** → Rely on HTTP authentication semantics and cap replay at one; never replay other statuses through the auth path.
- **Clock changes can still affect persisted GitHub UTC deadlines** → Refresh early and recover reactively from one GitHub 401; Copilot deadlines use receipt-relative `refresh_in`.
- **A test-only invalid bearer could be mistaken for production behavior** → Compile the seam only in DEBUG, require an explicit behavior-scenario flag, and assert it fires exactly once.
- **Extra storage and locking code increases AOT surface** → Use only BCL primitives and source-generated JSON, then inspect Native AOT binary size after publish.

## Migration Plan

1. Ship readers for v2 and legacy formats before any runtime writes v2.
2. Existing installations continue reading raw encrypted tokens without rewriting them.
3. The next successful interactive login writes v2 plus the legacy mirror. A legacy token cannot be upgraded to refreshable state without GitHub issuing refresh metadata, so it remains legacy until login.
4. Refreshes atomically replace v2 and best-effort update the mirror.
5. Logout deletes v2, mirrors, lock remnants that are safe to remove, and in-memory leases.
6. Rollback binaries read the compatibility mirror. If its update failed or the old binary encounters an expired token, the documented rollback recovery is logout/login.

## Resolved Validation

The affected user's device-flow metadata contained no access expiry or refresh
token, which is a valid OAuth App response rather than a failed exchange. With
the credential files unchanged, the same bridge process crossed the Copilot
refresh deadline, the old bearer's hard expiry, and the prior approximately
one-hour failure window while live gpt-5.6 requests remained 200; `whoami`, a
fresh Copilot-token exchange, and 39-model discovery also succeeded. If GitHub
later returns `Bad credentials`, collect request id/time only, check the GitHub
security log for `oauth_authorization.destroy`, and investigate server-side
revocation or a supported alternate GitHub credential source; do not misclassify
it as local expiry.
