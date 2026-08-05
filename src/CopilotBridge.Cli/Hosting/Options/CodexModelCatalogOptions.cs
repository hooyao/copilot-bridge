namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Codex:ModelCatalog</c>.
/// Controls only the Codex metadata endpoint; Responses inference remains
/// available when discovery is disabled.
/// </summary>
internal sealed record class CodexModelCatalogOptions
{
    /// <summary>
    /// Whether <c>GET /codex/models</c> is mapped. Default-on in code so an
    /// upgraded installation whose older appsettings lacks this section keeps
    /// remote catalog discovery enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Absolute persistent-cache override. Empty selects the per-user OS cache root.</summary>
    public string? CacheDirectory { get; set; }

    public int SourceTtlHours { get; set; } = 24;
    public int SourceTimeoutSeconds { get; set; } = 10;
    public int MaxSourceBytes { get; set; } = 4 * 1024 * 1024;
    public int RetentionDays { get; set; } = 90;
    public int MaxRetainedVersions { get; set; } = 32;
}

internal sealed class CodexModelCatalogOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<CodexModelCatalogOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, CodexModelCatalogOptions options)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.CacheDirectory) && !Path.IsPathFullyQualified(options.CacheDirectory))
            errors.Add("Codex.ModelCatalog.CacheDirectory must be an absolute path.");
        if (options.SourceTtlHours is < 1 or > 168)
            errors.Add("Codex.ModelCatalog.SourceTtlHours must be between 1 and 168.");
        if (options.SourceTimeoutSeconds is < 1 or > 60)
            errors.Add("Codex.ModelCatalog.SourceTimeoutSeconds must be between 1 and 60.");
        if (options.MaxSourceBytes is < 65_536 or > 16_777_216)
            errors.Add("Codex.ModelCatalog.MaxSourceBytes must be between 65536 and 16777216.");
        if (options.RetentionDays is < 1 or > 365)
            errors.Add("Codex.ModelCatalog.RetentionDays must be between 1 and 365.");
        if (options.MaxRetainedVersions is < 1 or > 256)
            errors.Add("Codex.ModelCatalog.MaxRetainedVersions must be between 1 and 256.");
        return errors.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(errors);
    }
}
