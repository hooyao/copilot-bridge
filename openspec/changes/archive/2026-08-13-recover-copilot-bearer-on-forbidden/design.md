## Context

Production evidence shows one failure chain across inference and metadata:

- a native Codex `/responses` send received HTTP 403 with the plain body
  `forbidden\n` and Copilot Kubernetes/request-id headers;
- Codex reported five failed attempts, consistent with its bounded provider HTTP
  retry layer;
- later `/models` refreshes received the same 403 on each client catalog poll;
- the scheduled Copilot bearer refresh eventually published generation 8; and
- restarting the bridge recovered immediately because the process-local bearer
  cache was discarded and a new token/endpoint lease was minted.

The current bridge replays an authenticated CAPI call only after 401. It classifies
every 403 as `policy_or_entitlement` without invalidating the lease, despite the
official VS Code Copilot token manager clearing its cached token after both 401 and
403. This makes an ambiguous stale-bearer 403 persist until the timer or restart.

The Codex catalog endpoint already fails open: a live overlay failure returns the
reviewed exact-version baseline (or a stale last-known-good overlay), not the
upstream 403. However, when no fresh last-known-good entry exists, each periodic
Codex catalog request starts another `/models` refresh and warning.

Constraints remain Native AOT, source-generated JSON, the sealed `AuthService`
facade, one immutable bearer+endpoint generation per send, exact request-byte
replay, no credential logging, and no unbounded retry.

## Goals / Non-Goals

**Goals:**

- Recover the current CAPI request when a stale bearer is expressed as either 401
  or 403, without requiring a bridge restart or consuming all client retries.
- Reject only the lease generation used by the failed request; reuse an already
  published newer generation when one exists.
- Preserve request body bytes, business headers, endpoint pairing, transient retry
  accounting, and per-send timeout semantics across the authentication replay.
- Keep persistent 403 policy/entitlement failures bounded and accurately logged.
- Suppress repeated live-overlay refreshes during an explicit operator-visible
  cooldown while preserving safe catalog output.
- Verify the behavior through contract tests and a real Codex complex-tool run
  that crosses a forced first-403 recovery path and is judged from Codex's own log.

**Non-Goals:**

- Retrying arbitrary 4xx responses or treating 400, 402, 429, validation, quota,
  or transport errors as authentication failures.
- Refreshing the persisted GitHub OAuth credential merely because CAPI returned
  403; this rejection targets the short-lived Copilot lease first.
- Hiding a persistent policy/entitlement 403, retrying it indefinitely, or changing
  Copilot account policy.
- Changing Codex request/stream retry counts or the client-facing model envelope.
- Persisting live-overlay failure state across bridge restarts.

## Decisions

### A first 403 receives the same bounded lease replay as a first 401

`CopilotClient.SendAuthenticatedAsync` will recognize 401 and 403 as CAPI lease
rejections on the first response. It disposes the rejected response and single-use
request, calls the existing `AuthService.GetCopilotTokenAsync` rejection path with
the status-specific reason, rebuilds the request from the retained body/header
inputs and paired endpoint, and sends one replay.

The first 403 is ambiguous, not yet terminal policy evidence. A second 403 after a
newer/fresh lease is strong evidence that refresh cannot repair the refusal, so it
is returned unchanged and classified `policy_or_entitlement_after_auth_replay`.
A second 401 remains terminal Copilot authentication rejection. The shared
`authReplayUsed` flag bounds mixed sequences too (`401 -> 403` or `403 -> 401`):
one authentication replay total per bridge request.

Alternative: invalidate the lease but return the first 403 and rely on Codex's
next retry. Rejected because Claude Code or metadata callers may not retry, it
still spends one visible client attempt, and the bridge already retains everything
needed for a safe exact replay. Alternative: replay every 403 repeatedly. Rejected
because real policy failures must remain terminal and bounded.

### Lease rejection carries a typed reason through the sealed facade

The auth API will retain `GetCopilotTokenAsync` as the only caller surface and add
a small internal rejection reason (`Unauthorized` or `Forbidden`) alongside the
rejected lease. `AuthService` uses it only for secret-free trigger/classification
logs (`copilot_401` / `copilot_403`); generation comparison remains the authority:

- if generation N is still current, clear it, stop its timer and mint N+1;
- if another caller already published N+1, return N+1 without another exchange.

No response body, token bytes, token prefix, Authorization value, or token-derived
identifier enters diagnostics.

Alternative: pass arbitrary strings or `HttpStatusCode` into `AuthService`.
Rejected because a closed enum keeps the security-relevant rejection set explicit
and prevents an unrelated status from silently gaining refresh semantics.

### Persistent 403 classification happens only after the refreshed attempt

The existing `policy_or_entitlement` label is currently emitted for the first 403
and overstates what the status proves. The recovery log will instead state that
status 403 rejected generation N and a bounded replay is starting. Only the replay
response receives terminal classification. This cleanly distinguishes:

- recovered stale bearer (`403 -> refresh/reuse -> success`);
- terminal policy/entitlement (`403 -> refresh/reuse -> 403`); and
- terminal authentication (`401 -> refresh/reuse -> 401`).

### Live-overlay failures are negative-cached for an exact configured cooldown

`CodexCatalogOverlayService` will record a process-local `retryAfter` only when a
shared refresh fails. `GetAsync` behavior under its lock becomes:

1. return a fresh validated overlay while its normal TTL is live;
2. join the one in-flight refresh when present;
3. before `retryAfter`, return the stale last-known-good overlay, or an empty
   unvalidated overlay on a cold failure, without upstream I/O or another warning;
4. after the cooldown, allow exactly one new shared refresh.

Success atomically replaces last-known-good facts and clears failure state. A
failed attempt logs one warning with `retry_in_seconds`; suppressed polls remain
quiet. Caller cancellation of `WaitAsync` does not cancel or poison the shared
refresh.

The cooldown is configured as
`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds`, defaults to 300 in both
code and stock `appsettings.json`, is used exactly, and is startup-validated in
the range 1..3600. This avoids another hidden retry magic number while bounding
both hammering and recovery delay. It is independent of official Codex source TTL
and confirmed-absence caching.

Alternative: reuse the five-minute successful-overlay TTL implicitly. Rejected
because success freshness and failure retry policy are different operator facts.
Alternative: persist failures. Rejected because a restart/new bearer must be able
to retry immediately and no failure payload should become durable state.

### Verification forces the changed path

Unit tests will mutation-check first-403 replay, exact bytes/headers, mixed/second
terminal statuses, generation races, retry-budget preservation, cooldown timing,
single-flight behavior and warning count. A Debug-only behavior seam will force
one pre-upstream 403 inside the authenticated send path; the replay then uses a
fresh real Copilot lease and live backend. A real Codex app-server task must cross
that seam, execute multiple commands/custom exec, return tool output, and show no
router/dispatch fatal in its own `logs_2.sqlite`. Real Claude coverage will guard
the shared `/v1/messages` path as well.

## Risks / Trade-offs

- **A genuine policy 403 causes one extra token exchange and request** → One replay
  is strictly bounded, the first response proves no model output was accepted, and
  the second 403 is returned/classified terminal.
- **Mixed 401/403 sequences could loop** → One shared replay flag covers both
  statuses and caps the request at two authenticated sends.
- **Cooldown temporarily retains stale capacity** → The exact reviewed baseline or
  last-known-good overlay remains safe, the warning names the retry delay, and the
  maximum default delay is five minutes.
- **A synthetic behavior seam could prove only the seam** → The second send must use
  a newly minted real lease against live Copilot, and the verdict still requires a
  real client tool round-trip plus client-owned dispatch evidence.
- **New configuration drifts on upgrade** → The POCO default activates for old
  appsettings; config migration adds the documented key when an update succeeds.

## Migration Plan

1. Ship the new binary and documented stock setting; no credential or cache-file
   migration is required.
2. On restart, upgraded installations use the 300-second cooldown even when their
   older `appsettings.json` lacks the key.
3. The next ambiguous CAPI 403 refreshes/reuses a lease and replays once; a
   persistent refusal remains visible to the client and operator.
4. Rollback restores the prior 401-only behavior and ignores the new JSON key.

## Open Questions

None. The incident, official-client invalidation behavior, bounded replay policy,
and cooldown ownership are sufficient to implement and verify the change.
