using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class AuthenticationSecretRedactionTests : IDisposable
{
    private const string OldAccess = "ghu_OLD_ACCESS_DO_NOT_LOG";
    private const string OldRefresh = "ghr_OLD_REFRESH_DO_NOT_LOG";
    private const string NewAccess = "ghu_NEW_ACCESS_DO_NOT_LOG";
    private const string NewRefresh = "ghr_NEW_REFRESH_DO_NOT_LOG";
    private const string CopilotBearer = "tid=COPILOT_BEARER_DO_NOT_LOG";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-redaction-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Credential_and_lease_ToString_are_secret_free()
    {
        var credential = Credential();
        var customCredential = new CredentialFileRecord
        {
            Version = CredentialFileRecord.CustomOAuthDirectVersion,
            AccessToken = OldAccess,
            RefreshToken = OldRefresh,
            OAuthClientId = "Ov23_PUBLIC_CLIENT_ID",
            CredentialId = "custom-redaction-test",
            Generation = 1,
        };
        var lease = new CopilotAuthLease
        {
            Token = CopilotBearer,
            ApiBaseUrl = "https://api.test",
            RefreshAt = _now.AddMinutes(20),
            ServerExpiresAt = _now.AddMinutes(30),
            Generation = 3,
        };
        var refreshRequest = new RefreshTokenRequest
        {
            ClientId = "client",
            RefreshToken = OldRefresh,
        };

        AssertSecretFree(credential.ToString());
        AssertSecretFree(customCredential.ToString());
        Assert.Contains("Ov23_PUBLIC_CLIENT_ID", customCredential.ToString(),
            StringComparison.Ordinal);
        AssertSecretFree(lease.ToString());
        AssertSecretFree(refreshRequest.ToString());
    }

    [Fact]
    public async Task Success_and_refresh_logs_never_contain_credential_material()
    {
        var protector = new TestProtector();
        var store = new CredentialStore(Path.Combine(_root, "primary"), protector);
        var source = Credential();
        store.Save(new CredentialFileRecord
        {
            Version = CredentialFileRecord.CopilotPluginVersion,
            AccessToken = source.AccessToken,
            AccessTokenExpiresAt = source.AccessTokenExpiresAt,
            RefreshToken = source.RefreshToken,
            RefreshTokenExpiresAt = source.RefreshTokenExpiresAt,
            CredentialId = "redaction-test",
            Generation = source.Generation,
        });
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"" + NewAccess
                + "\",\"expires_in\":3600,\"refresh_token\":\"" + NewRefresh
                + "\",\"refresh_token_expires_in\":7200}"),
            Json(HttpStatusCode.OK,
                "{\"token\":\"" + CopilotBearer
                + "\",\"expires_at\":2000000000,\"refresh_in\":1500,"
                + "\"endpoints\":{\"api\":\"https://api.test\"}}"),
        ]));
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        var credentials = new CredentialService(
            factory,
            store,
            new ManualTimeProvider(_now),
            loggerFactory.CreateLogger<CredentialService>());
        using var auth = new AuthService(
            factory,
            credentials,
            new ManualTimeProvider(_now),
            loggerFactory,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);

        _ = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.NotEmpty(provider.Events);
        var rendered = string.Join('\n', provider.Events.Select(evt =>
            evt.Message + " " + string.Join(' ', evt.Properties.Values)));
        AssertSecretFree(rendered);
    }

    [Fact]
    public void Copilot_status_output_contains_metadata_but_not_bearer()
    {
        var lease = new CopilotAuthLease
        {
            Token = CopilotBearer,
            ApiBaseUrl = "https://api.test.githubcopilot.com",
            RefreshAt = _now.AddMinutes(20),
            ServerExpiresAt = _now.AddMinutes(30),
            Generation = 2,
            IntegrationId = CopilotHeaderFactory.CustomOAuthIntegrationId,
        };
        using var output = new StringWriter();

        AuthCommand.WriteCopilotStatus(output, lease);

        var text = output.ToString();
        Assert.Contains("api.test.githubcopilot.com", text);
        Assert.Contains(lease.RefreshAt.ToString("O"), text);
        Assert.Contains(CopilotHeaderFactory.CustomOAuthIntegrationId, text);
        AssertSecretFree(text);
        Assert.DoesNotContain("token (head)", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Terminal_errors_never_embed_the_rejected_tokens()
    {
        var errors = new Exception[]
        {
            new GitHubReauthenticationRequiredException("refresh rejected"),
            new GitHubOAuthException("refresh-token exchange", "bad_refresh_token", HttpStatusCode.Unauthorized),
            new GitHubApiRequestException("Copilot token exchange", HttpStatusCode.Unauthorized),
        };

        foreach (var error in errors) AssertSecretFree(error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private GitHubCredentialRecord Credential() => new()
    {
        AccessToken = OldAccess,
        AccessTokenExpiresAt = _now.AddMinutes(1),
        RefreshToken = OldRefresh,
        RefreshTokenExpiresAt = _now.AddDays(1),
        Generation = 1,
    };

    private static void AssertSecretFree(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotContain(OldAccess, text, StringComparison.Ordinal);
        Assert.DoesNotContain(OldRefresh, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NewAccess, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NewRefresh, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CopilotBearer, text, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => [0xD1, .. plaintext.Select(x => (byte)(x ^ 0x41))];
        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length == 0 || blob[0] != 0xD1)
                throw new System.Security.Cryptography.CryptographicException();
            return blob[1..].Select(x => (byte)(x ^ 0x41)).ToArray();
        }
    }
}
