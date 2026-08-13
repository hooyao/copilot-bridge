## ADDED Requirements

### Requirement: Operator controls live-overlay failure cooldown

`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds` SHALL control the
process-local cooldown after a failed live Copilot `/models` overlay refresh. It
SHALL default to 300 seconds in both code and stock `appsettings.json`, so upgraded
installations whose existing file lacks the key receive the protection. The exact
positive configured value SHALL be used and startup SHALL reject values outside
1..3600 seconds with the full key and supported range.

This cooldown SHALL be independent of official Codex source freshness,
confirmed-absence caching, client request retries, and inference behavior.

#### Scenario: Upgraded configuration uses documented default

- **WHEN** an existing installation has no `LiveOverlayFailureCooldownSeconds` key
- **THEN** a failed live overlay refresh suppresses new attempts for 300 seconds.

#### Scenario: Explicit cooldown is exact

- **WHEN** the operator configures a valid positive cooldown
- **THEN** the next live overlay attempt is eligible after exactly that duration without a hidden margin, clamp, or substitute.

#### Scenario: Invalid cooldown fails before serving

- **WHEN** the configured value is below 1 or above 3600 seconds
- **THEN** startup fails with an actionable validation error naming the key, value, and accepted range.

## MODIFIED Requirements

### Requirement: Metadata refresh is bounded and fail-open

The bridge SHALL coalesce concurrent Copilot model refreshes, cache only a
validated last-known-good overlay for a bounded process-local TTL, and atomically
replace it after successful validation. A Copilot metadata timeout or error SHALL
reduce catalog freshness or capacity but SHALL NOT prevent the bridge from
returning a version-compatible safe catalog.

Every failed shared refresh SHALL establish one process-local retry deadline from
`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds`. Before that deadline,
catalog calls SHALL return the stale last-known-good overlay, or the reviewed
official baseline on a cold failure, without another upstream `/models` request or
duplicate warning. After the deadline, concurrent callers SHALL again share at
most one refresh. A successful refresh SHALL clear prior failure state. Failure
state SHALL NOT be persisted and a process restart SHALL permit an immediate
attempt.

#### Scenario: Concurrent Codex starts share one refresh

- **WHEN** multiple supported clients request the catalog while no fresh overlay exists and no failure cooldown is active
- **THEN** the bridge performs at most one concurrent Copilot `/models` refresh and all callers receive a valid catalog result.

#### Scenario: Refresh failure uses last known good facts

- **WHEN** Copilot refresh fails after the process has cached a validated overlay
- **THEN** the endpoint serves that last-known-good overlay with one warning rather than returning malformed or partially updated limits.

#### Scenario: Cold overlay failure uses official baseline

- **WHEN** the first Copilot refresh fails and no validated overlay exists
- **THEN** the endpoint returns the resolved exact official Codex baseline with no live context uplift and only statically known bridge-routable models enabled.

#### Scenario: Periodic polls are suppressed during cooldown

- **WHEN** Codex requests `/codex/models` repeatedly before the failure retry deadline
- **THEN** every request receives the same safe stale/baseline catalog result
- **AND** no additional Copilot `/models` call or refresh-failure warning occurs.

#### Scenario: Cooldown expiry permits one shared retry

- **WHEN** the configured cooldown expires and multiple catalog callers arrive
- **THEN** exactly one new live-overlay refresh is attempted and the callers share its result.

#### Scenario: Successful retry restores normal freshness

- **WHEN** a post-cooldown refresh succeeds
- **THEN** its validated models atomically replace the prior overlay and clear the failure deadline
- **AND** normal successful-overlay TTL behavior resumes.

#### Scenario: Restart does not preserve a terminal failure

- **WHEN** a bridge process restarts during a live-overlay failure cooldown
- **THEN** no persisted failure entry prevents the new process from attempting `/models` immediately.
