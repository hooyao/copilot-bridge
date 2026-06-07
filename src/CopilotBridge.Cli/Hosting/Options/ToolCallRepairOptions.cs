namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section
/// <c>Pipeline:ToolCallRepair</c>. Controls whether
/// <see cref="Pipeline.Response.ToolCallRepairStage"/> buffers and repairs 
/// malformed tool calls emitted by the Copilot backend.
/// </summary>
/// <remarks>
/// On by default to prevent Claude Code from crashing on "Invalid tool parameters".
/// When on, tool call streaming is buffered (delayed until <c>content_block_stop</c>).
/// </remarks>
internal sealed class ToolCallRepairOptions
{
    public bool Enabled { get; set; } = false;
}
