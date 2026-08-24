using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.ClientConfig;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Pins the bridge catalog contract to the real Codex 0.144.1 Rust
/// <c>ModelsResponse</c>/<c>ModelInfo</c> consumer. A successful
/// <c>codex debug models</c> means every remote entry deserialized before Codex
/// rendered the merged raw catalog; the per-slug instruction comparison then proves
/// the complete instruction source and capacity fields survived that consumer boundary.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class PinnedCodexCatalogConsumerTests
{
    private const string PinnedVersion = "0.144.1";
    private readonly ITestOutputHelper _output;

    public PinnedCodexCatalogConsumerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Endpoint_catalog_deserializes_in_real_pinned_Codex_with_instructions_intact()
    {
        var codexExe = ResolvePinnedCodexExe();
        await using var upstream = new CodexCommandAuthCaptureServer(
            await File.ReadAllTextAsync(CopilotSnapshotPath()));
        await using var bridge = await ServeProcess.StartAsync(
            new ServeInvocation(
                ServeScenario.PassthroughTestUpstream,
                TestUpstreamBaseUrl: upstream.BaseUrl));
        using var home = ClientBehaviorSupport.NewWorkDir("pinned-codex-catalog-consumer");

        var version = await RunCodexAsync(codexExe, home.Path, ["--version"]);
        Assert.Equal(0, version.ExitCode);
        Assert.Contains($"codex-cli {PinnedVersion}", version.Stdout, StringComparison.Ordinal);

        var invocation = CodexProviderAuthInvocation.ResolveCurrent();
        var connection = new BridgeConnection(new Uri(bridge.BaseUrl).Port);
        var (config, _) = CodexConfigurator.BuildContent(null, connection, invocation);
        File.WriteAllText(Path.Combine(home.Path, "config.toml"), config);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var endpointBytes = await http.GetByteArrayAsync(
            $"{bridge.BaseUrl}/codex/models?client_version={PinnedVersion}");
        using var endpoint = JsonDocument.Parse(endpointBytes);

        var consumed = await RunCodexAsync(codexExe, home.Path, ["debug", "models"]);
        Assert.Equal(0, consumed.ExitCode);
        using var rendered = JsonDocument.Parse(consumed.Stdout);

        var endpointModels = BySlug(endpoint.RootElement);
        var renderedModels = BySlug(rendered.RootElement);
        Assert.NotEmpty(endpointModels);
        Assert.Equal(endpointModels.Count, renderedModels.Count);

        foreach (var (slug, expected) in endpointModels)
        {
            Assert.True(renderedModels.TryGetValue(slug, out var actual),
                $"real Codex omitted endpoint model '{slug}'");
            var expectedInstructions = expected.GetProperty("base_instructions").GetString();
            var actualInstructions = actual.GetProperty("base_instructions").GetString();
            Assert.False(string.IsNullOrWhiteSpace(expectedInstructions));
            Assert.Equal(expectedInstructions, actualInstructions);
            Assert.Equal(
                expected.GetProperty("context_window").GetInt32(),
                actual.GetProperty("context_window").GetInt32());
            Assert.Equal(
                expected.GetProperty("max_context_window").GetInt32(),
                actual.GetProperty("max_context_window").GetInt32());
        }

        var expectedLatest = endpointModels[ClientBehaviorSupport.LatestGpt];
        Assert.Equal(1_050_000, expectedLatest.GetProperty("context_window").GetInt32());
        Assert.Equal(892_000, expectedLatest.GetProperty("auto_compact_token_limit").GetInt32());

        var cachePath = Path.Combine(home.Path, "models_cache.json");
        Assert.True(File.Exists(cachePath), "real Codex did not persist the remote catalog");
        using var cache = JsonDocument.Parse(File.ReadAllText(cachePath));
        Assert.Equal(PinnedVersion, cache.RootElement.GetProperty("client_version").GetString());
        var cachedLatest = BySlug(cache.RootElement)[ClientBehaviorSupport.LatestGpt];
        Assert.Equal(1_050_000, cachedLatest.GetProperty("context_window").GetInt32());
        Assert.Equal(892_000, cachedLatest.GetProperty("auto_compact_token_limit").GetInt32());

        _output.WriteLine(
            $"Codex {PinnedVersion} consumed {renderedModels.Count} endpoint entries; " +
            "every base_instructions source and capacity field remained intact.");
    }

    private static Dictionary<string, JsonElement> BySlug(JsonElement envelope) =>
        envelope.GetProperty("models")
            .EnumerateArray()
            .ToDictionary(
                model => model.GetProperty("slug").GetString()!,
                model => model.Clone(),
                StringComparer.Ordinal);

    private static string CopilotSnapshotPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "docs",
                "copilot-codex-model-capabilities-snapshot.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Could not locate the committed Copilot model capability snapshot.");
    }

    private static string ResolvePinnedCodexExe()
    {
        var candidates = new List<string>();
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_0144_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(directory, "codex.exe"));

        var installedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        if (Directory.Exists(installedRoot))
            candidates.AddRange(Directory.EnumerateFiles(installedRoot, "codex.exe", SearchOption.AllDirectories));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            var version = RunVersion(candidate);
            if (string.Equals(version, $"codex-cli {PinnedVersion}", StringComparison.Ordinal))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            $"Could not locate the pinned Codex {PinnedVersion} consumer. " +
            "Set CODEX_0144_EXE to that codex.exe; a newer client is not schema evidence for 0.144.x.");
    }

    private static string? RunVersion(string executable)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(5_000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProcessResult> RunCodexAsync(
        string codexExe,
        string codexHome,
        IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = codexExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CODEX_HOME"] = codexHome;
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("real Codex catalog consumer did not exit within 30 seconds");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
