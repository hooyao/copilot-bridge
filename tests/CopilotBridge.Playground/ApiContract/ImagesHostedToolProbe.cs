using System.Runtime.Versioning;
using System.Net;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Playground.Headless;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>Guard Copilot's hosted Responses image-tool rejection with explicit Flare.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class ImagesHostedToolProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task Codex_native_generation_and_edit_shapes_accept_flare()
    {
        // Captured from Codex 0.166.0-alpha.5.1's native image_gen tool. The
        // only mutation is the exact model id, which the bridge must route to
        // the Copilot image model instead of the client's OpenAI-only id.
        const string captured = """{"prompt":"Use case: logo-brand\nAsset type: simple raster graphic\nPrimary request: Create a single solid red circle centered on a pure white background.\nStyle/medium: flat, clean, minimal geometric graphic\nComposition/framing: perfectly circular red disk centered in a square canvas, with ample even white margin on all sides\nColor palette: vivid solid red circle (#FF0000 or visually equivalent) on pure white (#FFFFFF)\nConstraints: exactly one red circle; crisp smooth edge; uniform fill; no outline; no text; no watermark; no shadow; no gradient; no texture; no additional objects","background":"opaque","model":"gpt-image-2","quality":"auto","size":"auto"}""";
        using var parsed = JsonDocument.Parse(captured);
        Assert.Equal("gpt-image-2", parsed.RootElement.GetProperty("model").GetString());
        var adapted = captured.Replace("\"model\":\"gpt-image-2\"", "\"model\":\"gpt-image-2.5-flare\"", StringComparison.Ordinal);
        Assert.NotEqual(captured, adapted);
        using var source = await ImagesSdkTests.StageDirectCredentialAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var factory = new SingleClientHttpClientFactory(http);
        var store = new CredentialStore(source.Path,
            new WindowsDpapiTokenProtector("copilot-bridge.github_token.v1"u8.ToArray()));
        using var credentials = new CredentialService(factory, store, TimeProvider.System,
            NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(factory, credentials, TimeProvider.System, NullLoggerFactory.Instance, false);
        var lease = await auth.GetCopilotTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{lease.ApiBaseUrl}/v1/images/generations");
        new CopilotHeaderFactory().ApplyTo(request, lease.Token, integrationId: lease.IntegrationId);
        request.Content = new StringContent(adapted, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Flare native capture returned {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
        Assert.NotNull(data);
        Assert.StartsWith("iVBORw0KGgo", data);

        // Codex sends JSON images[].image_url rather than multipart. Reuse the
        // generated image and its captured native fields; Copilot must accept
        // this shape directly so the bridge need only rewrite the model id.
        var editJson = $$"""{"images":[{"image_url":"data:image/png;base64,{{data}}"}],"prompt":"Change the circle to blue.","background":"opaque","model":"gpt-image-2.5-flare","quality":"auto","size":"auto"}""";
        using var edit = new HttpRequestMessage(HttpMethod.Post, $"{lease.ApiBaseUrl}/v1/images/edits");
        new CopilotHeaderFactory().ApplyTo(edit, lease.Token, vision: true, integrationId: lease.IntegrationId);
        edit.Content = new StringContent(editJson, Encoding.UTF8, "application/json");
        using var edited = await http.SendAsync(edit);
        var editedBody = await edited.Content.ReadAsStringAsync();
        Assert.True(edited.IsSuccessStatusCode, $"Flare native edit returned {(int)edited.StatusCode}: {editedBody}");
        using var editedJson = JsonDocument.Parse(editedBody);
        var editedData = editedJson.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
        Assert.NotNull(editedData);
        Assert.StartsWith("iVBORw0KGgo", editedData);
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-6.1-sol")]
    public async Task Hosted_image_generation_with_flare_is_rejected_for_vscode_chat(string model)
    {
        using var source = await ImagesSdkTests.StageDirectCredentialAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var factory = new SingleClientHttpClientFactory(http);
        var store = new CredentialStore(source.Path,
            new WindowsDpapiTokenProtector("copilot-bridge.github_token.v1"u8.ToArray()));
        using var credentials = new CredentialService(factory, store, TimeProvider.System,
            NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(factory, credentials, TimeProvider.System, NullLoggerFactory.Instance, false);
        var lease = await auth.GetCopilotTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{lease.ApiBaseUrl}/responses");
        new CopilotHeaderFactory().ApplyTo(request, lease.Token, integrationId: lease.IntegrationId);
        var json = $$"""{"model":"{{model}}","input":"Generate a plain red circle on a white background.","tools":[{"type":"image_generation","model":"gpt-image-2.5-flare"}]}""";
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"{model} → HTTP {(int)response.StatusCode}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("unsupported_value", error.GetProperty("code").GetString());
        Assert.Equal("tools", error.GetProperty("param").GetString());
    }
}
