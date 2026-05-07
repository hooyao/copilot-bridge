using System.Security.Cryptography;
using System.Text;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Persists the GitHub OAuth token, encrypted with Windows DPAPI (CurrentUser scope).
/// The blob is bound to the current Windows user — another user, another machine, or a
/// stolen copy cannot decrypt it. Windows manages the key.
/// <para>
/// Lookup order on read: <see cref="FilePath"/> first (next to the .exe), then
/// <see cref="FallbackPath"/> (<c>~/github_token.dat</c>). Saves always go to
/// <see cref="FilePath"/>; deletes clear both so logout is total.
/// The fallback lets one login serve multiple binaries — production .exe in
/// <c>publish/</c> and dev runs from <c>bin/Debug/...</c> share the home-dir copy.
/// </para>
/// </summary>
internal static class TokenStore
{
    private const string FileName = "github_token.dat";

    // App-specific entropy mixed into the DPAPI envelope. Acts as a salt so another
    // app running as the same user cannot decrypt our token even if it stole the file.
    private static readonly byte[] s_entropy = "copilot-bridge.github_token.v1"u8.ToArray();

    /// <summary>Primary location: same directory as the executable. Saves go here.</summary>
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Read-only fallback: <c>~/github_token.dat</c>. Lets dev binaries find a single shared token.</summary>
    public static string FallbackPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        FileName);

    public static string? TryLoad() => TryLoadFrom(FilePath) ?? TryLoadFrom(FallbackPath);

    private static string? TryLoadFrom(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, s_entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Wrong user, corrupt file, or copied from another machine.
            return null;
        }
    }

    public static void Save(string token)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        var encrypted = ProtectedData.Protect(plain, s_entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static void Delete()
    {
        // Clear both locations so logout actually means logged out.
        if (File.Exists(FilePath)) File.Delete(FilePath);
        if (File.Exists(FallbackPath)) File.Delete(FallbackPath);
    }
}
