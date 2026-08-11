# Tasks

## 1. Diagnosis (complete)

- [x] 1.1 Reproduce the reported warning against the live GitHub API
- [x] 1.2 Establish the mechanism: anonymous quota is 60/hour per **source IP**,
      shared with everyone behind the NAT (403 body names the address)
- [x] 1.3 Establish that the egress is a rotating pool (`104.43.2.72`–`.79`)
- [x] 1.4 Establish that a pooled connection pins to one address (10/10 same IP)
      while fresh connections rotate (10 requests → 6 addresses)
- [x] 1.5 Measure the recovery ceiling: at FULL pool exhaustion, 0/10 recovered
      even at 20 attempts over 10s; quota resets on a fixed hourly window
- [x] 1.6 Rule out conditional requests: GitHub counts a 304 against the quota

## 2. Implementation

- [x] 2.1 Add a bounded, spaced rate-limit retry to `GitHubReleaseClient`
- [x] 2.2 Never start a retry whose delay would cross the overall deadline
- [x] 2.3 Count retries per page; reset after a page is fetched
- [x] 2.4 Exempt only the retry re-fetch from the pagination cycle guard
- [x] 2.5 Add `CreateDiscoveryHandler()` with `PooledConnectionLifetime = Zero`
      and use it from `StartupUpdateGate`
- [x] 2.6 Move the retry wait onto `IMonotonicClock` so tests stay sleep-free

## 3. Tests (contract-first, mutation-verified)

- [x] 3.1 Retry recovers a transient 403 and a transient 429
- [x] 3.2 Persistent rate limit still fails open after the allowance
- [x] 3.3 Retry allowance is honoured exactly
- [x] 3.4 Retry never carries the traversal past the overall deadline
- [x] 3.5 Retries are spaced, not a hot loop
- [x] 3.6 Retry does not trip the cycle guard; a real cycle is still detected
- [x] 3.7 Shutdown cancellation is not swallowed by the retry loop
- [x] 3.8 The discovery handler does not reuse connections
- [x] 3.9 A non-rate-limit 403 is not retried; a secondary limit signalled by
      retry-after is
- [x] 3.10 Mutation-check every new test — 7 mutations, all killed. Mutation 3
      (removing the pre-retry deadline guard) initially SURVIVED, proving the
      deadline test asserted nothing; it was rewritten to assert elapsed time
      rather than attempt count, which killed it.

## 4. Live verification

- [x] 4.1 A/B the two retry strategies against the real API from an exhausted
      address: pooled 0/8 vs fresh 8/8
- [x] 4.2 End-to-end control: drain the pool, start the pre-change bridge,
      confirm it logs the reported warning
- [x] 4.3 End-to-end fixed: drain the pool, start the post-change bridge,
      confirm discovery succeeds and finds the release
- [x] 4.4 Re-verify once the hourly quota window resets, so the fixed build is
      exercised against realistic PARTIAL contention rather than a fully
      drained pool. Measured at a 47% single-attempt success rate (i.e. the old
      behaviour would warn on ~53% of startups): fixed build **5/5** startups
      completed the check, control build **2/5** (3 warned). Retry cost was
      ~280ms per startup on average.

## 5. Retry only what a retry can clear

- [x] 5.1 Gate the retry on GitHub actually attributing the refusal to the rate
      limit (`x-ratelimit-remaining: 0`, or `retry-after` for the secondary
      limit), so a blocked-repository 403 fails open immediately
- [x] 5.2 Contract tests for both branches, mutation-checked (mutations 6 and 7)

## 7. PR review follow-ups (PR #69)

- [x] 7.1 Round 1 (Copilot): `Retry-After` was used only to CLASSIFY the refusal
      while every retry still waited the default 250ms — so a real secondary
      limit (`Retry-After: 60`) would burn the whole allowance while the server
      was guaranteed to keep refusing, and could prolong the abuse limit. It now
      sets the wait (default spacing as a floor), and a mandated wait that cannot
      fit inside the traversal deadline fails open WITHOUT a further request.
      Both RFC 9110 forms are parsed (delta-seconds and HTTP-date).
      Mutation-checked: mutations 8 (ignore the header) and 9 (no floor) killed.

## 6. Documentation

- [x] 6.1 Document the mechanism, the measured numbers, and the explicit limits
      in `docs/auto-update.md`
