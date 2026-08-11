namespace CopilotBridge.Cli.Auth;

/// <summary>
/// One immutable authentication generation for Copilot CAPI. Token and endpoint
/// must travel together; Generation lets a 401 reject only the lease actually
/// used instead of discarding a newer concurrent refresh.
/// </summary>
public sealed record CopilotAuthLease
{
    public required string Token { get; init; }
    public required string ApiBaseUrl { get; init; }
    public required DateTimeOffset RefreshAt { get; init; }
    public required DateTimeOffset ServerExpiresAt { get; init; }
    public required long Generation { get; init; }

    public override string ToString() =>
        $"CopilotAuthLease {{ Token = (redacted), ApiBaseUrl = {ApiBaseUrl}, "
        + $"RefreshAt = {RefreshAt:O}, ServerExpiresAt = {ServerExpiresAt:O}, "
        + $"Generation = {Generation} }}";
}
