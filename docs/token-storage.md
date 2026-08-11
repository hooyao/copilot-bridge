# Token storage security model

The bridge persists GitHub OAuth credential state obtained through device flow:
an access token plus any access expiry, rotating refresh token, and refresh-token
expiry returned by GitHub. Every secret-bearing file is encrypted on disk—never
plaintext—but the encryption scheme is chosen per platform at runtime, because
the strong OS-native facility used on Windows (DPAPI) has no portable equivalent
we were willing to depend on.

This document describes both schemes, the on-disk format of the non-Windows one,
and — importantly — the **threat model and its limits**, so you can decide
whether the bridge's at-rest protection is sufficient for your host.

> Implementation: `src/CopilotBridge.Cli/Auth/`. `AuthService` is the facade,
> `GitHubCredentialManager` owns refresh, and `GitHubCredentialStore` owns the
> encrypted files. Only the `ITokenProtector` differs by platform.

## The two schemes

| Platform | Scheme | Key custody |
| --- | --- | --- |
| **Windows** (x64 + arm64) | DPAPI (`ProtectedData`, CurrentUser scope) | The OS owns the key, bound to the Windows user account. We never see it. |
| **Linux / macOS** | AES-256-CBC + HMAC-SHA256, key derived from machine + user identity | Derived on the fly from machine id + username; nothing key-like is stored. |

CPU architecture is irrelevant to the choice — DPAPI is an OS service, so
`win-arm64` uses exactly the same path as `win-x64`.

## Credential files and rotation

The current format uses two encrypted files in the authoritative credential
directory:

| File | Encrypted plaintext payload | Purpose |
| --- | --- | --- |
| `github_credentials.v2.dat` | Source-generated JSON: pinned format version, access token/deadline, optional refresh token/deadline, token type, scope, opaque credential id, generation | Authoritative credential record used by current binaries |
| `github_token.dat` | Raw access-token string | Compatibility mirror readable by older bridge binaries |

The v2 record is always authoritative. Lookup order is v2 primary (next to the
executable), v2 home fallback, legacy primary, then legacy home fallback. A valid
v2 fallback therefore beats a stale raw primary mirror. Existing installations
with only `github_token.dat` continue without a rewrite; such a record has unknown
expiry and no refresh capability unless a later device login actually returns
refresh metadata.

Device login preserves `expires_in`, `refresh_token`, and
`refresh_token_expires_in` when GitHub supplies them. A known access-token expiry
is refreshed five minutes early. Each refresh-token grant rotates and atomically
commits the complete v2 generation before the new credential is exposed. The
compatibility mirror is updated second; failure there limits downgrade behavior
but cannot corrupt the current runtime's authoritative state.

Each fresh device login mints an opaque credential id and starts its generation
counter at one; refresh rotation preserves that id while incrementing the
generation. Rejection and stale-refresh checks use the pair, not generation alone.
That distinction lets a running bridge accept a fresh login written by another
process even though both old and new records use generation one. Pre-release v2
records without the field remain readable and receive an id on their next refresh.

The submitted refresh token is single-use. If GitHub returns a new access token
but unexpectedly omits its replacement refresh token, the bridge does not retain
the spent prior token: it commits the new generation as non-refreshable and logs
that boolean outcome without credential material.

The absence of those optional fields is a valid OAuth result. In that case
`auth status` reports `refreshable: False` and unknown expiry; the access token is
still usable, but a later server-side revocation requires interactive login. It
does not mean the device-token exchange failed, and the bridge must not synthesize
a deadline or refresh token.

Rotating refresh tokens are single-use state, so process-local locking is not
enough. Refresh acquires a lock file next to the authoritative v2 path, reloads
the credential-id/generation pair after obtaining it, and skips the network call
if another process already committed a refresh or fresh login. The empty lock
file is intentionally retained after release and logout so every Unix process
continues to lock the same inode; it contains no credential material. A commit
writes already-encrypted bytes to a restrictive same-directory temporary file,
flushes it, and atomically replaces v2. A pre-commit crash leaves the prior
complete record readable. On Unix both v2 and the raw mirror are forced to `0600`.

Every writer participates in that ordering. Fresh device login takes the primary
path lock before committing, so an older refresh either finishes first or reloads
and yields to the new identity. Logout locks both configured v2 paths in stable
order before deleting every credential representation, preventing an in-flight
refresh from recreating a token after sign-out.

A GitHub `401 Bad credentials` triggers at most one refresh-token rotation and
one replay. If the record is legacy, the refresh token is expired/rejected, or
the replay is also 401, the bridge preserves the last record and tells the
operator to run `auth logout` followed by `auth login`; it never refresh-loops.
Rate limits, server failures, timeouts, and other transient refresh failures do
not mark the credential rejected: the current record stays committed and the
bounded timer/request policy may retry later.
Logout removes both formats at primary and fallback locations plus in-memory
Copilot leases.

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
- **Credential lifecycle unit contracts:** `GitHubCredentialStoreTests`,
  `GitHubCredentialManagerTests`, `AuthServiceGitHubRecoveryTests`, and
  `AuthenticationSecretRedactionTests` cover legacy migration, encrypted v2 +
  mirror, atomic/locked rotation, expiry/401 refresh, bounded failure, and
  no-secret diagnostics.
- **Real Linux/macOS (CI only):** `copilot-bridge debug selftest-tokenstore`
  (hidden command) runs the **real** machine-id probing + encrypt/decrypt
  round-trip + `0600` check against a temp file — non-destructive, no login
  required. CI runs it on `ubuntu-latest` and `macos-14`.
