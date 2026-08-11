using System.Security.Cryptography;
using System.Text;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Composes the process-default encrypted GitHub OAuth credential store. The
/// encryption scheme is chosen per platform
/// at runtime:
/// <list type="bullet">
///   <item><b>Windows</b> — DPAPI (<see cref="WindowsDpapiTokenProtector"/>, CurrentUser scope).
///   The blob is bound to the current Windows user; Windows owns the key.</item>
///   <item><b>Linux / macOS</b> — AES-256-CBC + HMAC-SHA256 with a key derived from machine + user
///   identity (<see cref="DerivedKeyTokenProtector"/>), since DPAPI is Windows-only. Weaker than
///   DPAPI/Keychain but never plaintext; see <c>docs/token-storage.md</c>.</item>
/// </list>
/// <para>
/// Lookup prefers the authoritative v2 primary/fallback records, then the raw
/// <see cref="FilePath"/> / <see cref="FallbackPath"/> compatibility mirrors.
/// New device logins save v2 next to the executable; refresh writes back to the
/// loaded v2 path. Deletes clear every representation so logout is total.
/// </para>
/// <para>
/// Platform dispatch lives entirely in <see cref="CreateProtector"/>: the DPAPI type is the only
/// Windows-attributed surface in the assembly, constructed only under an
/// <see cref="OperatingSystem.IsWindows"/> guard — so there is no assembly-wide
/// <c>[SupportedOSPlatform("windows")]</c> (which would re-break the non-Windows build).
/// </para>
/// </summary>
internal static class TokenStore
{
    // App-specific entropy mixed into the Windows DPAPI envelope. Acts as a salt so another app
    // running as the same user cannot decrypt our token even if it stole the file. (The non-Windows
    // protector has its own HKDF salt; this value is DPAPI-only.)
    private static readonly byte[] s_entropy = "copilot-bridge.github_token.v1"u8.ToArray();

    // Lazily-created so the OS dispatch and any machine-id probing happen once, on first use.
    private static readonly ITokenProtector s_protector = CreateProtector();

    private static readonly GitHubCredentialStore s_store = new(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        s_protector);

    /// <summary>Legacy-compatible raw access-token mirror next to the executable.</summary>
    public static string FilePath => s_store.LegacyPrimaryPath;

    /// <summary>Legacy raw access-token home fallback.</summary>
    public static string FallbackPath => s_store.LegacyFallbackPath;

    /// <summary>Authoritative v2 credential location next to the executable.</summary>
    public static string CredentialFilePath => s_store.VersionedPrimaryPath;

    /// <summary>Authoritative v2 home fallback, when one exists.</summary>
    public static string CredentialFallbackPath => s_store.VersionedFallbackPath;

    internal static GitHubCredentialStore CredentialStore => s_store;

    public static string? TryLoad() => s_store.TryLoad()?.Record.AccessToken;

    internal static StoredGitHubCredential? TryLoadCredential() => s_store.TryLoad();

    public static void Save(string token)
    {
        s_store.SaveLegacy(token);
    }

    internal static void SaveCredential(GitHubCredentialRecord record) => s_store.SaveNew(record);

    public static void Delete()
    {
        s_store.Delete();
    }

    private static ITokenProtector CreateProtector() =>
        OperatingSystem.IsWindows()
            ? new WindowsDpapiTokenProtector(s_entropy)
            : new DerivedKeyTokenProtector(new MachineKeyProvider());

    /// <summary>
    /// Write the encrypted blob to <paramref name="path"/>. On Unix, create the file with
    /// <c>0600</c> permissions atomically (owner read/write only) — defense in depth even though the
    /// contents are already ciphertext. On Windows, DPAPI already binds the blob to the user, and the
    /// <see cref="FileStreamOptions.UnixCreateMode"/> setter is Windows-unsupported, so we use a plain write.
    /// </summary>
    /// <summary>
    /// Non-destructive self-test of the active protector, for CI smoke on platforms we cannot build
    /// locally (Linux/macOS). Exercises the things only verifiable on the real OS — machine-id probing
    /// (<see cref="MachineKeyProvider"/>), the HKDF/AES/HMAC (or DPAPI) round-trip, and the Unix
    /// <c>0600</c> create-mode — all against a temp file, so the user's real token is never touched.
    /// Returns true on success; writes a human-readable line either way.
    /// </summary>
    public static bool RunSelfTest(TextWriter output)
    {
        var scheme = OperatingSystem.IsWindows() ? "DPAPI" : "AES-256-CBC+HMAC (machine-derived)";
        var tempPath = Path.Combine(Path.GetTempPath(), $"copilot-bridge.selftest.{Guid.NewGuid():N}.dat");
        try
        {
            var sample = Encoding.UTF8.GetBytes("self-test-token-中文-payload");

            // 1) Protect/Unprotect round-trip through the real protector (real machine-id on unix).
            var blob = s_protector.Protect(sample);
            var recovered = s_protector.Unprotect(blob);
            if (!recovered.AsSpan().SequenceEqual(sample))
            {
                output.WriteLine($"token-store self-test FAILED: round-trip mismatch ({scheme}).");
                return false;
            }

            // 2) Write via the real on-disk path (verifies UnixCreateMode 0600 doesn't throw).
            GitHubCredentialStore.WriteBlobAtomic(tempPath, blob);

            // 3) On unix, confirm the file really is 0600 (owner read/write only).
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(tempPath);
                var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if (mode != expected)
                {
                    output.WriteLine($"token-store self-test FAILED: expected file mode 0600, got {mode}.");
                    return false;
                }
            }

            // 4) Read the written file back through the same load path.
            var fromDisk = s_protector.Unprotect(File.ReadAllBytes(tempPath));
            if (!fromDisk.AsSpan().SequenceEqual(sample))
            {
                output.WriteLine($"token-store self-test FAILED: on-disk round-trip mismatch ({scheme}).");
                return false;
            }

            output.WriteLine($"token-store self-test PASSED: {scheme}.");
            return true;
        }
        catch (Exception ex)
        {
            output.WriteLine($"token-store self-test FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }
}
