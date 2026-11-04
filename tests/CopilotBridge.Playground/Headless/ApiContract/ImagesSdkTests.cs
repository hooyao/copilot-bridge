using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Xunit;

namespace CopilotBridge.Playground.Headless;

/// <summary>Exercises the real Images client against a bridge subprocess and live Copilot.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class ImagesSdkTests
{
    [Fact]
    public async Task Openai_sdk_generates_and_edits_decodable_images()
    {
        var source = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_DIRECT_CREDENTIAL_SOURCE_DIRECTORY");
        using var staged = source is null ? await StageDirectCredentialAsync() : null;
        source ??= staged!.Path;
        var python = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_OPENAI_PYTHON")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_OPENAI_PYTHON to a Python with OpenAI and Pillow.");
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CredentialSourceDirectory: source,
            CredentialStagingMode: CredentialStagingMode.PurposeBuiltDirectVersionTwo,
            BridgeBinaryDirectory: Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_NATIVE_BINARY_DIRECTORY")));

        const string script = """
            import base64, io, sys
            from openai import OpenAI
            from PIL import Image

            def decode(response):
                assert response.data and response.data[0].b64_json
                data = base64.b64decode(response.data[0].b64_json, validate=True)
                with Image.open(io.BytesIO(data)) as image:
                    image.load()
                    assert image.format == 'PNG' and image.width > 0 and image.height > 0
                return data

            client = OpenAI(base_url=sys.argv[1] + '/v1', api_key='local-test', timeout=240, max_retries=0)
            generated = client.images.generate(model='gpt-image-2.5-flare', prompt='A simple red circle on a plain white background.', size='1024x1024')
            data = decode(generated)
            print('CLIENT GENERATED: decoded PNG', flush=True)
            client = OpenAI(base_url=sys.argv[1] + '/codex', api_key='local-test', timeout=240, max_retries=0)
            edited = client.images.edit(model='gpt-image-2.5-flare', prompt='Change the red circle to blue.', image=('input.png', data, 'image/png'))
            decode(edited)
            print('CLIENT EDITED: decoded PNG', flush=True)
            """;
        var start = new ProcessStartInfo
        {
            FileName = python, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(bridge.BaseUrl);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the SDK client.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"OpenAI Images client failed: {error}\n{output}");
        Assert.Contains("CLIENT GENERATED: decoded PNG", output);
        Assert.Contains("CLIENT EDITED: decoded PNG", output);
    }

    private static async Task<ClientBehaviorSupport.ScratchDir> StageDirectCredentialAsync()
    {
        var start = new ProcessStartInfo("gh", "auth token --hostname github.com --no-refresh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start gh.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var token = (await stdout).Trim();
        var error = await stderr;
        if (process.ExitCode != 0 || !token.StartsWith("gho_", StringComparison.Ordinal))
            throw new InvalidOperationException($"A GitHub CLI OAuth token is required: {error.Trim()}");
        var directory = ClientBehaviorSupport.NewWorkDir("images-direct-auth");
        try
        {
            var record = new CredentialFileRecord
            {
                Version = CredentialFileRecord.GitHubCliOAuthVersion,
                AccessToken = token,
                TokenType = "bearer",
                CredentialId = Guid.NewGuid().ToString("N"),
                Generation = 1,
            };
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonContext.Default.CredentialFileRecord);
            try
            {
                var encrypted = ProtectedData.Protect(plaintext,
                    Encoding.UTF8.GetBytes("copilot-bridge.github_token.v1"), DataProtectionScope.CurrentUser);
                File.WriteAllBytes(Path.Combine(directory.Path, "github_credentials.dat"), encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }
}
