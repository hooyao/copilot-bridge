# Token storage security model

The bridge persists GitHub OAuth state in one encrypted, versioned file beside the
executable: `github_credentials.dat`. Fresh login implements the appsettings-selected
GitHub OAuth Device Flow inside the bridge and requires no `gh` executable. The stock
configuration selects the official Copilot Plugin App; an explicit custom-App opt-in
uses the configured public client ID. Every secret-bearing
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
| `2` | Compatible GitHub CLI OAuth `gho_` direct credential | Use directly as bearer at `https://api.githubcopilot.com` |
| `3` | Newly issued Copilot Plugin credential with explicit `oauth_client_id` | Exchange at `/copilot_internal/v2/token`; use returned bearer + endpoint |
| `4` | Explicit custom OAuth App credential with recorded `oauth_client_id` | Use the GitHub OAuth access token directly at `https://api.githubcopilot.com`; refresh it from its recorded issuer |

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
the same ordered locks. The authoritative lock remains at a stable identity. The
historical `github_credentials.v2.dat.lock` is created only when a legacy credential or
that lock already exists; once present it likewise remains stable, but a fresh lookup,
login, or logout does not introduce it. Lock files contain no credential material. On
Unix credential files are forced to `0600`.

An updater-managed target or rollback activation defers this entire credential load,
migration, and network-authentication path until after it sends `Ready`. It then resumes
the ordinary authentication bootstrap in the background, including the first-run device
flow when no credential exists. This lets the candidate report `Ready` based on local
serving health without a token outage forcing rollback, while preserving the normal
login UX and legacy credential inputs throughout the readiness window. Post-readiness
auth failures are actionable but non-fatal. Ordinary launches retain the synchronous
startup authentication/device-flow behavior.

### Lifecycle

A migrated version 1 is not obsolete merely because newer versions exist. It remains in
use and, when refresh metadata is present, rotates five minutes before expiry under
the same cross-process lock. It is replaced only after GitHub terminally rejects it
and the operator runs `auth login`. Transient rate limits, server failures, and
transport errors preserve the current record.

The top-level `Authentication` configuration controls only the next explicit login.
`UseCustomAppId=false` (stock default) uses the official Copilot Plugin public OAuth
client ID and writes version 3. `UseCustomAppId=true` uses `CustomAppId` (stock value
`Ov23liSD97ZYGfIEHAZE`) and writes version 4. Both use `read:user`, form-encoded RFC
8628 requests, no client secret, and no `gh` process or keyring. Success atomically
overwrites the same file and records the issuing client ID in `oauth_client_id`.
Custom mode rejects a blank ID and the official Copilot Plugin ID (which must retain
version-3 exchange semantics). If the custom App has Device Flow disabled, the login
error preserves GitHub's bounded `device_flow_disabled` code and HTTP status without
echoing the response body.
Changing appsettings never reinterprets or rewrites an existing credential; run
`auth login` after restart to replace it deliberately.
If rejection recovery races a login from another process, the newer record remains
authoritative and authentication is rebuilt from its own version, including a direct
↔ exchanged provider change, rather than inheriting the rejected lease's mode.
`auth logout` removes the new file plus any exact pre-migration files that survived a
failed migration, then clears in-memory Copilot leases.

Copilot authentication has two compatible lease forms. Version-2 and version-4
credentials are direct bearers paired with `https://api.githubcopilot.com`; neither
calls `/copilot_internal/v2/token`. Version 2 retains its unknown-expiry compatibility
behavior. A refreshable version 4 schedules rotation five minutes before its known
GitHub access-token deadline and atomically preserves GitHub's rotating access/refresh
pair. Its Copilot lease uses the official SDK integration identity
`copilot-developer-cli`; versions 1–3 and compatible v2 retain `vscode-chat`.
Version-1 credentials continue producing the former short-lived,
memory-only exchanged lease using the endpoint returned by GitHub. Version 3 uses
the same exchange shape and reads its refresh client ID from the credential record.

For an exchanged version-1 or version-3 lease, a first CAPI 401 or 403 rejects the exact
bearer/endpoint generation, obtains its replacement, and replays the exact request once.
A direct version-2 403 is likewise ambiguous and gets one bounded replay. A direct 401,
however, rejects the persisted credential identity and requires `auth login`; replaying
the same non-refreshable `gho_` would be meaningless. A refreshable version-4 direct
lease instead rotates its GitHub credential once on the first 401 or 403 and uses the
new direct bearer for the same bounded replay; a non-refreshable v4 follows the terminal
401 rule. The terminal transition removes every cached lease
generation carrying that identity, and lock-free cache reuse checks terminal state before
returning a direct bearer. Credential identity, not only lease generation, therefore
prevents ordinary concurrent callers or a late 401 from reusing a terminal bearer.
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

Compatible version-2 GitHub CLI OAuth tokens carry broader GitHub scopes than the
Copilot Plugin credential. Custom version-4 tokens carry the configured App's grant
and a rotating refresh credential. Encryption and redaction therefore remain mandatory; the
bridge never writes any credential to client configuration or command output.

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
  `GitHubOAuthContractTests`, `AuthServiceGitHubRecoveryTests`, and
  `AuthenticationSecretRedactionTests` cover legacy migration, the encrypted
  versioned authority, atomic/locked rotation, expiry/401 refresh, bounded failure, and
  no-secret diagnostics.
- **Real Linux/macOS (CI only):** `copilot-bridge debug selftest-tokenstore`
  (hidden command) runs the **real** machine-id probing + encrypt/decrypt
  round-trip + `0600` check against a temp file — non-destructive, no login
  required. CI runs it on `ubuntu-latest` and `macos-14`.
