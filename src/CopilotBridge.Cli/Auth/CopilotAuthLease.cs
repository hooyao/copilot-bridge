namespace CopilotBridge.Cli.Auth;

/// <summary>
/// One immutable authentication generation for Copilot CAPI. Direct or exchanged
/// bearer and endpoint
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
    public CopilotLeaseKind Kind { get; init; }
    public string IntegrationId { get; init; } = "vscode-chat";
    internal int CredentialVersion { get; init; }
    internal string CredentialId { get; init; } = "";
    internal long CredentialGeneration { get; init; }
    internal bool CredentialIsRefreshable { get; init; }

    public override string ToString() =>
        $"CopilotAuthLease {{ Token = (redacted), ApiBaseUrl = {ApiBaseUrl}, "
        + $"RefreshAt = {RefreshAt:O}, ServerExpiresAt = {ServerExpiresAt:O}, "
        + $"Generation = {Generation}, Kind = {Kind}, IntegrationId = {IntegrationId} }}";
}

public enum CopilotLeaseKind
{
    Exchanged,
    Direct,
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
