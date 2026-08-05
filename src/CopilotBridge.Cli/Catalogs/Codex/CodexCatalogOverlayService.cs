using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Copilot;
using Microsoft.Extensions.Logging;

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
    private readonly ICopilotClient _copilot;
    private readonly ILogger<CodexCatalogOverlayService> _log;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private CacheEntry? _lastKnownGood;
    private Task<CodexCatalogOverlaySnapshot>? _refresh;

    public CodexCatalogOverlayService(ICopilotClient copilot, ILogger<CodexCatalogOverlayService> log)
        : this(copilot, log, DefaultTtl, () => DateTimeOffset.UtcNow) { }

    internal CodexCatalogOverlayService(
        ICopilotClient copilot,
        ILogger<CodexCatalogOverlayService> log,
        TimeSpan ttl,
        Func<DateTimeOffset> utcNow)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        _copilot = copilot;
        _log = log;
        _ttl = ttl;
        _utcNow = utcNow;
    }

    public ValueTask<CodexCatalogOverlaySnapshot> GetAsync(CancellationToken ct = default)
    {
        Task<CodexCatalogOverlaySnapshot> refresh;
        lock (_gate)
        {
            if (_lastKnownGood is { } cached && _utcNow() - cached.RefreshedAt < _ttl)
                return ValueTask.FromResult(Snapshot(cached.Models, validated: true, stale: false));
            refresh = _refresh ??= RefreshAsync();
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
            // ICopilotClient's named metadata HttpClient owns the finite two-minute
            // upper bound. The shared refresh is deliberately not tied to one
            // caller's cancellation: a cancelled Codex startup must not cancel the
            // result awaited by other concurrent clients.
            var response = await _copilot.GetModelsAsync(CancellationToken.None);
            var models = response.Data.ToArray();
            lock (_gate) _lastKnownGood = new CacheEntry(models, _utcNow());
            return Snapshot(models, validated: true, stale: false);
        }
        catch (Exception ex)
        {
            CacheEntry? stale;
            lock (_gate) stale = _lastKnownGood;
            _log.LogWarning(
                "Copilot model metadata refresh failed ({ErrorType}); Codex catalog capacity is degraded.",
                ex.GetType().Name);
            return stale is null
                ? Snapshot([], validated: false, stale: false)
                : Snapshot(stale.Models, validated: true, stale: true);
        }
        finally
        {
            lock (_gate) _refresh = null;
        }
    }

    private static CodexCatalogOverlaySnapshot Snapshot(IReadOnlyList<CopilotModel> models, bool validated, bool stale) =>
        new() { Models = models, IsValidated = validated, IsStale = stale };

    private sealed record CacheEntry(IReadOnlyList<CopilotModel> Models, DateTimeOffset RefreshedAt);
}
