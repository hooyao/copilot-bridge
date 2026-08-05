using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexModelsEndpointContractTests
{
    private const string UnseenPrerelease = "0.147.0-alpha.1.2";
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SettingControlsOnlyTheCatalogRoute(bool enabled, bool expectedCatalogRoute)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IOptions<CodexModelCatalogOptions>>(
            Options.Create(new CodexModelCatalogOptions { Enabled = enabled }));
        await using var app = builder.Build();

        app.MapCodexModels();
        app.MapCodexResponses();

        var patterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Equal(expectedCatalogRoute, patterns.Contains("/codex/models", StringComparer.Ordinal));
        Assert.Contains("/codex/responses", patterns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?client_version=0.144.1&client_version=0.144.2")]
    [InlineData("?client_version=garbage")]
    [InlineData("?client_version=v0.145.0")]
    [InlineData("?client_version=0.145.0%2Fmain")]
    public async Task InvalidVersionQueryFailsSafely(string query)
    {
        var result = await Invoke(query, Response("gpt-5.4"));

        Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
        Assert.Null(result.ETag);
        using var doc = JsonDocument.Parse(result.Body);
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task SupportedRequestReturnsCodexEnvelopeEtagAndNoRawCopilotShape()
    {
        var result = await Invoke($"?client_version={UnseenPrerelease}", Response("gpt-5.4"));

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.Matches("^\"[0-9a-f]{64}\"$", result.ETag);
        using var doc = JsonDocument.Parse(result.Body);
        Assert.True(doc.RootElement.TryGetProperty("models", out var models));
        Assert.Equal(JsonValueKind.Array, models.ValueKind);
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
        var model = models.EnumerateArray().Single(item => item.GetProperty("slug").GetString() == "gpt-5.4");
        Assert.True(model.TryGetProperty("base_instructions", out _));
        Assert.False(model.TryGetProperty("capabilities", out _));
        Assert.False(model.TryGetProperty("supported_endpoints", out _));
        Assert.Equal(1_050_000, model.GetProperty("context_window").GetInt32());
        Assert.Equal(UnseenPrerelease, result.ResolvedVersion);
        Assert.DoesNotContain("github_pat", result.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WholeVersionQueryUsesCorroboratedCompleteCodexUserAgentIdentity()
    {
        var result = await Invoke(
            "?client_version=0.147.0",
            Response("gpt-5.4"),
            "Codex Desktop/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64)");

        Assert.Equal(StatusCodes.Status200OK, result.Status);
        Assert.Equal(UnseenPrerelease, result.ResolvedVersion);
    }

    [Fact]
    public async Task MismatchedCodexUserAgentFailsBeforeCatalogCacheIsCalled()
    {
        var result = await Invoke(
            "?client_version=0.147.0",
            Response("gpt-5.4"),
            "Codex Desktop/0.148.0-alpha.1 (Windows 10.0.26200; x86_64)");

        Assert.Equal(StatusCodes.Status400BadRequest, result.Status);
        Assert.Null(result.ResolvedVersion);
    }

    [Fact]
    public async Task SameFactsYieldSameEtagAndChangedFactsYieldDifferentEtag()
    {
        var one = await Invoke($"?client_version={UnseenPrerelease}", Response("gpt-5.4", 1_050_000, 922_000));
        var same = await Invoke($"?client_version={UnseenPrerelease}", Response("gpt-5.4", 1_050_000, 922_000));
        var changed = await Invoke($"?client_version={UnseenPrerelease}", Response("gpt-5.4", 1_000_000, 900_000));

        Assert.Equal(one.ETag, same.ETag);
        Assert.NotEqual(one.ETag, changed.ETag);
    }

    private static async Task<Result> Invoke(
        string query,
        CopilotModelsResponse response,
        string? userAgent = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/codex/models";
        http.Request.QueryString = new QueryString(query);
        if (userAgent is not null) http.Request.Headers.UserAgent = userAgent;
        http.Response.Body = new MemoryStream();

        var client = new FakeClient(response);
        var source = new StaticSourceCache();
        await CodexModelsEndpoint.HandleAsync(
            http,
            source,
            new CodexCatalogOverlayService(client, NullLogger<CodexCatalogOverlayService>.Instance),
            new CodexCatalogProjector(
                new CodexModelProfileCatalog(),
                new CopilotModelRegistry(),
                NullLogger<CodexCatalogProjector>.Instance));

        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        return new Result(
            http.Response.StatusCode,
            http.Response.Headers.ETag.ToString() is { Length: > 0 } etag ? etag : null,
            body,
            source.ResolvedVersion);
    }

    private static CopilotModelsResponse Response(string id, int total = 1_050_000, int prompt = 922_000) => new()
    {
        Data = [new CopilotModel
        {
            Id = id,
            SupportedEndpoints = ["/responses"],
            Capabilities = new CopilotModelCapabilities
            {
                Limits = new CopilotModelLimits
                {
                    MaxContextWindowTokens = total,
                    MaxPromptTokens = prompt,
                    MaxOutputTokens = 128_000,
                },
            },
        }],
    };

    private sealed record Result(int Status, string? ETag, string Body, string? ResolvedVersion);

    private sealed class StaticSourceCache : ICodexCatalogSourceCache
    {
        public string? ResolvedVersion { get; private set; }

        public ValueTask<CodexCatalogResolution> ResolveAsync(
            string? clientVersion,
            CancellationToken cancellationToken = default)
        {
            if (!CodexClientVersion.TryParse(clientVersion, out _))
                return ValueTask.FromResult(new CodexCatalogResolution
                    { Success = false, Outcome = "invalid", Error = "invalid version" });
            ResolvedVersion = clientVersion;
            return ValueTask.FromResult(new CodexCatalogResolution
            {
                Success = true,
                Baseline = CodexCatalogTestFixtures.LoadCapturedBaseline(clientVersion!),
                Outcome = "test",
            });
        }
    }

    private sealed class FakeClient(CopilotModelsResponse response) : ICopilotClient
    {
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) => ValueTask.FromResult(response);
        public ValueTask<HttpResponseMessage> PostMessagesAsync(ReadOnlyMemory<byte> body, bool vision = false, IReadOnlyList<string>? anthropicBeta = null, IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostCountTokensAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostResponsesAsync(ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
