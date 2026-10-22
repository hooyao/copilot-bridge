namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>Explicit native Codex workaround for Copilot encrypted-task failures.</summary>
internal sealed class CodexPlaintextCollaborationOptions
{
    public bool Enabled { get; set; }
}
