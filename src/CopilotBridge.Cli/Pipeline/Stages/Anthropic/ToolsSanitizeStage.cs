using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Cleans up Claude Code's IDE-specific MCP tools that Copilot rejects:
/// <list type="bullet">
///   <item><c>mcp__ide__executeCode</c> — dropped unless
///         <c>defer_loading=true</c> is set, since Copilot has no equivalent
///         IDE-side execution channel and the tool is functionally a no-op
///         (research §3.6 rule 6).</item>
///   <item><c>mcp__ide__getDiagnostics</c> — description rewrite TODO; we
///         don't yet know Copilot's preferred phrasing. Pass through for now.</item>
/// </list>
/// Other tools (Bash / Read / Write / Grep / etc.) are left alone.
/// </summary>
internal sealed class ToolsSanitizeStage : IRequestStage<MessagesRequest>
{
    public string Name => "ToolsSanitize";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        if (ctx.Request.Body.Tools is null || ctx.Request.Body.Tools.Count == 0)
        {
            return Task.CompletedTask;
        }

        var dropped = 0;
        var rebuilt = new List<Tool>(ctx.Request.Body.Tools.Count);
        foreach (var tool in ctx.Request.Body.Tools)
        {
            if (tool.Name == "mcp__ide__executeCode" && tool.DeferLoading != true)
            {
                dropped++;
                continue;
            }
            rebuilt.Add(tool);
        }

        if (dropped > 0)
        {
            ctx.Request.Body = ctx.Request.Body with { Tools = rebuilt };
        }

        Log.Debug($"stage {Name}: dropped {dropped} IDE-only tools (kept {rebuilt.Count})");
        return Task.CompletedTask;
    }
}
