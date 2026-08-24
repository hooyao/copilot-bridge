# Token storage security model

The bridge persists GitHub OAuth state in one encrypted, versioned file beside the
executable: `github_credentials.dat`. Fresh login implements the GitHub CLI OAuth App
Device Flow inside the bridge and requires no `gh` executable. Every secret-bearing
file is encrypted on disk—never
plaintext—but the encryption scheme is chosen per platform at runtime, because
the strong OS-native facility used on Windows (DPAPI) has no portable equivalent
we were willing to depend on.

This document describes both schemes, the on-disk format of the non-Windows one,
and — importantly — the **threat model and its limits**, so you can decide
whether the bridge's at-rest protection is sufficient for your host.

> Implementation: `src/CopilotBridge.Cli/Auth/`. `CredentialService` exclusively
> owns paths, encryption, migration, login, refresh, rejection, status, and logout;
> `AuthService` consumes only an immutable credential lease. Only the
> `ITokenProtector` differs by platform.

## The two schemes

| Platform | Scheme | Key custody |
| --- | --- | --- |
| **Windows** (x64 + arm64) | DPAPI (`ProtectedData`, CurrentUser scope) | The OS owns the key, bound to the Windows user account. We never see it. |
| **Linux / macOS** | AES-256-CBC + HMAC-SHA256, key derived from machine + user identity | Derived on the fly from machine id + username; nothing key-like is stored. |

CPU architecture is irrelevant to the choice — DPAPI is an OS service, so
`win-arm64` uses exactly the same path as `win-x64`.

## Credential file, versions, and migration

`<exe-dir>/github_credentials.dat` is the only runtime authority. Its decrypted
source-generated JSON begins with a required semantic version:

| Version | Meaning | CAPI authentication |
| --- | --- | --- |
| `1` | Migrated Copilot Plugin credential, including complete access/refresh/deadline/identity/generation state when available | Exchange at `/copilot_internal/v2/token`; use returned bearer + endpoint |
| `2` | GitHub CLI OAuth `gho_` credential produced by built-in Device Flow | Use directly as bearer at `https://api.githubcopilot.com` |

The runtime never infers credential semantics from a filename or token prefix. An
unknown version fails closed and leaves the file untouched.

### One-time migration

If the new file is absent, the service holds `github_credentials.dat.lock`, rechecks,
then migrates in this order:

1. decrypt/parse `github_credentials.v2.dat` and preserve every field;
2. otherwise decrypt `github_token.dat` and create a non-refreshable version-1 record
   with unknown expiry.

It writes encrypted bytes to a restrictive same-directory temporary file, flushes,
atomically replaces the new path, re-opens it through the normal reader, and compares
the complete record. Only after verified readback does it delete both old files. Any
failure before verification removes the unverified new file and leaves both old files
untouched. If cleanup is interrupted after verification, the new file remains
authoritative and a later load retries deletion of every residual old credential under
the same ordered locks. Empty lock files intentionally remain at stable identities and
contain no credential material. On Unix credential files are forced to `0600`.

### Lifecycle

A migrated version 1 is not obsolete merely because version 2 exists. It remains in
use and, when refresh metadata is present, rotates five minutes before expiry under
the same cross-process lock. It is replaced only after GitHub terminally rejects it
and the operator runs `auth login`. Transient rate limits, server failures, and
transport errors preserve the current record.

Explicit login uses GitHub CLI's public OAuth client ID and exact minimum scopes
(`repo read:org gist`) with form-encoded RFC 8628 requests, no client secret, and no
`gh` process or keyring. Success atomically overwrites the same file with version 2.
`auth logout` removes the new file plus any exact pre-migration files that survived a
failed migration, then clears in-memory Copilot leases.

Copilot authentication has two compatible lease forms. A version-2 credential is
used directly as the bearer paired with
`https://api.githubcopilot.com`; its expiry is unknown and the bridge neither
calls `/copilot_internal/v2/token` nor arms a short-lived bearer timer. Version-1
credentials continue producing the former short-lived,
memory-only exchanged lease using the endpoint returned by GitHub.

For an exchanged version-1 lease, a first CAPI 401 or 403 rejects the exact
bearer/endpoint generation, obtains its replacement, and replays the exact request once.
A direct version-2 403 is likewise ambiguous and gets one bounded replay. A direct 401,
however, rejects the persisted credential identity and requires `auth login`; replaying
the same `gho_` would be meaningless. Credential identity, not only lease generation,
prevents a late 401 from reusing the same bearer after a concurrent 403 republished it.
A replayed 401 is terminal authentication failure and a replayed 403 is terminal
policy/entitlement. One shared replay bound covers mixed status sequences. This explains
why restart could previously appear to repair a stale exchanged-bearer 403: it discarded
the rejected process-local lease and forced a new exchange. Restart cannot bypass a
genuine policy refusal, which remains 403 after the bounded replay.

### Windows — DPAPI

`WindowsDpapiTokenProtector` calls `ProtectedData.Protect/Unprotect` with
`DataProtectionScope.CurrentUser` and a fixed app entropy value. The encrypted
blob is bound by Windows to the current user account; another user, another
machine, or a stolen copy of the file cannot decrypt it. This is the strongest
option and is unchanged from the original Windows-only design.

### Linux / macOS — machine-derived key (Encrypt-then-MAC)

DPAPI is Windows-only. The macOS Keychain and Linux Secret Service
(libsecret/D-Bus) are the native equivalents, but both were **deliberately not
used**:

- They require P/Invoke into native libraries, which is fragile under Native AOT
  and impossible to unit-test on the Windows dev machine.
- The Linux Secret Service depends on a D-Bus session + a running keyring
  daemon, which **headless servers and containers usually don't have** — exactly
  where this bridge most often runs. (GitHub's own Copilot CLI hit this and fell
  back to *plaintext* on headless Linux; we explicitly refuse plaintext.)

Instead, `DerivedKeyTokenProtector` derives its keys from a stable machine- and
user-bound identity and encrypts with widely-available primitives.

**Primitives (all FIPS-approved):**

- **HKDF-SHA256** (SP 800-56C) for key derivation
- **AES-256-CBC** (FIPS-197 / SP 800-38A) for confidentiality
- **HMAC-SHA256** (FIPS-198-1 / FIPS-180-4) for integrity/authenticity

AES-GCM is intentionally avoided: on macOS it has historically required OpenSSL
(not guaranteed present), whereas AES-CBC + HMAC route through the platform's
native crypto with no optional dependency, and reproduce byte-for-byte on
Windows for unit testing.

**Key derivation.** Input keying material is

```
ikm = machineId || 0x1F || userName || 0x1F || appSalt
```

run through HKDF-SHA256 twice (distinct `info` strings) to produce two
independent 32-byte keys — `Kenc` for AES and `Kmac` for HMAC. The HKDF salt and
`appSalt` are fixed, non-secret constants compiled into the binary (they provide
domain separation, not secrecy).

- **`machineId`**:
  - Linux — `/etc/machine-id` (fallback `/var/lib/dbus/machine-id`)
  - macOS — `IOPlatformUUID`, parsed from `ioreg -rd1 -c IOPlatformExpertDevice`
    (no P/Invoke; the parser is unit-tested from a captured sample)
- **`userName`**: `Environment.UserName` (mirrors DPAPI's per-user binding)

**On-disk blob layout** (Encrypt-then-MAC):

```
| 0       | 1 .. 16 | 17 .. (N-33) | (N-32) .. (N-1) |
| version | IV (16) | ciphertext   | HMAC-SHA256(32) |
```

- `version = 0x01`.
- The HMAC covers `version || IV || ciphertext` (the whole header), so version
  downgrade and IV tampering are both detected.
- On read, the MAC is verified **before** decryption, in constant time
  (`CryptographicOperations.FixedTimeEquals`). Any mismatch — wrong machine,
  wrong user, truncation, tampering, unknown version — throws
  `CryptographicException`, which the caller treats as "not logged in" and the
  user simply re-runs `auth login`. This mirrors DPAPI's "copied from another
  machine → re-login" UX exactly.

**File permissions.** On Unix each credential file is created `0600` (owner read/write
only) atomically via `FileStreamOptions.UnixCreateMode`, so there's no
brief window at default umask.

## Threat model

**What it protects against (all platforms):**

- A credential file being copied to another machine — the derived key won't match
  (different `machineId`), DPAPI won't decrypt → useless ciphertext.
- Casual disclosure: neither file is plaintext; `cat`-ing either yields ciphertext.

The GitHub CLI OAuth-compatible token carries broader GitHub scopes than the old
Copilot Plugin credential (`repo`, `read:org`, and `gist` rather than `read:user`).
Encryption and redaction therefore remain mandatory; the bridge never writes the
token to client configuration or command output.

**Windows (DPAPI):** additionally, another user on the same machine cannot
decrypt it (OS-enforced, key bound to the Windows account).

**Linux / macOS — explicit weakness.** The key is derived from the machine id
(which is **world-readable**, e.g. `/etc/machine-id`) plus the username (public).
There is no hardware-backed secret and no OS keystore. Therefore:

> A local attacker who can **run code as the same user on the same host** can
> re-derive the key (they can read the same machine id, and they already are the
> user) and decrypt the token. The host itself is the trust boundary.

This is **weaker than DPAPI/Keychain**, but **strictly better than plaintext**:
it defends the realistic "file got copied/leaked off-box" case while never
storing a recoverable secret. If your host's threat model requires protection
against same-user local attackers, run the bridge on Windows (DPAPI) or store
the token on an OS keystore via future work.

**Future work.** Optional native backends (macOS Keychain, Linux Secret Service
where a session exists, `pass`/GPG for headless) could be added behind the same
`ITokenProtector` abstraction, falling back to the derived-key scheme when no
keystore is available. Not implemented in M2 (AOT/P-Invoke cost + headless
ubiquity of the fallback path).

## Verification

- **Windows / unit tests:** `DerivedKeyTokenProtectorTests` exercises the full
  scheme with an injected fixed key provider (round-trip, IV freshness,
  wrong-machine/user, every tamper position, truncation, unknown version, blob
  layout). `MachineKeyProviderParseTests` covers `ParseIOPlatformUUID`.
- **Credential lifecycle unit contracts:** `CredentialServiceMigrationTests`,
  `GitHubCliOAuthContractTests`, `AuthServiceGitHubRecoveryTests`, and
  `AuthenticationSecretRedactionTests` cover legacy migration, the encrypted
  versioned authority, atomic/locked rotation, expiry/401 refresh, bounded failure, and
  no-secret diagnostics.
- **Real Linux/macOS (CI only):** `copilot-bridge debug selftest-tokenstore`
  (hidden command) runs the **real** machine-id probing + encrypt/decrypt
  round-trip + `0600` check against a temp file — non-destructive, no login
  required. CI runs it on `ubuntu-latest` and `macos-14`.
