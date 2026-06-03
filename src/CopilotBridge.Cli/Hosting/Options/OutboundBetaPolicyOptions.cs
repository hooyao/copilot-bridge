namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:OutboundBeta</c>.
/// Lets operators control which <c>anthropic-beta</c> tokens the bridge
/// strips from EVERY outbound request, before the (per-profile) strips
/// declared in <c>ModelProfile.StripBetas</c> run.
/// </summary>
/// <remarks>
/// <para>Defaults ship two strip patterns that fail Copilot on every backend
/// we've seen:</para>
/// <list type="bullet">
///   <item><c>advisor-tool-*</c> — Copilot's gateway returns <c>"unsupported
///         beta header(s): advisor-tool-2026-03-01"</c> on every model when
///         Claude Code 4.8 sends this token.</item>
///   <item><c>structured-outputs-*</c> — GCP Vertex AI organization policy
///         <c>vertexai.allowedPartnerModelFeatures</c> blocks the
///         <c>structured_outputs</c> feature on Anthropic partner models,
///         returning <c>"Organization Policy constraint ... violated ...
///         FAILED_PRECONDITION"</c>. Other Copilot backends (Bedrock) accept
///         it, but the bridge can't reliably tell which backend a tenant runs
///         on, and Claude Code's response handling does not depend on
///         structured_outputs, so the safer default is to strip universally.
///         Operators on a Bedrock-only tenant who want structured_outputs
///         back can remove this entry from appsettings.json.</item>
/// </list>
/// <para>Patterns follow the same trailing-wildcard syntax as the per-rule
/// header <c>Remove</c> entries: a trailing <c>*</c> matches any tokens
/// sharing the prefix; no wildcard means whole-token equality
/// (case-insensitive).</para>
/// </remarks>
internal sealed class OutboundBetaPolicyOptions
{
    public List<string> GlobalStrip { get; set; } = new();
}
