namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Codex:ModelCatalog</c>.
/// Controls only the Codex metadata endpoint; Responses inference remains
/// available when discovery is disabled.
/// </summary>
internal sealed class CodexModelCatalogOptions
{
    /// <summary>
    /// Whether <c>GET /codex/models</c> is mapped. Default-on in code so an
    /// upgraded installation whose older appsettings lacks this section keeps
    /// remote catalog discovery enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
