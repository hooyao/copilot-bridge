using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal sealed record CodexCatalogOverlaySnapshot
{
    public required IReadOnlyList<CopilotModel> Models { get; init; }
    public required bool IsValidated { get; init; }
    public required bool IsStale { get; init; }
}

internal sealed class CodexCatalogOverlayService
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(5);
    private readonly ICopilotClient _copilot;
    private readonly ILogger<CodexCatalogOverlayService> _log;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeSpan _failureCooldown;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private CacheEntry? _lastKnownGood;
    private Task<CodexCatalogOverlaySnapshot>? _refresh;
    private DateTimeOffset? _retryAfter;

    public CodexCatalogOverlayService(
        ICopilotClient copilot,
        ILogger<CodexCatalogOverlayService> log,
        IOptions<CodexModelCatalogOptions> options)
        : this(
            copilot,
            log,
            DefaultTtl,
            DefaultRefreshTimeout,
            TimeSpan.FromSeconds(options.Value.LiveOverlayFailureCooldownSeconds),
            () => DateTimeOffset.UtcNow) { }

    internal CodexCatalogOverlayService(
        ICopilotClient copilot,
        ILogger<CodexCatalogOverlayService> log,
        TimeSpan ttl,
        TimeSpan refreshTimeout,
        TimeSpan failureCooldown,
        Func<DateTimeOffset> utcNow)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (refreshTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshTimeout));
        if (failureCooldown <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(failureCooldown));
        _copilot = copilot;
        _log = log;
        _ttl = ttl;
        _refreshTimeout = refreshTimeout;
        _failureCooldown = failureCooldown;
        _utcNow = utcNow;
    }

    public ValueTask<CodexCatalogOverlaySnapshot> GetAsync(CancellationToken ct = default)
    {
        Task<CodexCatalogOverlaySnapshot> refresh;
        lock (_gate)
        {
            var now = _utcNow();
            if (_lastKnownGood is { } cached && now - cached.RefreshedAt < _ttl)
                return ValueTask.FromResult(Snapshot(cached.Models, validated: true, stale: false));
            if (_refresh is not null)
            {
                refresh = _refresh;
            }
            else if (_retryAfter is { } retryAfter && now < retryAfter)
            {
                return ValueTask.FromResult(FallbackSnapshot(_lastKnownGood));
            }
            else
            {
                refresh = _refresh = RefreshAsync();
            }
        }
        return new ValueTask<CodexCatalogOverlaySnapshot>(refresh.WaitAsync(ct));
    }

    private async Task<CodexCatalogOverlaySnapshot> RefreshAsync()
    {
        // Ensure GetAsync publishes the in-flight task before a test double or
        // cached HTTP handler can complete synchronously; otherwise finally may
        // clear _refresh before the assignment that stores the completed task.
        await Task.Yield();
        try
        {
            // Catalog discovery is startup-critical, so it has a short bound independent
            // of both the metadata client's coarse timeout and any one caller. WaitAsync
            // enforces that bound even if an ICopilotClient implementation ignores the
            // cancellation token. The shared refresh remains alive when one caller leaves.
            using var refreshCts = new CancellationTokenSource(_refreshTimeout);
            var response = await _copilot.GetModelsAsync(refreshCts.Token)
                .AsTask()
                .WaitAsync(refreshCts.Token);
            var models = response.Data.ToArray();
            lock (_gate)
            {
                _lastKnownGood = new CacheEntry(models, _utcNow());
                _retryAfter = null;
            }
            return Snapshot(models, validated: true, stale: false);
        }
        catch (Exception ex)
        {
            CacheEntry? stale;
            lock (_gate)
            {
                stale = _lastKnownGood;
                _retryAfter = _utcNow() + _failureCooldown;
            }
            _log.LogWarning(
                "Copilot model metadata refresh failed ({ErrorType}); Codex catalog capacity "
                + "is degraded; retry_in_seconds={RetryInSeconds}.",
                ex.GetType().Name,
                (long)_failureCooldown.TotalSeconds);
            return FallbackSnapshot(stale);
        }
        finally
        {
            lock (_gate) _refresh = null;
        }
    }

    private static CodexCatalogOverlaySnapshot Snapshot(IReadOnlyList<CopilotModel> models, bool validated, bool stale) =>
        new() { Models = models, IsValidated = validated, IsStale = stale };

    private static CodexCatalogOverlaySnapshot FallbackSnapshot(CacheEntry? stale) =>
        stale is null
            ? Snapshot([], validated: false, stale: false)
            : Snapshot(stale.Models, validated: true, stale: true);

    private sealed record CacheEntry(IReadOnlyList<CopilotModel> Models, DateTimeOffset RefreshedAt);
}
