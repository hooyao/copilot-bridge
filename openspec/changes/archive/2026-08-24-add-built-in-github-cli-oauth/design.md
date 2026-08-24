## Context

Current binaries prefer an encrypted JSON record in `github_credentials.v2.dat` and
maintain an encrypted raw-token compatibility mirror in `github_token.dat`. Fresh work
also added explicit provider metadata and a per-user fallback path. The final design
instead requires one exe-local authority whose internal version completely determines
the credential semantics.

Two credential semantics must coexist across upgrade:

- **Version 1 — legacy Copilot Plugin.** Preserves access token, optional access
  deadline, rotating refresh token/deadline, token type, scope, credential identity,
  and generation. It continues using `/copilot_internal/v2/token` and the returned
  endpoint until genuinely rejected.
- **Version 2 — GitHub CLI OAuth direct.** Contains the `gho_` access token and OAuth
  metadata. It is used directly as the bearer at `https://api.githubcopilot.com` and
  never enters `/copilot_internal/v2/token`.

GitHub CLI source revision `a255baf71d13fe5947a4eb7ad521ffd412d64cee`
provides public client ID `178c6fc778ccc68e1d6a` and scopes
`repo read:org gist`. `github.com/cli/oauth` v1.2.2 confirms form-encoded RFC 8628
Device Flow without a client secret. Live `/models` and `/responses` requests with the
resulting `gho_` returned 200, and a real Codex tool chain completed through direct CAPI.

## Goals / Non-Goals

**Goals:**

- One encrypted exe-local `github_credentials.dat` authority.
- Version-driven parsing and behavior with no filename/token-prefix dispatch.
- Preserve a still-working legacy credential and all refresh state during upgrade.
- Delete both old files only after an atomic new-file commit is re-opened and validated.
- Require login only after actual terminal credential rejection.
- Encapsulate every credential detail in `CredentialService`.
- Retain built-in GitHub CLI OAuth without invoking `gh.exe`.

**Non-Goals:**

- Sharing credentials across executable directories, OS users, or machines.
- Reading GitHub CLI keyrings/configuration.
- Auto-authorizing a replacement credential before the legacy credential fails.
- Guessing unknown future versions or inferring provider from token prefixes.

## Decisions

### One encrypted, versioned authority

`github_credentials.dat` contains one source-generated JSON envelope protected by the
existing OS-specific `ITokenProtector`. A required integer `version` is the first
dispatch field. Version 1 carries the full legacy fields; version 2 carries direct
OAuth fields. Readers reject unknown versions without deleting or rewriting the file.
Writers always emit every pinned field relevant to their version and atomically replace
the same path under one stable lock.

### Transactional migration consumes both legacy files

When the new file is absent, `CredentialService` acquires the new-file lock and then
the historical v2 lock in that fixed order, so an in-flight old-binary refresh cannot
recreate v2 after deletion. It checks again, then tries the old files in this order:

1. decrypt and parse `github_credentials.v2.dat`; if valid, map all fields to version 1;
2. otherwise decrypt `github_token.dat` and create a version-1 raw legacy record.

The service protects and atomically commits the new envelope, reads it back through
the normal new-format path, and compares the complete credential identity/state. Only
after that verification succeeds does it delete both exact old paths. Failure before
verification leaves both old files untouched. Empty historical lock files may remain;
they contain no credential material and preserve cross-process inode safety.

### Old credentials remain authoritative until terminal rejection

Migration is format-only, not reauthentication. Version 1 retains proactive refresh
when expiry/refresh metadata exists and one reactive refresh after GitHub 401. A
non-refreshable version 1 remains usable indefinitely until GitHub rejects it. Transient
transport, rate-limit, and 5xx failures never trigger replacement login. Only a typed
terminal rejection marks the current credential generation unusable and produces the
actionable `auth login` requirement.

### The next login replaces version 1 with version 2

Interactive login is never automatic while version 1 remains usable. Once no usable
credential exists, the service performs the GitHub CLI OAuth App Device Flow, commits a
version-2 envelope to the same exe-local file, and clears terminal state. Version 2
publishes a direct CAPI lease with unknown expiry and no exchange timer.

### CredentialService is the sole credential owner

`CredentialService` owns the protector/store, new and legacy paths, migration,
cross-process locks, OAuth Device Flow, refresh, rejection generation, status metadata,
and deletion. It returns an immutable internal credential lease containing only the
token, credential version/identity, deadlines, and generation needed by `AuthService`.
`AuthService`, startup, debug commands, and endpoint clients never read credential
files or choose formats/providers.

### Secret-free observability

Logs and status report file path, format version, direct/exchanged mode, refreshability,
known deadlines, generation, migration outcome, and API host. They never emit tokens,
refresh tokens, Authorization values, prefixes, hashes, or decrypted JSON.

## Risks / Trade-offs

- **Migration deletes rollback inputs** → delete only after verified commit; explicit
  user requirement favors one file over old-binary rollback.
- **Version 1 can be revoked remotely at any time** → preserve it until actual terminal
  rejection, then require explicit login rather than guessing expiry.
- **Unknown future version** → fail closed and preserve bytes for a newer binary.
- **GitHub CLI OAuth scopes are broader** → request only reviewed minimum scopes and
  retain encryption/redaction.
- **Concurrent processes migrate together** → one new-file lock, re-check after lock,
  and atomic replacement ensure one winner; followers read the committed file.

## Migration Plan

1. Ship the new reader/writer and migration tests before changing runtime ownership.
2. First current-binary load migrates the richer v2 record or raw mirror, verifies it,
   then deletes both old files.
3. Continue version-1 authentication without user interaction.
4. On eventual terminal rejection, instruct `auth login`.
5. Login atomically overwrites the same file with version 2 and direct CAPI begins.
