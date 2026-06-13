using System.Reflection;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Application-wide product identity. Both values are constants for the life of
/// the process, so they live as statics that any layer can read directly —
/// no DI registration, no constructor threading.
/// </summary>
internal static class ProductInfo
{
    public const string Name = "copilot-bridge";

    /// <summary>
    /// Semantic version without the build metadata suffix. Sourced from the
    /// assembly's <see cref="AssemblyInformationalVersionAttribute"/>, which
    /// MSBuild stamps from <c>&lt;Version&gt;</c> in Directory.Build.props
    /// (<c>0.1.0-dev</c> by default) and the release workflow overrides with
    /// <c>-p:Version=x.y.z</c>.
    /// </summary>
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var info = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0-dev";

        // Strip the "+<git-sha>" build metadata SourceLink appends.
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
