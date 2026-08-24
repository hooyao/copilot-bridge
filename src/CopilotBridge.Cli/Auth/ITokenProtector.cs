namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Reversible byte-level protection for the persisted GitHub OAuth token.
/// One implementation per platform strategy:
/// <list type="bullet">
///   <item><see cref="WindowsDpapiTokenProtector"/> — Windows DPAPI (CurrentUser).</item>
///   <item><see cref="DerivedKeyTokenProtector"/> — machine+user-derived AES-256-CBC + HMAC-SHA256
///   (Linux/macOS, where DPAPI is unavailable).</item>
/// </list>
/// <para>
/// The contract is pure: <see cref="Protect"/>/<see cref="Unprotect"/> only transform bytes.
/// File I/O, path resolution, and migration order all live in <see cref="CredentialStore"/>,
/// so the platform-specific surface stays a single byte-in/byte-out function (which is what makes
/// the non-Windows path unit-testable on Windows).
/// </para>
/// <para>
/// <see cref="Unprotect"/> MUST throw <see cref="System.Security.Cryptography.CryptographicException"/>
/// on ANY protection failure — wrong machine, wrong user, tampered blob, or truncation — so the
/// credential reader can fail closed without rewriting or deleting the unreadable authority.
/// </para>
/// </summary>
internal interface ITokenProtector
{
    /// <summary>Encrypt <paramref name="plaintext"/> into an opaque on-disk blob.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Decrypt a blob produced by <see cref="Protect"/>.
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/> on any failure.
    /// </summary>
    byte[] Unprotect(byte[] blob);
}
