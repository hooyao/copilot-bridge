## 1. Credential Contract and Storage

- [x] 1.1 Write failing unit contracts for refreshable and non-expiring device-flow responses, derived deadlines, and source-generated serialization before changing product code.
- [x] 1.2 Write failing storage contracts for legacy raw-token loading, v2 precedence, encrypted round trips, compatibility-mirror behavior, logout, corrupt records, and zero plaintext credential files.
- [x] 1.3 Add the pinned OAuth/credential DTOs and every required `JsonContext` registration, including optional access/refresh expiry fields and credential generation.
- [x] 1.4 Implement the authoritative encrypted v2 credential record, legacy primary/fallback reader, encrypted raw-token mirror, and complete logout cleanup.
- [x] 1.5 Write concurrency and failure-injection contracts, then implement path-scoped cross-process locking, generation re-check, restrictive temporary files, flush, and atomic v2 replacement.
- [x] 1.6 Mutation-check the storage contracts by breaking v2 precedence, encryption, legacy loading, and pre-commit failure handling and confirming each mutation makes the intended test fail.

## 2. GitHub OAuth Refresh Lifecycle

- [x] 2.1 Write failing contracts for five-minute proactive refresh, non-expiring reuse, rotating refresh-token persistence, refresh rejection, and one reactive retry after GitHub `401 Bad credentials`.
- [x] 2.2 Extend `GitHubAuthClient` and response models with the device-flow expiry fields and the documented refresh-token grant, using typed bounded failures rather than message matching.
- [x] 2.3 Refactor `AuthService` to obtain a usable GitHub credential under single-flight locking, refresh before known expiry, re-read after the cross-process lock, and preserve the last committed record on failure.
- [x] 2.4 Route startup auth, `auth whoami`, `auth copilot-status`, and debug model discovery through the refresh-capable facade so all surfaces classify an invalid GitHub credential consistently.
- [x] 2.5 Add contracts proving a missing/expired refresh token produces one actionable logout/login error with no timer or request hot loop and no credential loss.
- [x] 2.6 Mutation-check proactive and reactive GitHub refresh by disabling the expiry boundary, rotation save, 401 retry cap, and refresh-failure terminal path in turn.

## 3. Atomic Copilot Authentication Lease

- [x] 3.1 Write failing contracts for receipt-relative `refresh_in` scheduling, clock-skew tolerance, the five-minute safety window, and atomic token/base-URL generations.
- [x] 3.2 Introduce the immutable `CopilotAuthLease` and update `GetCopilotTokenAsync` to return or reject a lease generation while keeping `AuthService` as the only lifecycle facade.
- [x] 3.3 Replace separate token/endpoint reads in `CopilotClient`, startup reporting, commands, Playground helpers, and test fakes with one lease snapshot.
- [x] 3.4 Make background refresh outcomes observable and keep request-time freshness authoritative when a timer is delayed or a refresh attempt fails.
- [x] 3.5 Mutation-check the lease contracts by forcing absolute-expiry-only scheduling and by pairing token generation N with endpoint generation N+1.

## 4. Bounded Copilot 401 Recovery

- [x] 4.1 Write failing contracts for first-401 refresh/replay on `/v1/messages`, `/responses`, `/models`, and `/v1/messages/count_tokens`, including byte-identical bodies and preserved non-auth headers.
- [x] 4.2 Add contracts for concurrent stale-generation rejection, a second terminal 401, and no auth replay for 400/402/403/429 or successful responses.
- [x] 4.3 Implement a shared authenticated-send path that disposes the rejected response, refreshes only the lease generation used, rebuilds the single-use request, and replays at most once.
- [x] 4.4 Verify and test the combined bounds with existing connection-layer retries and first-byte timeouts so neither retry mechanism resets or multiplies the other's budget unexpectedly.
- [x] 4.5 Mutation-check the 401 contracts by removing lease invalidation, permitting a second auth replay, and reserializing or altering the original request body.

## 5. Secret-Safe Diagnostics and Documentation

- [x] 5.1 Write failing tests proving auth logs and CLI output contain no access token, refresh token, Authorization value, token prefix, or token-derived fingerprint on success and every failure path.
- [x] 5.2 Implement typed authentication lifecycle logs and actionable error classification for GitHub login, GitHub refresh, Copilot bearer acquisition, upstream bearer rejection, policy, and quota outcomes.
- [x] 5.3 Remove `Copilot token (head)` from `auth copilot-status` and report only storage kind/path, refreshability, expiry timing, and Copilot API host where appropriate.
- [x] 5.4 Update `docs/pipeline-design.md` first for the facade/lease and authenticated replay contract, then update `docs/token-storage.md`, `docs/copilot-api-research.md`, `docs/design.md`, README troubleshooting, and command help.
- [x] 5.5 Audit every new DTO and serialization call for Native AOT source generation and verify Windows DPAPI remains the only Windows-attributed surface.

## 6. Contract and Real-Client Verification

- [x] 6.1 Add Playground `Kind=ApiContract` coverage that drives captured request bytes through a scripted first-401/then-success exchange and asserts one refresh, one byte-identical replay, and no secret leakage.
- [x] 6.2 Add a DEBUG-only one-shot behavior seam that sends an invalid Copilot bearer on the first real gpt-5.6 request, is absent from Release/AOT, and records that the genuine upstream 401 recovery path fired exactly once.
- [x] 6.3 Run `dotnet test tests/CopilotBridge.UnitTests` and `dotnet test CopilotBridge.slnx --filter "Category!=Integration"`, resolving product defects without weakening contract assertions.
- [x] 6.4 Run the real `Kind=ClientBehavior` gpt-5.6 multi-turn, multi-tool task through the one-shot 401 seam and use the real-client verification workflow to confirm tool execution and no Codex router/dispatch fatal in the client's own log.
- [x] 6.5 With an affected user, capture only OAuth field presence and expiry durations after login, then verify the bridge crosses the prior failure window without deleting the token file; never collect credential values. *(Verified 2026-08-11: v2, `refreshable=False`, access expiry unknown, no refresh expiry. The same bridge process and unchanged credential files crossed the observed Copilot refresh deadline, old bearer's hard expiry, and the full prior approximately one-hour window; live gpt-5.6 requests remained 200 through 23:05, while `whoami`, Copilot-token exchange, and 39-model discovery all succeeded. Official OAuth documentation and the protocol reference confirm the field shape is a valid non-refreshable response, not a failed exchange.)*
- [x] 6.6 Publish Windows Native AOT with `build-aot.bat`, verify the release binary and updater packaging still succeed, inspect binary size, and record any size change in `docs/size-history.md`.
- [x] 6.7 Review final logs, traces, test artifacts, and git diff for credential disclosure, temporary auth files, unrelated changes, and any unverified acceptance item before declaring the fix complete.

## 7. PR Review Follow-ups

- [x] 7.1 Key terminal rejection and stale-refresh checks on a persisted credential-instance identity plus generation so a fresh login cannot collide with the rejected credential.
- [x] 7.2 Classify transient OAuth refresh failures separately from invalid/expired/revoked refresh credentials, and mark genuine proactive rejection terminal without repeated network use.
- [x] 7.3 Keep the auth-replay ClientBehavior case on the shared reviewed Codex version and re-run the real-client verdict. *(Verified with Codex 0.147.0-alpha.6.6: three completed custom-exec call/output loops, one genuine bearer refresh/replay, canary present, no abort, SQLite router fatal=0 and ERROR=0.)*
- [x] 7.4 Isolate the process-global Serilog ordering contract from parallel tests and restore the original logger after the test.
