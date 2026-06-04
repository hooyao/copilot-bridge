namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section
/// <c>Pipeline:ResponseModelRewrite</c>. Controls whether
/// <see cref="Pipeline.Response.ResponseModelRewriteStage"/> restores the
/// client-requested model id in the response body / message_start event.
/// </summary>
/// <remarks>
/// On by default. Reasons to turn off:
/// <list type="bullet">
///   <item>You want downstream tooling (ccusage, telemetry) to report the
///         real back-end model that ran the request rather than the model
///         the client asked for.</item>
///   <item>You're debugging a discrepancy between request and response
///         shapes and need the wire bytes untouched on the response side.</item>
/// </list>
/// Off by default would surprise Claude Code in the other direction —
/// every resume reconciles the session against the model id stored in the
/// jsonl, and seeing claude-opus-4-7 there after asking for claude-opus-4-8
/// makes the client switch to 4-7 mid-conversation.
/// </remarks>
internal sealed class ResponseModelRewriteOptions
{
    public bool Enabled { get; set; } = true;
}
