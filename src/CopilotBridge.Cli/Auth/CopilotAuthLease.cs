namespace CopilotBridge.Cli.Auth;

/// <summary>
/// One immutable authentication generation for Copilot CAPI. Token and endpoint
/// must travel together; Generation lets a CAPI authentication rejection discard
/// only the lease actually used instead of a newer concurrent refresh.
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

/// <summary>
/// The closed set of CAPI statuses that can reject a Copilot lease. Keeping this
/// separate from arbitrary HTTP status codes prevents unrelated 4xx responses
/// from silently acquiring token-refresh semantics.
/// </summary>
public enum CopilotLeaseRejectionReason
{
    Unauthorized,
    Forbidden,
}

/// <summary>
/// One rejected lease generation and the exact CAPI reason that rejected it.
/// </summary>
public readonly record struct CopilotLeaseRejection(
    CopilotAuthLease Lease,
    CopilotLeaseRejectionReason Reason);
