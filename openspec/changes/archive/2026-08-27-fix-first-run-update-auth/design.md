## Context

Updater-managed target and rollback processes currently skip all credential access before `Ready`, which correctly prevents an external authentication failure from deciding whether a binary update commits. The readiness reporter sends `Ready` after `ApplicationStarted`, but nothing resumes the ordinary startup authentication path afterward. A credential-free replacement therefore serves indefinitely without initiating the device-code flow that an ordinary launch presents.

Credential migration also coordinates with binaries that still write `github_credentials.v2.dat` by taking the unified lock and the historical v2 lock. The store currently opens both unconditionally, so a fresh installation with no current or legacy credential creates a permanent historical lock artifact despite having no legacy state.

## Goals / Non-Goals

**Goals:**

- Keep update commit dependent only on local serving health.
- Restore the first-run device-code flow after a successful readiness send.
- Keep post-readiness authentication errors non-fatal to the already-serving bridge.
- Avoid introducing the historical v2 lock on a fresh credential lifecycle.
- Preserve migration and downgrade coordination when legacy files or a historical lock already exist.

**Non-Goals:**

- Change the update wire format or add a commit acknowledgement.
- Make GitHub availability part of target or rollback readiness.
- Delete historical lock files that already exist.
- Change credential formats, OAuth providers, or endpoint authentication semantics.

## Decisions

### Resume authentication in the readiness reporter after sending Ready

`UpdateReadinessReporter` will sequence two operations in its existing `ApplicationStarted` background task: first send the authenticated `Ready` message; only when that transport send succeeds, run the same authentication bootstrap used by an ordinary launch. Because the callback remains fire-and-forget after `ApplicationStarted`, OAuth polling cannot delay listener startup or the readiness message. Authentication failure is logged as an actionable warning and is not rethrown into host lifetime.

This keeps ordering explicit in one component. A separate hosted service was rejected because `ApplicationStarted` callback ordering would not prove that authentication starts after `Ready`. Running authentication in `BridgeStartupHostedService` was rejected because it would restore the rollback loop that readiness decoupling removed.

The current readiness wire has no commit acknowledgement. A successful send proves delivery to the updater, not final validation, so a malformed replacement may briefly begin authentication before the updater terminates it. Extending the frozen cross-version wire for this UX fix is disproportionate; the replacement uses the exact context to construct its message, and the updater remains authoritative for commit.

### Acquire the historical lock only for pre-existing legacy state

Every mutation continues to acquire `github_credentials.dat.lock` first. While holding it, the store checks for either legacy credential input or an already-existing `github_credentials.v2.dat.lock`. Only then does it acquire the historical lock before rechecking, migrating, saving, cleaning up, or logging out. Once a historical lock exists, it is never unlinked, preserving stable filesystem identity for installations that crossed the migration boundary.

A completely fresh lifecycle skips the historical lock during empty lookup, first login save, and empty logout. The authoritative lock still serializes current-format writers. This deliberately narrows cross-version coordination to installations with observable legacy state; an old binary that begins its first legacy write in the small interval after the absence check is outside that fresh-state guarantee.

## Risks / Trade-offs

- **The updater can receive but reject a Ready message** → Post-readiness auth may start briefly before that child is terminated; no authentication result influences commit and no wire change is introduced.
- **A legacy writer can appear after an absence check on a genuinely fresh directory** → The unified lock still protects current writers, and any observed legacy file or historical lock restores ordered dual-lock coordination. Existing migration installations never unlink their historical lock.
- **Background device flow outlives the readiness task** → It uses host shutdown cancellation and logs failure without terminating the host.

## Migration Plan

No data migration is required. Existing historical locks remain untouched. Fresh installations stop creating the historical lock unless legacy state appears. Rollback restores the previous binary and its existing credential artifacts unchanged.

## Open Questions

None.
