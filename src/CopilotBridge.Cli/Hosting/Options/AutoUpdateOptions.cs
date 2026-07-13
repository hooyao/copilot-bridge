namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>AutoUpdate</c>. Controls the
/// serve-only startup update gate. The code defaults here are authoritative:
/// an installation upgraded from a build that predates this section has no
/// <c>AutoUpdate</c> keys, so the feature must still default to enabled
/// stable-only checking from these POCO defaults alone — never from the stock
/// JSON (which a successful config migration overlays with the old file's
/// values and would therefore not reintroduce for an old installation).
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
