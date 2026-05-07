using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CopilotBridge.Cli.Models;

namespace CopilotBridge.Cli.Auth;

internal static class AuthCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var sub = args.Length == 0 ? "login" : args[0];
        return sub switch
        {
            "login" => await LoginAsync().ConfigureAwait(false),
            "whoami" => await WhoAmIAsync().ConfigureAwait(false),
            "logout" => Logout(),
            "status" => Status(),
            "copilot-status" => await CopilotStatusAsync().ConfigureAwait(false),
            _ => Unknown(sub),
        };
    }

    private static async Task<int> LoginAsync()
    {
        using var http = CreateHttpClient();
        var auth = new AuthService(http, OnDeviceCodeIssued);

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

    private static async Task<int> WhoAmIAsync()
    {
        var token = TokenStore.TryLoad();
        if (token is null)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login`.");
            return 1;
        }

        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        try
        {
            using var resp = await http.GetAsync("https://api.github.com/user");
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");
                return 1;
            }
            var user = await resp.Content.ReadFromJsonAsync(JsonContext.Default.GitHubUser);
            if (user is null)
            {
                Console.Error.WriteLine("Empty response from GitHub.");
                return 1;
            }
            Console.WriteLine($"Logged in as {user.Login} (id {user.Id})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request failed: {ex.Message}");
            return 1;
        }
    }

    private static int Logout()
    {
        var primaryExisted = File.Exists(TokenStore.FilePath);
        var fallbackExisted = File.Exists(TokenStore.FallbackPath);
        if (!primaryExisted && !fallbackExisted)
        {
            Console.WriteLine("Not logged in.");
            return 0;
        }
        TokenStore.Delete();
        if (primaryExisted) Console.WriteLine($"Deleted: {TokenStore.FilePath}");
        if (fallbackExisted) Console.WriteLine($"Deleted: {TokenStore.FallbackPath}");
        return 0;
    }

    private static int Status()
    {
        var primaryExists = File.Exists(TokenStore.FilePath);
        var fallbackExists = File.Exists(TokenStore.FallbackPath);
        if (TokenStore.TryLoad() is null)
        {
            Console.WriteLine("Not logged in.");
            Console.WriteLine($"  primary:  {TokenStore.FilePath}  (exists: {primaryExists})");
            Console.WriteLine($"  fallback: {TokenStore.FallbackPath}  (exists: {fallbackExists})");
            return 0;
        }
        var loadedFrom = primaryExists ? TokenStore.FilePath : TokenStore.FallbackPath;
        Console.WriteLine("Logged in.");
        Console.WriteLine($"  loaded from: {loadedFrom}");
        if (primaryExists && fallbackExists)
            Console.WriteLine("  note: both primary and fallback files exist; primary is used");
        return 0;
    }

    private static async Task<int> CopilotStatusAsync()
    {
        if (TokenStore.TryLoad() is null)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login` first.");
            return 1;
        }

        using var http = CreateHttpClient();
        using var auth = new AuthService(http);

        try
        {
            var token = await auth.GetCopilotTokenAsync();
            var head = token.Length > 16 ? token[..16] + "..." : token;

            Console.WriteLine($"Copilot token (head):  {head}");
            Console.WriteLine($"Token expires at:      {auth.CopilotTokenExpiry:O}");
            Console.WriteLine($"Copilot API base URL:  {auth.CopilotApiBaseUrl}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to obtain Copilot token: {ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown subcommand: auth {sub}");
        Console.Error.WriteLine("Available: auth login | auth whoami | auth status | auth copilot-status | auth logout");
        return 2;
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
