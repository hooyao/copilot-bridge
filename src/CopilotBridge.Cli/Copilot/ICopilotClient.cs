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
        IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
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

    /// <summary>
    /// <c>POST {baseUrl}/responses</c> — Copilot's native OpenAI Responses
    /// endpoint (the Codex backend). Forwards the already-serialized Responses
    /// request body (post-T2) and returns the response with headers read, body
    /// still streaming, so the caller can pump SSE bytes through. Uses the same
    /// official VS Code Copilot header set as <see cref="PostMessagesAsync"/>
    /// (the factory is endpoint-agnostic — no <c>anthropic-version</c>); Codex's
    /// own <c>x-codex-*</c> headers are dropped (replaced by the official set,
    /// like <c>/cc</c>). Pass <paramref name="vision"/>=true when the body
    /// contains an <c>input_image</c> (adds <c>Copilot-Vision-Request</c>).
    /// Mirror of <see cref="PostMessagesAsync"/> incl. transient-failure retry.
    /// Caller owns + disposes the returned <see cref="HttpResponseMessage"/>.
    /// </summary>
    ValueTask<HttpResponseMessage> PostResponsesAsync(
        ReadOnlyMemory<byte> body,
        bool vision = false,
        CancellationToken ct = default);
}
