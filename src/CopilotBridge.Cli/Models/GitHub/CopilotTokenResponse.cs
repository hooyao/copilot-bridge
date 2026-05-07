namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// Response from GET https://api.github.com/copilot_internal/v2/token. Only the fields
/// we currently consume are typed; everything else GitHub returns is ignored.
/// </summary>
internal sealed record CopilotTokenResponse
{
    /// <summary>The bearer token to send as <c>Authorization: Bearer</c> on Copilot requests.</summary>
    public required string Token { get; init; }

    /// <summary>Unix epoch seconds when this token expires.</summary>
    public long ExpiresAt { get; init; }

    /// <summary>Seconds from issuance until the token should be refreshed.</summary>
    public int RefreshIn { get; init; }

    /// <summary>Per-account API endpoints. <c>endpoints.api</c> is the CAPI base URL.</summary>
    public CopilotTokenEndpoints? Endpoints { get; init; }

    /// <summary>Subscription tier (e.g. <c>free_limited_copilot</c>, <c>copilot_for_business</c>).</summary>
    public string? Sku { get; init; }
}

internal sealed record CopilotTokenEndpoints
{
    /// <summary>CAPI base URL — <c>https://api.githubcopilot.com</c> for individual, business/enterprise have their own.</summary>
    public string? Api { get; init; }

    public string? Telemetry { get; init; }
    public string? Proxy { get; init; }
}
