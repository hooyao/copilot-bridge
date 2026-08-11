using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class AuthServiceGitHubRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-github-401-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Copilot_exchange_401_rotates_GitHub_credential_and_replays_once()
    {
        var store = CreateStore(refreshable: true);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_new\",\"expires_in\":3600,\"refresh_token\":\"ghr_new\",\"refresh_token_expires_in\":7200}"),
            Json(HttpStatusCode.OK,
                "{\"token\":\"copilot_new\",\"expires_at\":2000000000,\"refresh_in\":1500,\"endpoints\":{\"api\":\"https://api.new.test\"}}"),
        ]));
        using var auth = CreateAuth(store, handler);

        var lease = await auth.GetCopilotTokenAsync(ct: CancellationToken.None);

        Assert.Equal("copilot_new", lease.Token);
        Assert.Equal("https://api.new.test", lease.ApiBaseUrl);
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("ghu_old", requests[0].AuthorizationParameter);
        Assert.Equal("2025-04-01", requests[0].ApiVersion);
        Assert.Contains("login/oauth/access_token", requests[1].Uri.AbsoluteUri);
        Assert.Contains("ghr_old", requests[1].Body);
        Assert.Equal("ghu_new", requests[2].AuthorizationParameter);
        Assert.Equal("2025-04-01", requests[2].ApiVersion);
        Assert.Equal(2, store.TryLoad()!.Record.Generation);
    }

    [Fact]
    public async Task Copilot_exchange_second_401_is_terminal_after_one_GitHub_refresh()
    {
        var store = CreateStore(refreshable: true);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_new\",\"expires_in\":3600,\"refresh_token\":\"ghr_new\",\"refresh_token_expires_in\":7200}"),
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
        ]));
        using var auth = CreateAuth(store, handler);

        var error = await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(ct: CancellationToken.None).AsTask());

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, store.TryLoad()!.Record.Generation);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(ct: CancellationToken.None).AsTask());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Copilot_exchange_401_with_legacy_token_requires_login_without_replay()
    {
        var store = CreateStore(refreshable: false);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
        ]));
        using var auth = CreateAuth(store, handler);

        var error = await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(ct: CancellationToken.None).AsTask());

        Assert.Contains("auth logout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.Equal("ghu_legacy", store.TryLoad()!.Record.AccessToken);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetCopilotTokenAsync(ct: CancellationToken.None).AsTask());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Background_refresh_401_without_refresh_token_is_terminal_and_not_rearmed()
    {
        // Contract: when a scheduled Copilot-bearer refresh discovers that GitHub
        // rejects a non-refreshable credential, interactive authorization is the
        // only recovery. The timer must terminate instead of retrying every 30s.
        var store = CreateStore(refreshable: false);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                "{\"token\":\"copilot_initial\",\"expires_at\":2000000000,\"refresh_in\":300,\"endpoints\":{\"api\":\"https://api.initial.test\"}}"),
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
        ]));
        var time = new TimerTimeProvider(_now);
        var logs = new TimerOutcomeLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var auth = CreateAuth(
            store,
            handler,
            time,
            loggerFactory,
            enableBackgroundRefresh: true);

        await auth.GetCopilotTokenAsync(ct: CancellationToken.None);
        Assert.Equal(1, time.TimerCreationCount);

        time.Advance(TimeSpan.FromMinutes(1));
        var outcome = await logs.TimerOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("outcome=terminal_reauth_required", outcome, StringComparison.Ordinal);
        await EventuallyAsync(() => time.TimerCreationCount > 1 || time.DisposedTimerCount > 0);
        Assert.Equal(1, time.TimerCreationCount);
        Assert.Equal(2, handler.Requests.Count);

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Yield();
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, time.TimerCreationCount);
    }

    [Fact]
    public async Task WhoAmI_401_rotates_GitHub_credential_and_replays_once()
    {
        var store = CreateStore(refreshable: true);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_new\",\"expires_in\":3600,\"refresh_token\":\"ghr_new\",\"refresh_token_expires_in\":7200}"),
            Json(HttpStatusCode.OK, "{\"login\":\"contract-user\",\"id\":42}"),
        ]));
        using var auth = CreateAuth(store, handler);

        var user = await auth.GetGitHubUserAsync(CancellationToken.None);

        Assert.Equal("contract-user", user.Login);
        Assert.Equal(42, user.Id);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, store.TryLoad()!.Record.Generation);
        var requests = handler.Requests.ToArray();
        Assert.Null(requests[0].ApiVersion);
        Assert.Null(requests[2].ApiVersion);
    }

    [Fact]
    public async Task WhoAmI_second_401_marks_generation_terminal_across_calls()
    {
        var store = CreateStore(refreshable: true);
        var handler = new SequenceHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Json(HttpStatusCode.OK,
                "{\"access_token\":\"ghu_new\",\"expires_in\":3600,\"refresh_token\":\"ghr_new\",\"refresh_token_expires_in\":7200}"),
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
        ]));
        using var auth = CreateAuth(store, handler);

        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetGitHubUserAsync(CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<GitHubReauthenticationRequiredException>(() =>
            auth.GetGitHubUserAsync(CancellationToken.None).AsTask());

        Assert.Equal(3, handler.Requests.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private GitHubCredentialStore CreateStore(bool refreshable)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var store = new GitHubCredentialStore(
            Path.Combine(_root, suffix, "primary"),
            Path.Combine(_root, suffix, "fallback"),
            new TestProtector());
        if (!refreshable)
        {
            store.SaveLegacy("ghu_legacy");
            return store;
        }

        store.SaveNew(new GitHubCredentialRecord
        {
            AccessToken = "ghu_old",
            AccessTokenExpiresAt = _now.AddHours(1),
            RefreshToken = "ghr_old",
            RefreshTokenExpiresAt = _now.AddDays(1),
            Generation = 1,
        });
        return store;
    }

    private AuthService CreateAuth(
        GitHubCredentialStore store,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        bool enableBackgroundRefresh = false) =>
        new(
            new SingleClientHttpClientFactory(new HttpClient(handler)),
            store,
            timeProvider ?? new ManualTimeProvider(_now),
            loggerFactory ?? NullLoggerFactory.Instance,
            onDeviceCodeIssued: null,
            enableBackgroundRefresh);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly object _gate = new();
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiVersion = request.Headers.TryGetValues(
                "X-GitHub-Api-Version", out var values)
                ? string.Join(',', values)
                : null;
            Requests.Enqueue(new CapturedRequest(
                request.RequestUri!, request.Headers.Authorization?.Parameter, body, apiVersion));
            lock (_gate)
            {
                if (responses.Count == 0)
                    throw new InvalidOperationException("Unexpected auth request.");
                return responses.Dequeue();
            }
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? AuthorizationParameter,
        string Body,
        string? ApiVersion);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TimerTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private int _timerCreationCount;
        private int _disposedTimerCount;

        public int TimerCreationCount => Volatile.Read(ref _timerCreationCount);
        public int DisposedTimerCount => Volatile.Read(ref _disposedTimerCount);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return now;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer;
            lock (_gate)
            {
                timer = new ManualTimer(
                    this,
                    callback,
                    state,
                    dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime,
                    period);
                _timers.Add(timer);
                Interlocked.Increment(ref _timerCreationCount);
            }
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                now += by;
                foreach (var timer in _timers)
                {
                    if (!timer.TryTakeDue(now, out var callback)) continue;
                    callbacks.Add(callback);
                }
            }

            foreach (var (callback, state) in callbacks)
                callback(state);
        }

        private void MarkDisposed() => Interlocked.Increment(ref _disposedTimerCount);

        private sealed class ManualTimer(
            TimerTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset? dueAt,
            TimeSpan period) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset? _dueAt = dueAt;
            private TimeSpan _period = period;

            public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
            {
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : owner.GetUtcNow() + dueTime;
                    _period = nextPeriod;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _dueAt = null;
                    owner.MarkDisposed();
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryTakeDue(
                DateTimeOffset current,
                out (TimerCallback Callback, object? State) invocation)
            {
                if (_disposed || _dueAt is null || _dueAt > current)
                {
                    invocation = default;
                    return false;
                }

                invocation = (callback, state);
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : current + _period;
                return true;
            }
        }
    }

    private sealed class TimerOutcomeLoggerProvider : ILoggerProvider
    {
        public TaskCompletionSource<string> TimerOutcome { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new TimerOutcomeLogger(this);

        public void Dispose() { }

        private sealed class TimerOutcomeLogger(TimerOutcomeLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (message.StartsWith(
                        "Copilot bearer refresh trigger=timer outcome=",
                        StringComparison.Ordinal))
                    owner.TimerOutcome.TrySetResult(message);
            }
        }
    }

    private sealed class TestProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => [0xC3, .. plaintext.Select(x => (byte)(x ^ 0x33))];
        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length == 0 || blob[0] != 0xC3)
                throw new System.Security.Cryptography.CryptographicException();
            return blob[1..].Select(x => (byte)(x ^ 0x33)).ToArray();
        }
    }
}
