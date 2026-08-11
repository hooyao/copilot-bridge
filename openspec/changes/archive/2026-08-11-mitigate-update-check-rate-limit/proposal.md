# Mitigate the shared-IP rate limit on the startup update check

## Why

The startup update check reports

```
[WRN] Auto-update check failed (GitHub API rate limit reached); copilot-bridge
      will continue with the current version.
```

on most startups, on a machine that makes **one** update request per launch.

The cause is not the bridge's own request volume. Anonymous GitHub API quota is
60 requests/hour keyed on the **source IP**, and the reporting machine reaches
GitHub through a NAT whose egress is a rotating pool of eight addresses
(`104.43.2.72`–`.79`). The 60/hour bucket is shared with every other client on
that address, so the bridge inherits an exhausted bucket it did not drain — the
403 body names the address, not the caller:

```
API rate limit exceeded for 104.43.2.79.
```

Discovery treats the first 403/429 as final, so a shared-bucket refusal — a
transient property of one egress address — is reported as a failed update check.

## What Changes

- Discovery **retries** a 403/429 instead of failing on the first refusal,
  bounded by a retry count, a spacing delay, and the existing traversal deadline.
- Discovery runs on a handler that does **not** reuse connections
  (`PooledConnectionLifetime = TimeSpan.Zero`), which is what makes the retry
  effective: a pooled connection pins every request to a single egress address,
  so a retry re-hits the same exhausted bucket.
- The fail-open contract is unchanged. Exhausting the retries still logs the same
  warning and starts the current version.

Measured live against the GitHub API from the affected NAT, after exhausting one
egress address's quota:

| retry strategy | recovered |
| --- | --- |
| reuse the pooled connection | 0 / 8 |
| open a fresh connection | 8 / 8 (≤2 attempts, 2.1s across 8 checks) |

End-to-end, with the pool drained and a real bridge started immediately after:
the pre-change build logged the reported warning; the post-change build completed
discovery and found the release.

## Scope and limits

This is a **mitigation, not a fix**. It recovers the case where *some* address in
the pool still has quota. It cannot help when:

- every address in the pool is exhausted — measured at full exhaustion, recovery
  was 0/10 even at 20 attempts over 10s; or
- the egress is a single address, where there is nothing to re-select.

Those cases still fail open with the same warning, after a bounded retry. Quota
resets on a fixed hourly window rather than a sliding one (a fully drained pool
measured 0% for 6 minutes and recovered at the window boundary), so
`EnableAutoUpdate: false` and manual updating remain the durable escape hatches.

Authenticating discovery would raise the limit to 5000/hour and key it on the
account instead of the IP, but the update check is anonymous **by design** (see
the trust boundary in `docs/auto-update.md`) — it holds no GitHub token and must
not acquire one. Conditional `If-None-Match` requests were also measured: GitHub
still counts a 304 against the quota, so they do not help here.

## Impact

- Affected specs: `startup-update-discovery`
- Affected code: `src/CopilotBridge.Cli/Update/GitHubReleaseClient.cs`,
  `src/CopilotBridge.Cli/Update/StartupUpdateGate.cs`
- No configuration surface is added; the retry policy is internal, like the
  existing timeouts.
