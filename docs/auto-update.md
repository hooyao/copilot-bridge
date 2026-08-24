# Startup auto-update

Copilot Bridge can update itself at startup. The check is **serve-only**,
**synchronous**, and **fail-open**: it runs once before the proxy binds its
port, and any problem (offline, rate-limited, a bad release, a failed install)
leaves the current version serving.

## What triggers a check

Only the proxy entry points check:

```
copilot-bridge                # parameterless == serve
copilot-bridge serve
copilot-bridge serve --port N
```

Maintenance commands never check: `auth …`, `config …`, `debug …`, `--help`,
`--version`. A local development build (a version whose prerelease contains
`dev`, e.g. `0.1.0-dev`) never self-updates, so `dotnet run` / a dev publish
directory is never replaced by a release archive.

## Configuration

```jsonc
{
  "AutoUpdate": {
    "EnableAutoUpdate": true,   // master switch; false = never check, no GitHub request
    "AllowBetaUpdates": false   // true = GitHub prereleases (beta/rc/alpha) also qualify
  }
}
```

These **code defaults** (enabled, stable-only) are what make the feature
on-by-default — not the stock JSON. An installation upgraded from a build that
predates this section has no `AutoUpdate` keys in its *own* `appsettings.json`,
so its **pre-update gate** relies on the POCO defaults. Configuration migration
walks the *new* template and keeps new-only keys, so a successful update **does**
write this section into the merged file — but only after that gate has already
run, which is why the defaults live in code, not solely in the stock JSON.

`AllowBetaUpdates=true` does not *prefer* betas — it only widens the candidate
set. The highest [Semantic Versioning 2.0](https://semver.org) version in the
allowed set wins, independent of publication order, and only a version strictly
greater than the installed one is offered (no downgrade, no reinstall).

## The flow

```
load appsettings.json
        │
   serve entry?  ── no ─────────────► run the maintenance command
        │ yes
   EnableAutoUpdate?  ── no ────────► start the proxy
        │ yes
   anonymous GitHub Releases check (bounded)
        │
   ├── check failed ── warn ────────► start the current version
   ├── no newer eligible release ───► start the current version
   └── newer release
          │
     print full release notes
          │
     Install this update now? [y/N]
          │
     ├── no / non-interactive ──────► start the current version
     └── yes ─────────────────────► hand off to copilot-updater
```

The check has three bounds so it can never keep the proxy from starting: a
finite per-request timeout, one wall-clock deadline for the whole paginated
traversal, and a defensive page cap (plus pagination-cycle detection).
Application-shutdown cancellation (Ctrl-C) stays shutdown — it is not turned
into a warning.

### Shared-IP rate limiting

Discovery is anonymous, and GitHub's anonymous quota is **60 requests/hour keyed
on the source IP**. A single bridge spends one request per startup, so it cannot
exhaust that budget by itself — but behind a corporate or cloud NAT the bucket is
shared with every other client on the same address, which is why the check can
report

```
Auto-update check failed (GitHub API rate limit reached)
```

on a machine that has barely used it. A 403/429 therefore describes the shared
bucket at that instant, not a verdict on this check, so discovery **retries** it
(bounded, spaced, and still inside the traversal deadline) instead of giving up
on the first refusal.

The retry only helps because discovery runs on a handler with
`PooledConnectionLifetime = TimeSpan.Zero`. Measured live from a NAT with an
8-address egress pool:

| retry strategy | recovered |
| --- | --- |
| reuse the pooled connection | **0 / 8** |
| open a fresh connection | **8 / 8** (≤2 attempts, 2.1s for 8 checks) |

A pooled connection pins every request to one egress address (10/10 requests, one
IP), so retrying on it re-hits the exact bucket that just returned
`remaining: 0`. A fresh connection re-runs source-address selection (10 requests
→ 6 distinct addresses) and can land on an address that still has quota.

Only a refusal GitHub attributes to the rate limit is retried — an exhausted
counter (`x-ratelimit-remaining: 0`) or a `retry-after` signal for the secondary
limit. A 403 carrying neither (a blocked repository, a banned user-agent) fails
open immediately rather than spending the traversal budget on a retry that cannot
clear it.

`Retry-After` is obeyed as an instruction, not just read as a classifier: it sets
the wait (with the default spacing as a floor), because retrying before it elapses
is both guaranteed to be refused and liable to extend a secondary limit. When the
mandated wait does not fit inside the traversal deadline, discovery fails open
immediately instead of retrying early.

**This is a mitigation, not a fix, and it is bounded by construction.** When
*every* address in the pool is exhausted the retry cannot help — measured at full
exhaustion, recovery was 0/10 even at 20 attempts over 10s — and a single-address
egress has nothing to re-select. Those cases still fail open with the same
warning, just after a bounded retry. Quota resets on a **fixed hourly window**,
not a sliding one (measured: a fully drained pool stayed at 0% for 6 minutes and
recovered only at the window boundary), so the durable escape hatches remain
`EnableAutoUpdate: false` or updating manually.

## Trust boundary

Update discovery uses the **public GitHub Releases REST API anonymously** — no
`gh` executable, no GitHub token, no Copilot token. The CLI reads each asset's
`browser_download_url`, `size`, and GitHub-computed `sha256:` **digest** from
the API and records them in an immutable plan; the `copilot-updater` downloads
over HTTPS and verifies both size and digest before it touches the
installation. There is **no separately published checksum file** — a sidecar in
the same GitHub Release would share the same trust boundary and add no
independent authenticity.

This means the trust root is *GitHub Releases over HTTPS*. The digest detects
corruption or substitution between selection and installation; it is **not** an
independent code-signing system, and it does not defend against a compromised
repository release authority. Independent signing (Authenticode /
notarization / Sigstore) is a possible future change.

## The updater and the transaction

A running executable cannot replace itself on Windows, so a second small Native
AOT executable, **`copilot-updater`**, ships in every auto-update archive
alongside the bridge and `appsettings.json`. The bridge owns every decision and
hands the updater a complete, immutable plan; the updater is a mechanical
executor — it queries no releases, picks no version, prompts no one, and holds
no secret.

The install is a recoverable transaction:

1. **Prepare** (old bridge still serving): download + verify digest, extract
   into a private staging tree (rejecting traversal/symlink/duplicate entries),
   snapshot the installed config **once** into an immutable hashed byte
   snapshot, back up every managed binary, and generate the merged config.
   Any failure here leaves the old bridge untouched — it just keeps serving.
2. **Hand off**: the updater signals `Prepared`; the bridge authorizes cutover
   over a per-attempt authenticated named pipe and then exits without ever
   starting Kestrel.
3. **Cutover**: after the exact parent exits and a final drift re-check, rename
   the original `appsettings.json` to `appsettings.json.bak.<attempt-id>`,
   install the new binaries, and write the merged config.
4. **Commit on readiness**: launch the replacement and wait for an
   authenticated `Ready` signal it emits **only after it truly reaches serving
   state** (route/config validation, auth setup, all hosted services, and the
   listener are up). Process creation, a log line, or elapsed time is never
   enough. Only a valid `Ready` commits.
5. **Rollback** on any post-cutover failure: restore the old binaries and the
   **exact original config** (byte-for-byte, including keys a successful
   migration would have dropped), relaunch the old bridge, and require *its*
   `Ready` before declaring service recovered.

The replacement is launched with the **original argument vector and working
directory** (no shell), so `copilot-bridge` stays `copilot-bridge` and
`serve --port 18765` stays `serve --port 18765`. The recovery launch suppresses
the update check for that one launch, so a failed release can never loop.

Only the managed release files are ever touched. `github_credentials.dat`, legacy
`github_credentials.v2.dat` / `github_token.dat`, `log/`,
request traces, unrelated `.bak` files, and any unknown user file in the
install directory are preserved through both success and rollback.

## Configuration migration

The new release's `appsettings.json` is the **template**. The updater walks the
template and overlays the old file's values only for keys that still exist in
it:

| Case | Result |
| --- | --- |
| key in both | old value wins |
| object vs object | merge recursively |
| array vs array | **whole old array** replaces the new one (atomic — no element merge/append/sort/dedup) |
| type mismatch | whole old value wins, atomically (nested old-only content kept) |
| old value is `null` | `null` is a value — it wins |
| key only in new | new default |
| key only in old (at an object/object frontier) | **dropped** |

Key matching is case-insensitive (.NET config semantics); the output uses the
template's spelling. Case-insensitively duplicate keys fail the migration
*before* the old bridge is stopped. The merged file is generated fresh in
staging — the installed original is never edited in place, and its exact bytes
survive in the transaction `.bak` and a verified private copy for rollback.

Because a successful migration keeps only keys the new template defines, **every
new configuration key must have a working code default** — the bridge must not
depend on the stock `appsettings.json` to obtain a critical setting, since an
upgraded installation overlays the old file and would not carry a key the old
file lacked.

## Client-persisted state: keep the discriminator stable

Self-update makes DOWNGRADE a normal event — a user can roll back, or pin a version,
and a transcript written by a newer bridge is then replayed into an older one.
Anything the bridge folds into client-visible state is therefore a wire protocol
between bridge versions, and the older peer cannot be changed after the fact.

The rule that falls out of that: **evolve the PAYLOAD, never the discriminator.**
An older reader recognises its own discriminator and fails closed on a payload it
cannot handle — a bounded 400. Change the discriminator and the same reader sees
"not mine", falls through, and forwards the value upstream as if it were provider
data: a 200 with the wrong bytes and no error anywhere.

Measured against the real v0.4.29-beta reasoning-carrier decoder
(`Pipeline/Adapters/ClaudeCode/ClaudeReasoningEnvelope.cs`), by compiling that exact
shipped source and running both cases:

| carrier replayed into v0.4.29-beta | verdict | outcome |
| --- | --- | --- |
| same prefix, unknown payload version or unknown field | `Invalid` | 400, safe |
| new prefix | `Absent` | **forwarded upstream** |

So a staged reader-then-writer rollout is NOT needed for payload changes, and a
discriminator change is not made safe by staging it — it is simply the thing to
avoid. If one ever becomes unavoidable, ship read support first and let it reach
users before anything emits it.
## When it can't update

- **Install directory not writable** (e.g. a macOS `.pkg` install under
  `/usr/local`): the updater does **not** elevate. It warns and the current
  version keeps serving; update manually.
- **macOS**: auto-update always uses the `.tar.gz` asset, never the `.pkg`.
- **Non-interactive** stdin: the release notes are printed but nothing is
  installed.
- **Rollback also fails**: the updater keeps every backup and prints the exact
  install/backup/original-config paths plus manual recovery steps to stderr and
  its per-attempt transaction log.
