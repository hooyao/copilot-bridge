using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Windows-only token protector backed by DPAPI (<see cref="ProtectedData"/>, CurrentUser scope).
/// The blob is bound to the current Windows user — another user, another machine, or a stolen copy
/// cannot decrypt it. Windows owns the key; we never touch it.
/// <para>
/// This is the single Windows-attributed type in the assembly. <see cref="TokenStore"/> only ever
/// constructs it under an <see cref="OperatingSystem.IsWindows"/> guard, which is exactly the shape
/// the CA1416 platform-compatibility analyzer understands — so no assembly-wide
/// <c>[SupportedOSPlatform("windows")]</c> is needed (or wanted: that would re-break the non-Windows build).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiTokenProtector : ITokenProtector
{
    private readonly byte[] _entropy;

    /// <param name="entropy">
    /// App-specific entropy mixed into the DPAPI envelope. Acts as a salt so another app running as
    /// the same user cannot decrypt our token even if it stole the file.
    /// </param>
    public WindowsDpapiTokenProtector(byte[] entropy) => _entropy = entropy;

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);

    // Throws CryptographicException on wrong user / corrupt file / copied from another machine —
    // which is exactly what TokenStore's catch turns into "not logged in".
    public byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, _entropy, DataProtectionScope.CurrentUser);
}
