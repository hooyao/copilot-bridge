using System.Diagnostics;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;

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
        using var auth = new AuthService(
            new SingleClientHttpClientFactory(http),
            OnDeviceCodeIssued,
            LoadLoginProvider());

        try
        {
            await auth.LoginAsync();
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
        using var http = CreateHttpClient();
        using var auth = new AuthService(new SingleClientHttpClientFactory(http));
        return Logout(auth, Console.Out);
    }

    internal static int Logout(AuthService auth, TextWriter output)
    {
        var location = auth.TokenLocation;
        auth.SignOut();
        output.WriteLine($"Deleted stored credentials from: {location}");
        return 0;
    }

    public static int Status()
    {
        using var http = CreateHttpClient();
        using var auth = new AuthService(new SingleClientHttpClientFactory(http));
        var status = auth.GetCredentialStatus();
        if (status is null)
        {
            Console.WriteLine("Not logged in.");
            Console.WriteLine($"  credential: {auth.TokenLocation}  (exists: False)");
            return 0;
        }
        Console.WriteLine("Logged in.");
        Console.WriteLine($"  loaded from: {status.Path}");
        Console.WriteLine($"  version:     {status.Version}");
        Console.WriteLine($"  OAuth app:   {status.OAuthClientId ?? "(implicit legacy provider)"}");
        Console.WriteLine($"  mode:        {(status.IsDirect ? "direct" : "exchanged")}");
        Console.WriteLine($"  refreshable: {status.IsRefreshable}");
        Console.WriteLine($"  generation:  {status.Generation}");
        Console.WriteLine($"  access expires:  {status.AccessTokenExpiresAt?.ToString("O") ?? "(unknown)"}");
        Console.WriteLine($"  refresh expires: {status.RefreshTokenExpiresAt?.ToString("O") ?? "(none)"}");
        return 0;
    }

    public static async Task<int> CopilotStatusAsync()
    {
        using var http = CreateHttpClient();
        using var auth = new AuthService(new SingleClientHttpClientFactory(http));
        if (!auth.IsAuthenticated)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login` first.");
            return 1;
        }

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
        output.WriteLine($"Authentication mode:   {lease.Kind.ToString().ToLowerInvariant()}");
        output.WriteLine($"Token expires at:      {FormatDeadline(lease.ServerExpiresAt)}");
        output.WriteLine($"Token refresh at:      {FormatDeadline(lease.RefreshAt)}");
        output.WriteLine($"Copilot API base URL:  {lease.ApiBaseUrl}");
        output.WriteLine($"CAPI integration ID:   {lease.IntegrationId}");
    }

    private static string FormatDeadline(DateTimeOffset value) =>
        value == DateTimeOffset.MaxValue ? "(unknown)" : value.ToString("O");

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
        return http;
    }

    internal static Models.GitHub.GitHubOAuthLoginProvider LoadLoginProvider(
        IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder()
            .AddBridgeAppSettings()
            .Build();
        return AuthenticationOptions.FromConfiguration(configuration)
            .ResolveLoginProvider();
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
