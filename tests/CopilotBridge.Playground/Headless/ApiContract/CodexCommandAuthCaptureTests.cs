using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Models;
using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground.Headless;

[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCommandAuthCaptureTests
{
    [Fact]
    public async Task Real_Codex_sends_only_public_sentinel_to_models_and_responses()
    {
        var catalog = CodexCatalogTestFixtures.Load();
        var catalogJson = JsonSerializer.Serialize(
            new CopilotBridge.Cli.Models.Codex.CodexModelsResponse { Models = catalog.Models },
            JsonContext.Default.CodexModelsResponse);
        await using var server = new CodexCommandAuthCaptureServer(catalogJson);
        using var home = ClientBehaviorSupport.NewWorkDir("codex-command-auth-capture");
        using var work = ClientBehaviorSupport.NewWorkDir("codex-command-auth-work");

        var invocation = CodexProviderAuthInvocation.ResolveCurrent();
        var config = $"""
            model = "gpt-5.6-sol"
            model_provider = "capture"

            [model_providers.capture]
            name = "capture"
            base_url = "{server.BaseUrl}"
            wire_api = "responses"

            [model_providers.capture.auth]
            command = "{Toml(invocation.Command)}"
            args = [ {string.Join(", ", invocation.Args.Select(arg => $"\"{Toml(arg)}\""))} ]
            timeout_ms = 5000
            refresh_interval_ms = 0
            """;
        File.WriteAllText(Path.Combine(home.Path, "config.toml"), config);

        var start = new ProcessStartInfo
        {
            FileName = CodexProcess.ResolveCodexExe("0.147.0-alpha.1.2"),
            WorkingDirectory = work.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CODEX_HOME"] = home.Path;
        foreach (var arg in new[]
        {
            "exec", "--json", "--skip-git-repo-check", "-m", "gpt-5.6-sol",
            "--dangerously-bypass-approvals-and-sandbox", "Reply with exactly sentinel-ok and do not use tools."
        }) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start Codex");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "Codex command-auth capture timed out");
        Assert.Equal(0, process.ExitCode);
        var clientOutput = await stdout;
        _ = await stderr; // informational startup warnings do not affect the header contract
        Assert.Contains("\"type\":\"turn.completed\"", clientOutput, StringComparison.Ordinal);

        var requests = server.Requests.ToArray();
        Assert.Contains(requests, request => request.Path == "/models");
        Assert.Contains(requests, request => request.Path == "/responses");
        var modelsRequests = requests.Where(request => request.Path == "/models").ToArray();
        Assert.NotEmpty(modelsRequests);
        Assert.All(modelsRequests, request =>
        {
            Assert.Contains("client_version=0.147.0", request.RawUrl, StringComparison.Ordinal);
            Assert.Contains("/0.147.0-alpha.1.2 ", request.UserAgent, StringComparison.Ordinal);
        });
        Assert.All(requests.Where(request => request.Path is "/models" or "/responses"),
            request => Assert.Equal($"Bearer {AuthCommand.ProviderSentinel}", request.Authorization));
    }

    private static string Toml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
