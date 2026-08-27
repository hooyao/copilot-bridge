using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Update;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public async Task Updater_managed_activation_resumes_authentication_after_ready_is_sent()
    {
        var auth = new RecordingAuthService { Authenticated = false };
        using var lifetime = new TestHostApplicationLifetime();
        using var logs = new RecordingLoggerProvider();
        using var services = new ServiceCollection()
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton<IAuthService>(auth)
            .AddLogging(builder => builder.AddProvider(logs))
            .BuildServiceProvider();
        var capability = UpdateCapability.Create("post-ready-auth", UpdateWire.RoleTarget);
        var context = new UpdateLaunchContext(
            "post-ready-auth",
            UpdateWire.RoleTarget,
            capability.PipeName,
            capability.Token,
            ProductInfo.Version);
        var reporter = new UpdateReadinessReporter(services, context);
        var ready = UpdatePipeTransport.ServerReceiveLineAsync(
            capability.PipeName,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await reporter.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.NotNull(await ready);
        await auth.FirstEnsureCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await auth.FirstCopilotLeaseCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, auth.AuthenticationChecks);
        Assert.Equal(1, auth.EnsureCalls);
        Assert.Equal(1, auth.CopilotLeaseCalls);
        Assert.Contains(
            logs.Events,
            entry => entry.Message.Contains(
                "No GitHub token on disk — starting device-code flow",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_ready_send_does_not_resume_authentication()
    {
        var auth = new RecordingAuthService { ThrowOnAnyAccess = true };
        using var services = new ServiceCollection()
            .AddSingleton<IAuthService>(auth)
            .AddLogging()
            .BuildServiceProvider();
        var context = new UpdateLaunchContext(
            "failed-ready",
            UpdateWire.RoleTarget,
            "unused-pipe",
            "secret",
            ProductInfo.Version);
        var reporter = new UpdateReadinessReporter(
            services,
            context,
            (_, _) => Task.FromResult(false));

        await reporter.ReportReadyAndResumeAuthenticationAsync(CancellationToken.None);

        Assert.Equal(0, auth.TotalAccesses);
    }

    [Fact]
    public async Task Post_ready_authentication_failure_is_non_fatal_and_actionable()
    {
        var auth = new RecordingAuthService
        {
            Authenticated = true,
            EnsureFailure = new HttpRequestException("offline"),
        };
        using var logs = new RecordingLoggerProvider();
        using var services = new ServiceCollection()
            .AddSingleton<IAuthService>(auth)
            .AddLogging(builder => builder.AddProvider(logs))
            .BuildServiceProvider();
        var context = new UpdateLaunchContext(
            "failed-auth",
            UpdateWire.RoleTarget,
            "unused-pipe",
            "secret",
            ProductInfo.Version);
        var reporter = new UpdateReadinessReporter(
            services,
            context,
            (_, _) => Task.FromResult(true));

        await reporter.ReportReadyAndResumeAuthenticationAsync(CancellationToken.None);

        Assert.Equal(1, auth.EnsureCalls);
        Assert.Equal(0, auth.CopilotLeaseCalls);
        Assert.Contains(
            logs.Events,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("bridge remains serving", StringComparison.Ordinal)
                && entry.Message.Contains("auth login", StringComparison.Ordinal));
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

        public TaskCompletionSource FirstEnsureCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCopilotLeaseCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            FirstEnsureCall.TrySetResult();
            ThrowIfForbidden();
            if (EnsureFailure is not null) return ValueTask.FromException<string>(EnsureFailure);
            return ValueTask.FromResult("github-token");
        }

        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotLeaseRejection? rejection = null,
            CancellationToken ct = default)
        {
            CopilotLeaseCalls++;
            FirstCopilotLeaseCall.TrySetResult();
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

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void NotifyStarted() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
