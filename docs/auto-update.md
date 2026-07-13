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

These **code defaults** (enabled, stable-only) are authoritative. An
installation upgraded from a build that predates this section still
auto-checks, because config migration overlays the *old* `appsettings.json`
onto the new template and would not reintroduce a section the old file lacked.

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

Only the managed release files are ever touched. `github_token.dat`, `log/`,
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
