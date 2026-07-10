# Token storage security model

The bridge can consume a GitHub token ephemerally from
`COPILOT_BRIDGE_GITHUB_TOKEN`. When set to a non-empty value, it takes precedence
over disk and is never encrypted, copied, or persisted by the bridge. This is
the intended path for short-lived GitHub Actions credentials.

Without that environment variable, the bridge persists one secret at rest: the long-lived **GitHub OAuth token**
(obtained via the device-code flow). It is always encrypted on disk — never
plaintext — but the encryption scheme is chosen per platform at runtime, because
the strong OS-native facility we use on Windows (DPAPI) has no portable
equivalent we were willing to depend on.

This document describes both schemes, the on-disk format of the non-Windows one,
and — importantly — the **threat model and its limits**, so you can decide
whether the bridge's at-rest protection is sufficient for your host.

> Implementation: `src/CopilotBridge.Cli/Auth/`. The public surface
> (`TokenStore.TryLoad/Save/Delete`) is identical across platforms; only the
> `ITokenProtector` behind it differs.

## The two schemes

| Platform | Scheme | Key custody |
| --- | --- | --- |
| **Windows** (x64 + arm64) | DPAPI (`ProtectedData`, CurrentUser scope) | The OS owns the key, bound to the Windows user account. We never see it. |
| **Linux / macOS** | AES-256-CBC + HMAC-SHA256, key derived from machine + user identity | Derived on the fly from machine id + username; nothing key-like is stored. |

CPU architecture is irrelevant to the choice — DPAPI is an OS service, so
`win-arm64` uses exactly the same path as `win-x64`.

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

**File permissions.** On Unix the token file is created `0600` (owner read/write
only) atomically via `FileStreamOptions.UnixCreateMode`, so there's no
brief window at default umask.

## Threat model

**What it protects against (all platforms):**

- The token file being copied to another machine — the derived key won't match
  (different `machineId`), DPAPI won't decrypt → useless ciphertext.
- Casual disclosure: the file is never plaintext; `cat`-ing it yields ciphertext.

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
- **Real Linux/macOS (CI only):** `copilot-bridge debug selftest-tokenstore`
  (hidden command) runs the **real** machine-id probing + encrypt/decrypt
  round-trip + `0600` check against a temp file — non-destructive, no login
  required. CI runs it on `ubuntu-latest` and `macos-14`.
