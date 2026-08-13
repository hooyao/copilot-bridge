## Why

A real Codex turn received five consecutive `403 forbidden` responses until the
bridge was restarted; restart immediately recovered because it discarded the
in-memory Copilot bearer and minted a new lease. The bridge currently treats every
CAPI 403 as definitive policy/entitlement, even though the official Copilot client
invalidates its cached bearer on both 401 and 403, so a stale bearer remains active
until the scheduled refresh while inference and `/models` continue failing.

## What Changes

- Treat the first 403 from an authenticated Copilot CAPI endpoint as an ambiguous
  bearer-or-policy rejection: reject only the lease generation used, obtain the
  already-newer or freshly minted bearer/endpoint lease, and replay the exact
  request once.
- Keep recovery bounded. A second 403 is terminal and retains
  policy/entitlement meaning; no 400, 402, 429, validation, quota, or transport
  response enters this authentication replay.
- Make authentication logs identify the status, rejected generation, refresh
  trigger, replay, and final classification without exposing credential bytes.
- Cache failed live `/models` overlay refreshes for a configured bounded cooldown
  (`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds`, default `300`) so Codex's
  periodic catalog polling cannot hammer the same terminal failure or emit one
  warning every poll. Continue serving the reviewed baseline or stale
  last-known-good overlay during the cooldown.
- Add contract and real-client verification for `403 -> new bearer -> successful
  replay`, persistent-policy 403, generation races, request-byte fidelity, and
  catalog failure cooldown.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `auth-token-lifecycle`: Extend generation-aware, single-replay CAPI recovery
  from 401 to the ambiguous 403 rejection observed in production while preserving
  terminal policy classification after one refreshed attempt.
- `codex-model-catalog`: Add a bounded negative-cache/cooldown for failed live
  Copilot metadata overlays while continuing to serve safe catalog capacity.

## Impact

This affects `CopilotClient`, the sealed `AuthService` facade and lease-rejection
contract, Codex catalog overlay caching, authentication/catalog diagnostics,
their unit tests, durable auth/catalog documentation, and real Codex/Claude
behavior verification. It changes no client-facing request shape, adds no
dependency, and preserves Native AOT constraints.
