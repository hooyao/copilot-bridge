namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:UpstreamRetry</c>.
/// Controls idempotent retry of transient upstream connection failures
/// (<c>net_http_client_execution_error</c> / <c>net_http_ssl_connection_failed</c>)
/// in <see cref="Copilot.CopilotClient.PostMessagesAsync"/>.
/// </summary>
/// <remarks>
/// <para>Retry is only attempted while the failure occurs <b>before</b> response
/// headers are read — at that point the request body has not been processed by
/// the upstream, so re-sending it is idempotent. Once headers arrive (and SSE
/// streaming may have begun), no retry happens: re-sending could duplicate
/// streamed content.</para>
/// <para>These failures cluster (a 30-second window of refused connections from
/// a flaky proxy/VPN/gateway), so a small bounded retry with backoff turns most
/// transient blips transparent without masking a sustained outage.</para>
/// </remarks>
internal sealed class UpstreamRetryOptions
{
    /// <summary>
    /// Number of <i>additional</i> attempts after the first failure. 0 disables
    /// retry entirely. Default 2 (so up to 3 total sends).
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff in milliseconds before the first retry. Default 250.</summary>
    public int BaseDelayMs { get; set; } = 250;

    /// <summary>
    /// Backoff multiplier applied per attempt (exponential). Default 3 →
    /// 250ms, 750ms. Capped at <see cref="MaxDelayMs"/>.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 3.0;

    /// <summary>Upper bound on any single backoff delay, milliseconds. Default 2000.</summary>
    public int MaxDelayMs { get; set; } = 2000;
}
