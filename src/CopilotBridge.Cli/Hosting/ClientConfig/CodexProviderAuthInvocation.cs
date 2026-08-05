using System.Runtime.CompilerServices;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// The command-auth invocation written into Codex's provider definition.
/// Framework-dependent development runs use <c>dotnet &lt;absolute dll&gt;</c>;
/// Native AOT installs invoke the absolute bridge executable directly.
/// </summary>
internal sealed record CodexProviderAuthInvocation(string Command, IReadOnlyList<string> Args)
{
    internal const int TimeoutMs = 5_000;
    internal const int RefreshIntervalMs = 0;

    internal static CodexProviderAuthInvocation ResolveCurrent()
    {
        // The project has two supported shapes: framework-dependent JIT output and
        // Native AOT. RuntimeFeature avoids Assembly.Location, which is empty and
        // produces an IL3000 warning in a single-file AOT publish.
        var dllPath = RuntimeFeature.IsDynamicCodeSupported
            ? Path.Combine(AppContext.BaseDirectory, "copilot-bridge.dll")
            : null;
        return Resolve(Environment.ProcessPath, dllPath);
    }

    internal static CodexProviderAuthInvocation Resolve(string? processPath, string? assemblyLocation)
    {
        if (!string.IsNullOrWhiteSpace(assemblyLocation)
            && string.Equals(Path.GetExtension(assemblyLocation), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexProviderAuthInvocation(
                "dotnet",
                [Path.GetFullPath(assemblyLocation), "auth", "provider-token"]);
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException(
                "Cannot configure Codex command auth because the bridge executable path is unavailable.");
        }

        return new CodexProviderAuthInvocation(
            Path.GetFullPath(processPath),
            ["auth", "provider-token"]);
    }
}
