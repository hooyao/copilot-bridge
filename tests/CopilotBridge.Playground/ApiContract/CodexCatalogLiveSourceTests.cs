using System.Net;
using System.Net.Http.Headers;
using CopilotBridge.Cli.Catalogs.Codex;
using Xunit;

namespace CopilotBridge.Playground;

[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCatalogLiveSourceTests
{
    private const string PinnedVersion = "0.144.1";

    [Fact]
    public async Task OfficialExactTagSupportsBoundedConditionalRevalidation()
    {
        Assert.True(CodexClientVersion.TryParse(PinnedVersion, out var version));
        var expected =
            "https://raw.githubusercontent.com/openai/codex/rust-v0.144.1/codex-rs/models-manager/models.json";
        Assert.Equal(expected, CodexCatalogSource.BuildUri(version).AbsoluteUri);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge-contract-probe/1.0");

        using var initial = await http.GetAsync(expected, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.NotNull(initial.Headers.ETag);
        Assert.Equal("text/plain", initial.Content.Headers.ContentType?.MediaType);
        Assert.True(initial.Content.Headers.ContentLength is > 65_536 and < 4 * 1024 * 1024);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, expected);
        conditional.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(initial.Headers.ETag!.ToString()));
        using var revalidated = await http.SendAsync(conditional, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
    }
}
