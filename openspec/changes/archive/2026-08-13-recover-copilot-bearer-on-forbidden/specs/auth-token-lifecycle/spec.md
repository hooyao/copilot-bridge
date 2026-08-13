## ADDED Requirements

### Requirement: Copilot CAPI authentication rejection triggers one safe replay

The system SHALL treat the first 401 or 403 from an authenticated Copilot CAPI
endpoint as rejection of the exact bearer/endpoint lease generation used for that
request. It SHALL obtain the already-newer or freshly minted immutable lease,
rebuild the request, and replay it at most once. The replay SHALL preserve the
original request body bytes, non-authentication headers and semantics, shared
transient-retry accounting, and per-send timeout behavior.

One authentication replay SHALL be the total bound even when the two sends return
different rejection statuses. A replayed 401 SHALL be terminal authentication
failure; a replayed 403 SHALL retain terminal policy/entitlement meaning. Statuses
other than 401/403 SHALL NOT enter this lease-rejection replay.

#### Scenario: Current Copilot bearer receives 401

- **WHEN** `/v1/messages`, `/responses`, `/models`, or `/v1/messages/count_tokens` returns 401 for the current lease
- **THEN** the system disposes that response, rejects the used generation, obtains its replacement, and sends one exact replay.

#### Scenario: Current Copilot bearer receives ambiguous 403

- **WHEN** an authenticated CAPI endpoint returns 403 for the current lease
- **THEN** the system treats the first refusal as ambiguous bearer-or-policy state
- **AND** rejects the used generation, obtains its replacement, and sends one exact replay without requiring a bridge restart.

#### Scenario: Another caller already refreshed the rejected generation

- **WHEN** a request reports 401 or 403 for generation N after generation N+1 has already been published
- **THEN** the system reuses generation N+1 for the one replay without issuing a redundant token exchange.

#### Scenario: Refreshed request remains forbidden

- **WHEN** the one replay after a first 403 also returns 403
- **THEN** the system returns that second response as terminal policy/entitlement
- **AND** performs no further refresh or replay.

#### Scenario: Rejection statuses change across sends

- **WHEN** the first and replayed responses are any sequence of 401 and 403
- **THEN** the system performs no more than one lease refresh/reuse and two authenticated sends total.

#### Scenario: Non-authentication status is unchanged

- **WHEN** Copilot returns 400, 402, 429, a validation response, or another status outside 401/403
- **THEN** the authentication replay mechanism does not resend the request or reinterpret that status as token rejection.

#### Scenario: Replay preserves the request contract

- **WHEN** a POST request carrying body bytes, vision/beta/header overrides, and a consumed transient-retry budget crosses a 401 or 403 replay
- **THEN** both sends carry identical business bytes and headers while each uses its own lease-paired bearer/endpoint and per-send header timer
- **AND** the authentication replay does not reset the transient-retry budget.

## MODIFIED Requirements

### Requirement: Authentication observability never reveals credentials

The system SHALL record secret-free authentication lifecycle events sufficient to
distinguish GitHub credential failure, GitHub refresh failure, Copilot bearer
refresh, a recoverable first CAPI 401/403 rejection, a successful authentication
replay, and terminal authentication, policy, quota, or rate-limit outcomes. A
first 403 SHALL NOT be labelled definitive policy/entitlement before the bounded
fresh-lease replay completes. CLI diagnostics and logs SHALL NOT emit an access
token, refresh token, Authorization header, token prefix, response body containing
credential material, or token-derived identifier.

#### Scenario: Authentication refresh succeeds

- **WHEN** a GitHub or Copilot token refresh completes
- **THEN** logs identify the credential layer, trigger, outcome, expiry timing, and API host without any credential bytes.

#### Scenario: First CAPI 403 begins bounded recovery

- **WHEN** CAPI returns the first 403 for lease generation N
- **THEN** logs identify status 403, generation N, and the one replay attempt
- **AND** do not yet label the account definitively policy-ineligible.

#### Scenario: CAPI 403 persists after replay

- **WHEN** the refreshed/reused lease replay also returns 403
- **THEN** logs classify the second response as terminal policy/entitlement after authentication replay without exposing either lease.

#### Scenario: Copilot status is requested

- **WHEN** the operator runs `auth copilot-status`
- **THEN** the command reports status, expiry, and API base URL without printing any portion of the Copilot bearer.

#### Scenario: Authentication fails terminally

- **WHEN** refresh or token exchange cannot recover
- **THEN** the operator-facing error identifies whether GitHub login, Copilot bearer acquisition, account policy, or quota requires action without embedding secrets.

#### Scenario: Hosted authentication lifecycle is logged

- **WHEN** the bridge constructs hosted services and performs startup authentication
- **THEN** startup and authentication lifecycle events reach the full rolling log rather than a disposed bootstrap logger.

## REMOVED Requirements

### Requirement: Copilot inference 401 triggers one safe authentication replay

**Reason**: Production demonstrated that Copilot can express a stale short-lived
bearer as HTTP 403, and the official client invalidates its token on both 401 and
403. Restricting recovery to 401 leaves the rejected lease cached until timer or
restart.

**Migration**: Use `Copilot CAPI authentication rejection triggers one safe
replay`, which preserves the old 401 behavior and adds one bounded 403 recovery.

#### Scenario: Legacy 401-only recovery is superseded

- **WHEN** an authenticated CAPI request receives 401 or 403
- **THEN** the new bounded rejection contract applies instead of retaining a 403-rejected lease.
