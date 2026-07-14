namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>AutoUpdate</c>. Controls the
/// serve-only startup update gate. The code defaults here are authoritative for
/// the gate that runs BEFORE any migration: an installation upgraded from a build
/// that predates this section has no <c>AutoUpdate</c> keys in its own
/// <c>appsettings.json</c>, so the pre-update gate must default to enabled,
/// stable-only checking from these POCO defaults alone. (Configuration migration
/// walks the NEW template and keeps new-only properties, so a successful update
/// then WRITES this section into the merged file — but that happens after the
/// gate has already decided, which is why the POCO defaults, not the stock JSON,
/// are what the feature relies on to be on by default.)
/// </summary>
internal sealed class AutoUpdateOptions
{
    /// <summary>
    /// When true (default), a single synchronous update check runs before the
    /// proxy host starts on <c>serve</c> (explicit or the parameterless default
    /// action). When false, no GitHub request is made.
    /// </summary>
    public bool EnableAutoUpdate { get; set; } = true;

    /// <summary>
    /// When false (default), only non-prerelease GitHub releases are update
    /// candidates. When true, GitHub prereleases join the candidate set and the
    /// highest semantic version across both channels is selected.
    /// </summary>
    public bool AllowBetaUpdates { get; set; }
}
