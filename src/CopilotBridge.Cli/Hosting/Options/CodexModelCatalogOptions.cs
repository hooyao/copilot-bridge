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

    /// <summary>
    /// Whether a confirmed-absent exact tag falls back to the compile-time
    /// bundled snapshot instead of a metadata error. Default-on in code so an
    /// upgraded installation whose older appsettings lacks this key still
    /// serves a catalog to a client whose release was not tagged yet.
    /// </summary>
    public bool BuiltinFallbackEnabled { get; set; } = true;

    /// <summary>
    /// Exact process-local delay before a failed live Copilot /models overlay
    /// may be attempted again. Separate from successful-overlay freshness.
    /// </summary>
    public int LiveOverlayFailureCooldownSeconds { get; set; } = 300;

    /// <summary>
    /// How long a definitive upstream 404 for one exact version is trusted.
    /// Deliberately separate from <see cref="SourceTtlHours"/>: that TTL asks
    /// whether validated content changed, where staleness is harmless, while
    /// this one asks whether a tag exists yet, where staleness means serving a
    /// compile-time snapshot after the real catalog went live. Use
    /// <see cref="EffectiveAbsenceTtlHours"/> rather than this value directly —
    /// an operator who lowered SourceTtlHours must not be forced to also set
    /// this key just to keep the bridge starting.
    /// </summary>
    public int AbsenceTtlHours { get; set; } = 6;

    /// <summary>
    /// The absence TTL actually applied: never longer than the source TTL, so
    /// a confirmed 404 is always re-checked at least as often as validated
    /// content is revalidated, without rejecting a pre-existing configuration
    /// that set a shorter source TTL and knows nothing about this key.
    /// </summary>
    public int EffectiveAbsenceTtlHours => Math.Max(1, Math.Min(AbsenceTtlHours, SourceTtlHours));

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
        // Only the value's own range is validated. Its relation to SourceTtlHours
        // is enforced by clamping in EffectiveAbsenceTtlHours instead, because
        // rejecting the pair here would break startup for an already-valid
        // installation that lowered SourceTtlHours before this key existed.
        if (options.AbsenceTtlHours is < 1 or > 168)
            errors.Add("Codex.ModelCatalog.AbsenceTtlHours must be between 1 and 168.");
        if (options.LiveOverlayFailureCooldownSeconds is < 1 or > 3600)
        {
            errors.Add(
                "Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds value "
                + $"{options.LiveOverlayFailureCooldownSeconds} is outside the supported range 1..3600.");
        }
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
