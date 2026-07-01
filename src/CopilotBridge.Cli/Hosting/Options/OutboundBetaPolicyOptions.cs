namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:OutboundBeta</c>.
/// Lets operators control which <c>anthropic-beta</c> tokens the bridge
/// strips from EVERY outbound request, before the (per-profile) strips
/// declared in <c>ModelProfile.StripBetas</c> run.
/// </summary>
/// <remarks>
/// <para>Defaults ship one strip pattern that fails Copilot on every backend
/// we've seen:</para>
/// <list type="bullet">
///   <item><c>advisor-tool-*</c> — Copilot's gateway returns <c>"unsupported
///         beta header(s): advisor-tool-2026-03-01"</c> on every model when
///         Claude Code 4.8 sends this token.</item>
/// </list>
/// <para><b>Not stripped anymore:</b> <c>structured-outputs-*</c> was stripped by
/// default until 2026-07. The reason was a GCP Vertex AI organization policy
/// (<c>vertexai.allowedPartnerModelFeatures</c>) that blocked the
/// <c>structured_outputs</c> feature and returned <c>"Organization Policy
/// constraint ... violated ... FAILED_PRECONDITION"</c>. A live probe in 2026-07
/// found Copilot now accepts <c>structured-outputs-2025-12-15</c> (200 on
/// opus-4.8, both header-only and with a strict-schema tool), so the header is
/// forwarded by default and Claude Code's structured outputs work. A Vertex-backed
/// tenant that still hits FAILED_PRECONDITION can add <c>structured-outputs-*</c>
/// back to <c>Pipeline:OutboundBeta:GlobalStrip</c> in appsettings.json.</para>
/// <para>Patterns follow the same trailing-wildcard syntax as the per-rule
/// header <c>Remove</c> entries: a trailing <c>*</c> matches any tokens
/// sharing the prefix; no wildcard means whole-token equality
/// (case-insensitive).</para>
/// </remarks>
internal sealed class OutboundBetaPolicyOptions
{
    public List<string> GlobalStrip { get; set; } = new();
}
