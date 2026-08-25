using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract: update commit proves local serving health, not GitHub availability.
/// A target/rollback activation must touch no credential state and make no auth
/// request before Ready; ordinary launches retain the interactive auth gate.
/// </summary>
[Collection("gate-env")]
public class UpdateActivationAuthenticationTests
{
    [Theory]
    [InlineData(UpdateWire.RoleTarget)]
    [InlineData(UpdateWire.RoleRollback)]
    public async Task Updater_managed_activation_never_touches_authentication(string role)
    {
        var auth = new RecordingAuthService { ThrowOnAnyAccess = true };
        var context = new UpdateLaunchContext(
            "attempt", role, "pipe", "secret", "0.5.10-beta");

        var warmed = await BridgeStartupHostedService.BootstrapAuthenticationAsync(
            auth, context, NullLogger.Instance, CancellationToken.None);

        Assert.False(warmed);
        Assert.Equal(0, auth.TotalAccesses);
    }

    [Fact]
    public async Task Complete_update_environment_reaches_startup_without_authentication()
    {
        var auth = new RecordingAuthService { ThrowOnAnyAccess = true };
        var saved = new Dictionary<string, string?>();
        void Set(string key, string value)
        {
            saved[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            Set(UpdateLaunchContext.EnvAttempt, "attempt");
            Set(UpdateLaunchContext.EnvRole, UpdateWire.RoleTarget);
            Set(UpdateLaunchContext.EnvPipe, "pipe");
            Set(UpdateLaunchContext.EnvToken, "secret");
            Set(UpdateLaunchContext.EnvVersion, "0.5.10-beta");

            await CreateStartupService(auth).StartAsync(CancellationToken.None);

            Assert.Equal(0, auth.TotalAccesses);
        }
        finally
        {
            foreach (var (key, value) in saved)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task Ordinary_launch_still_warms_authentication_before_serving()
    {
        var auth = new RecordingAuthService { Authenticated = true };

        var warmed = await BridgeStartupHostedService.BootstrapAuthenticationAsync(
            auth, updateContext: null, NullLogger.Instance, CancellationToken.None);

        Assert.True(warmed);
        Assert.Equal(1, auth.AuthenticationChecks);
        Assert.Equal(1, auth.EnsureCalls);
        Assert.Equal(1, auth.CopilotLeaseCalls);
    }

    [Fact]
    public async Task Ordinary_launch_auth_failure_remains_a_startup_failure()
    {
        var expected = new HttpRequestException("refresh endpoint unavailable");
        var auth = new RecordingAuthService
        {
            Authenticated = true,
            EnsureFailure = expected,
        };

        var error = await Assert.ThrowsAsync<BridgeStartupException>(() =>
            BridgeStartupHostedService.BootstrapAuthenticationAsync(
                auth, updateContext: null, NullLogger.Instance, CancellationToken.None));

        Assert.Same(expected, error.InnerException);
        Assert.Equal(1, auth.EnsureCalls);
        Assert.Equal(0, auth.CopilotLeaseCalls);
    }

    private static BridgeStartupHostedService CreateStartupService(IAuthService auth) => new(
        auth,
        Options.Create(new BridgeServerOptions()),
        Options.Create(new RoutesConfig()),
        Options.Create(new UpstreamTimeoutOptions()),
        Options.Create(new UpstreamRetryOptions()),
        Options.Create(new ResponseLeakGuardOptions()),
        Options.Create(new ToolInputValidationOptions()),
        new ModelProfileCatalog(),
        new CodexModelProfileCatalog(),
        NullLogger<BridgeStartupHostedService>.Instance);

    private sealed class RecordingAuthService : IAuthService
    {
        private int _authenticationChecks;

        public bool ThrowOnAnyAccess { get; init; }
        public bool Authenticated { get; init; }
        public Exception? EnsureFailure { get; init; }
        public int AuthenticationChecks => _authenticationChecks;
        public int EnsureCalls { get; private set; }
        public int CopilotLeaseCalls { get; private set; }
        public int StatusReads { get; private set; }
        public int TotalAccesses =>
            AuthenticationChecks + EnsureCalls + CopilotLeaseCalls + StatusReads;

        public bool IsAuthenticated
        {
            get
            {
                _authenticationChecks++;
                ThrowIfForbidden();
                return Authenticated;
            }
        }

        public string TokenLocation
        {
            get
            {
                StatusReads++;
                ThrowIfForbidden();
                return "github_credentials.dat";
            }
        }

        public string? CopilotApiBaseUrl
        {
            get
            {
                StatusReads++;
                ThrowIfForbidden();
                return "https://api.githubcopilot.com";
            }
        }

        public DateTimeOffset? CopilotTokenExpiry
        {
            get
            {
                StatusReads++;
                ThrowIfForbidden();
                return null;
            }
        }

        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default)
        {
            EnsureCalls++;
            ThrowIfForbidden();
            if (EnsureFailure is not null) return ValueTask.FromException<string>(EnsureFailure);
            return ValueTask.FromResult("github-token");
        }

        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotLeaseRejection? rejection = null,
            CancellationToken ct = default)
        {
            CopilotLeaseCalls++;
            ThrowIfForbidden();
            return ValueTask.FromResult(new CopilotAuthLease
            {
                Token = "copilot-token",
                ApiBaseUrl = "https://api.githubcopilot.com",
                RefreshAt = DateTimeOffset.MaxValue,
                ServerExpiresAt = DateTimeOffset.MaxValue,
                Generation = 1,
            });
        }

        public void SignOut() => throw new NotSupportedException();

        private void ThrowIfForbidden()
        {
            if (ThrowOnAnyAccess)
                throw new InvalidOperationException(
                    "Updater-managed activation touched authentication.");
        }
    }
}
