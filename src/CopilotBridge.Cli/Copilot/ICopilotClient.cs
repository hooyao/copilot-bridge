using CopilotBridge.Cli.Models.Copilot;

namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// HTTP client for the GitHub Copilot CAPI endpoints. Auth and refresh are owned
/// by <see cref="Auth.IAuthService"/>; this client just constructs requests, signs
/// them with the fetched token, and parses responses.
/// </summary>
internal interface ICopilotClient
{
    /// <summary>
    /// <c>GET {baseUrl}/models</c>. Returns the full model list for the active
    /// account; consumers filter by <see cref="CopilotModel.SupportedEndpoints"/>.
    /// </summary>
    ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>POST {baseUrl}/v1/messages</c>. Forwards the already-serialized request
    /// body (post-preprocessing) and returns the response with headers read but
    /// the body still streaming, so the caller can pump SSE bytes through to the
    /// inbound Claude Code connection.
    /// </summary>
    /// <remarks>
    /// Caller owns the returned <see cref="HttpResponseMessage"/> and must dispose
    /// it (which also tears down the underlying request). Pass <paramref name="vision"/>
    /// = <c>true</c> when the body contains image content blocks (adds the
    /// <c>Copilot-Vision-Request</c> header).
    /// </remarks>
    ValueTask<HttpResponseMessage> PostMessagesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        IReadOnlyList<string>? anthropicBeta = null,
        CancellationToken ct = default);

    /// <summary>
    /// <c>POST {baseUrl}/v1/messages/count_tokens</c>. Plain JSON request/response —
    /// no SSE, no pipeline transforms. Forwards the raw inbound body and returns
    /// the response with headers read (caller disposes). Copilot was verified to
    /// support this endpoint with the same wire format as Anthropic
    /// (<c>CopilotGapProbes.CountTokens_ProbeCopilotUpstream</c>), so passthrough
    /// gives Claude Code real counts instead of a stubbed <c>{input_tokens:1}</c>.
    /// </summary>
    ValueTask<HttpResponseMessage> PostCountTokensAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken ct = default);
}
