using System.Net.Http.Headers;

namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// Builds the HTTP header set Copilot's CAPI endpoints expect, mirroring the
/// official <c>@vscode/copilot-api</c> package's <c>_mixinHeaders</c> function
/// — see <c>references/vscode-copilot-api-pkg/package/dist/index.js</c>
/// (minified, around byte offset 22990 for the function declaration; the
/// constants below land at offsets 23053-23250). Research doc §3.0.4 has the
/// pretty-printed version. Whenever the upstream package version bumps, re-grep
/// <c>_mixinHeaders</c> in <c>dist/index.js</c> and update the constants below.
/// </summary>
internal sealed class CopilotHeaderFactory
{
    // From dist/index.js _mixinHeaders (byte offset ~23053-23250):
    //   r["X-GitHub-Api-Version"]  = "2026-01-09";
    //   r["Copilot-Integration-Id"] = "vscode-chat";   // when prod + license OK
    //   r["Editor-Plugin-Version"] = `copilot-chat/${this._extensionInfo.version}`;
    //   r["Editor-Version"]        = `vscode/${this._extensionInfo.vscodeVersion}`;
    private const string ApiVersion = "2026-01-09";
    private const string IntegrationId = "vscode-chat";

    // Editor identity. These get re-checked against current VS Code Copilot Chat
    // releases periodically; mismatch is a soft signal Copilot may treat traffic
    // differently for quota or routing.
    private const string EditorPluginVersion = "copilot-chat/0.46.0";
    private const string EditorVersion = "vscode/1.95.0";

    // VS Code persists these across launches. We don't bother — Copilot accepts
    // fresh per-process IDs, only stability *within* a process matters for things
    // like x-interaction-id session affinity.
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _machineId = Guid.NewGuid().ToString();
    private readonly string _deviceId = Guid.NewGuid().ToString();

    /// <summary>
    /// Add Copilot-required headers to <paramref name="req"/>, including the bearer auth.
    /// Pass <paramref name="vision"/>=true when the request body contains image content
    /// blocks; Copilot uses this header to gate vision-capable routing.
    /// </summary>
    public void ApplyTo(HttpRequestMessage req, string copilotToken, bool vision = false)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", copilotToken);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        req.Headers.TryAddWithoutValidation("Copilot-Integration-Id", IntegrationId);
        req.Headers.TryAddWithoutValidation("VScode-SessionId", _sessionId);
        req.Headers.TryAddWithoutValidation("VScode-MachineId", _machineId);
        req.Headers.TryAddWithoutValidation("Editor-Device-Id", _deviceId);
        req.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);
        req.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
        if (vision)
            req.Headers.TryAddWithoutValidation("Copilot-Vision-Request", "true");
    }
}
