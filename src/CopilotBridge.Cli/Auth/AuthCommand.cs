using System.Diagnostics;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Auth;

internal static class AuthCommand
{
    /// <summary>
    /// Public, non-secret bearer value used only to opt a custom Codex provider into
    /// remote model discovery. This command deliberately has no dependency on the
    /// token store, HTTP, or the web host.
    /// </summary>
    internal const string ProviderSentinel = "copilot-bridge-provider";

    public static int ProviderToken()
    {
        Console.WriteLine(ProviderSentinel);
        return 0;
    }

    public static async Task<int> LoginAsync()
    {
        using var http = CreateHttpClient();
        var auth = new AuthService(new SingleClientHttpClientFactory(http), OnDeviceCodeIssued);

        if (auth.IsAuthenticated)
        {
            Console.WriteLine($"Already logged in. Token: {auth.TokenLocation}");
            Console.WriteLine("Run `auth logout` to sign out, or `auth whoami` to verify.");
            return 0;
        }

        try
        {
            await auth.EnsureGitHubTokenAsync();
            Console.WriteLine();
            Console.WriteLine("Login complete. Encrypted token saved to:");
            Console.WriteLine($"  {auth.TokenLocation}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Login cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Login failed: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> WhoAmIAsync()
    {
        using var http = CreateHttpClient();
        using var auth = new AuthService(new SingleClientHttpClientFactory(http));
        if (!auth.IsAuthenticated)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login`.");
            return 1;
        }

        try
        {
            var user = await auth.GetGitHubUserAsync();
            Console.WriteLine($"Logged in as {user.Login} (id {user.Id})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    public static int Logout()
    {
        var primaryExisted = File.Exists(TokenStore.FilePath);
        var fallbackExisted = File.Exists(TokenStore.FallbackPath);
        var v2PrimaryExisted = File.Exists(TokenStore.CredentialFilePath);
        var v2FallbackExisted = File.Exists(TokenStore.CredentialFallbackPath);
        if (!primaryExisted && !fallbackExisted && !v2PrimaryExisted && !v2FallbackExisted)
        {
            Console.WriteLine("Not logged in.");
            return 0;
        }
        TokenStore.Delete();
        if (primaryExisted) Console.WriteLine($"Deleted: {TokenStore.FilePath}");
        if (fallbackExisted) Console.WriteLine($"Deleted: {TokenStore.FallbackPath}");
        if (v2PrimaryExisted) Console.WriteLine($"Deleted: {TokenStore.CredentialFilePath}");
        if (v2FallbackExisted) Console.WriteLine($"Deleted: {TokenStore.CredentialFallbackPath}");
        return 0;
    }

    public static int Status()
    {
        var loaded = TokenStore.TryLoadCredential();
        if (loaded is null)
        {
            Console.WriteLine("Not logged in.");
            Console.WriteLine($"  v2 primary:     {TokenStore.CredentialFilePath}  (exists: {File.Exists(TokenStore.CredentialFilePath)})");
            Console.WriteLine($"  v2 fallback:    {TokenStore.CredentialFallbackPath}  (exists: {File.Exists(TokenStore.CredentialFallbackPath)})");
            Console.WriteLine($"  legacy primary: {TokenStore.FilePath}  (exists: {File.Exists(TokenStore.FilePath)})");
            Console.WriteLine($"  legacy fallback:{TokenStore.FallbackPath}  (exists: {File.Exists(TokenStore.FallbackPath)})");
            return 0;
        }
        Console.WriteLine("Logged in.");
        Console.WriteLine($"  loaded from: {loaded.AuthoritativePath}");
        Console.WriteLine($"  format:      {loaded.Format.ToString().ToLowerInvariant()}");
        Console.WriteLine($"  refreshable: {loaded.Record.IsRefreshable}");
        Console.WriteLine($"  access expires:  {loaded.Record.AccessTokenExpiresAt?.ToString("O") ?? "(unknown)"}");
        Console.WriteLine($"  refresh expires: {loaded.Record.RefreshTokenExpiresAt?.ToString("O") ?? "(none)"}");
        return 0;
    }

    public static async Task<int> CopilotStatusAsync()
    {
        if (TokenStore.TryLoad() is null)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login` first.");
            return 1;
        }

        using var http = CreateHttpClient();
        using var auth = new AuthService(new SingleClientHttpClientFactory(http));

        try
        {
            var lease = await auth.GetCopilotTokenAsync();
            WriteCopilotStatus(Console.Out, lease);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to obtain Copilot token: {ex.Message}");
            return 1;
        }
    }

    internal static void WriteCopilotStatus(TextWriter output, CopilotAuthLease lease)
    {
        output.WriteLine($"Token expires at:      {lease.ServerExpiresAt:O}");
        output.WriteLine($"Token refresh at:      {lease.RefreshAt:O}");
        output.WriteLine($"Copilot API base URL:  {lease.ApiBaseUrl}");
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
        return http;
    }

    private static void OnDeviceCodeIssued(DeviceCodeChallenge challenge)
    {
        Console.WriteLine();
        Console.WriteLine($"  Open: {challenge.VerificationUri}");
        Console.WriteLine($"  Code: {challenge.UserCode}");
        Console.WriteLine($"  (expires in ~{challenge.ExpiresIn.TotalMinutes:F0} min)");
        Console.WriteLine();
        TryOpenBrowser(challenge.VerificationUri);
        Console.WriteLine("Waiting for authorization on github.com...");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Headless or no shell — the URL was printed above, that's fine.
        }
    }
}
