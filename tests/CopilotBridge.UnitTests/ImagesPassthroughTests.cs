using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.Images;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public class ImagesPassthroughTests
{
    [Fact]
    public async Task Images_routes_accept_post_for_standard_and_codex_base_urls()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.MapImages();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        Assert.Equal(new[] { "/codex/images/edits", "/codex/images/generations", "/v1/images/edits", "/v1/images/generations" },
            routes.Select(r => r.RoutePattern.RawText).Order(StringComparer.Ordinal).ToArray());
        Assert.All(routes, route => Assert.Equal(["POST"], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods));
    }

    // Contract: a multipart upload including binary bytes and its exact boundary
    // reach the upstream unchanged, and an upstream error remains observable.
    [Fact]
    public async Task Edit_relay_preserves_multipart_bytes_content_type_and_upstream_error()
    {
        byte[] body = [45, 45, 66, 13, 10, 0, 255, 13, 10, 45, 45, 66, 45, 45];
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentType = "multipart/form-data; boundary=B";
        context.Response.Body = new MemoryStream();
        using var upstream = new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":\"invalid_image\"}", Encoding.UTF8, "application/json") };
        upstream.Headers.TryAddWithoutValidation("x-request-id", "req-image");
        upstream.Headers.TryAddWithoutValidation("Connection", "x-hop");
        upstream.Headers.TryAddWithoutValidation("x-hop", "hidden");
        var client = new FakeImagesClient(upstream);

        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Edit);

        Assert.Equal(ImageOperation.Edit, client.Operation);
        Assert.Equal(body, client.Body);
        Assert.Equal("multipart/form-data; boundary=B", client.ContentType);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("req-image", context.Response.Headers["x-request-id"].ToString());
        Assert.False(context.Response.Headers.ContainsKey("x-hop"));
        Assert.Equal("{\"error\":\"invalid_image\"}", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task Generation_relay_preserves_unknown_request_fields_and_image_response()
    {
        const string input = " {\"model\":\"future-image-model\",\"prompt\":\"circle\",\"future\":true} ";
        const string output = "{\"data\":[{\"b64_json\":\"iVBORw0KGgo=\"}],\"future\":7}\n";
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(input));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(output, Encoding.UTF8, "application/json") };
        var client = new FakeImagesClient(upstream);

        var routes = new RoutesConfig { ImageModelAliases = [new ImageModelAlias { From = "gpt-image-2", To = "gpt-image-2.5-flare" }] };
        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Generate, routes);

        Assert.Equal(ImageOperation.Generate, client.Operation);
        Assert.Equal(Encoding.UTF8.GetBytes(input), client.Body);
        Assert.Equal("application/json", client.ContentType);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes(output), ((MemoryStream)context.Response.Body).ToArray());
    }

    // Contract: Codex's native image-model alias routes to Flare on the Codex
    // image endpoint, with every other byte unchanged. The generic API is opaque.
    [Theory]
    [InlineData("/codex/images/generations", "gpt-image-2.5-flare")]
    [InlineData("/codex/images/edits", "gpt-image-2.5-flare")]
    [InlineData("/v1/images/generations", "gpt-image-2")]
    public async Task Codex_native_model_alias_is_mapped_only_on_codex_routes(string path, string expectedModel)
    {
        const string input = " {\"nested\":{\"model\":\"gpt-image-2\"}, \"model\":\"gpt-image-2\", \"prompt\":\"keep gpt-image-2\", \"future\":true} ";
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(input));
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Response.Body = new MemoryStream();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        var client = new FakeImagesClient(upstream);

        var routes = new RoutesConfig { ImageModelAliases = [new ImageModelAlias { From = "gpt-image-2", To = "gpt-image-2.5-flare" }] };
        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Generate, routes);

        var expected = input.Replace("\"model\":\"gpt-image-2\", \"prompt\"", $"\"model\":\"{expectedModel}\", \"prompt\"", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), client.Body);
    }

    [Fact]
    public async Task Duplicate_top_level_model_fields_are_not_rewritten()
    {
        const string input = "{\"model\":\"gpt-image-2\",\"model\":\"another-model\"}";
        var context = new DefaultHttpContext();
        context.Request.Path = "/codex/images/generations";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(input));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        var client = new FakeImagesClient(upstream);
        var routes = new RoutesConfig { ImageModelAliases = [new ImageModelAlias { From = "gpt-image-2", To = "gpt-image-2.5-flare" }] };
        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Generate, routes);
        Assert.Equal(Encoding.UTF8.GetBytes(input), client.Body);
    }

    [Fact]
    public void Shipped_routing_config_contains_the_codex_image_alias()
    {
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false).Build();
        var services = new ServiceCollection();
        services.Configure<RoutesConfig>(config.GetSection("Routing"));
        using var provider = services.BuildServiceProvider();
        var aliases = provider.GetRequiredService<IOptions<RoutesConfig>>().Value.ImageModelAliases;
        var alias = Assert.Single(aliases);
        Assert.Equal("gpt-image-2", alias.From);
        Assert.Equal("gpt-image-2.5-flare", alias.To);
    }

    [Fact]
    public void Image_alias_uses_operator_target_and_empty_list_disables_rewrite()
    {
        ReadOnlyMemory<byte> input = "{\"model\":\"gpt-image-2\",\"prompt\":\"unchanged\"}"u8.ToArray();
        var mapped = CodexImageModelAlias.Rewrite(input, [new ImageModelAlias { From = "gpt-image-2", To = "custom-model" }]);
        Assert.Equal("{\"model\":\"custom-model\",\"prompt\":\"unchanged\"}", Encoding.UTF8.GetString(mapped!));
        Assert.Null(CodexImageModelAlias.Rewrite(input, []));
    }

    [Fact]
    public void Configured_model_id_is_json_escaped()
    {
        ReadOnlyMemory<byte> input = "{\"model\":\"gpt-image-2\"}"u8.ToArray();
        var result = CodexImageModelAlias.Rewrite(input, [new ImageModelAlias { From = "gpt-image-2", To = "custom\"model" }]);
        using var json = System.Text.Json.JsonDocument.Parse(result!);
        Assert.Equal("custom\"model", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Empty_or_duplicate_image_aliases_are_rejected_at_startup()
    {
        var empty = new RoutesConfig { ImageModelAliases = [new ImageModelAlias { From = "gpt-image-2", To = "" }] };
        var duplicates = new RoutesConfig { ImageModelAliases = [
            new ImageModelAlias { From = "gpt-image-2", To = "one" },
            new ImageModelAlias { From = "gpt-image-2", To = "two" },
        ] };
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(empty));
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(duplicates));
    }

    // Contract: an upload rejected by the server's request-size limit must be
    // reported as a client error and never be sent to Copilot.
    [Fact]
    public async Task Oversized_multipart_upload_returns_413_without_calling_upstream()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new OversizedUploadStream();
        context.Request.ContentType = "multipart/form-data; boundary=B";
        context.Response.Body = new MemoryStream();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unexpected") };
        var client = new FakeImagesClient(upstream);

        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Edit);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal(0, client.Calls);
        Assert.Contains("invalid_request_error", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Theory]
    [InlineData(false, "/v1/images/generations", "application/json", false)]
    [InlineData(true, "/v1/images/edits", "multipart/form-data; boundary=example", true)]
    public async Task Authenticated_transport_forwards_bytes_to_the_corresponding_copilot_path(
        bool edit, string path, string contentType, bool vision)
    {
        byte[] body = [0, 1, 2, 255, 13, 10];
        var handler = new CaptureHandler();
        var client = NewClient(handler);
        using var response = await client.PostImageAsync(edit ? ImageOperation.Edit : ImageOperation.Generate, body, contentType);
        Assert.Equal("https://copilot.example" + path, handler.Uri);
        Assert.Equal(body, handler.Bodies.Single());
        Assert.Equal(contentType, handler.ContentType);
        Assert.Equal("Bearer lease-token", handler.Authorization);
        Assert.Equal("copilot-developer-cli", handler.Integration);
        Assert.Equal(vision, handler.Vision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authentication_replay_resends_identical_multipart_bytes()
    {
        byte[] body = [1, 255, 0, 13, 10];
        var handler = new CaptureHandler(rejectFirst: true);
        var client = NewClient(handler);
        using var response = await client.PostImageAsync(ImageOperation.Edit, body, "multipart/form-data; boundary=B");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, captured => Assert.Equal(body, captured));
        Assert.Equal("multipart/form-data; boundary=B", handler.ContentType);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Failed_authentication_recovery_preserves_upstream_rejection(HttpStatusCode status)
    {
        var handler = new CaptureHandler(rejectStatus: status);
        var client = NewClient(handler, new FixedAuth(failRecovery: true));
        using var response = await client.PostImageAsync(ImageOperation.Generate, "{}"u8.ToArray(), "application/json");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Transient_send_failure_is_not_retried_to_avoid_duplicate_generation()
    {
        var handler = new CaptureHandler(fail: true);
        var client = NewClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.PostImageAsync(ImageOperation.Generate, "{}"u8.ToArray(), "application/json"));
        Assert.Single(handler.Bodies);
    }

    private static ImagesClient NewClient(HttpMessageHandler handler, IAuthService? auth = null) => new(
        new SingleClientHttpClientFactory(new HttpClient(handler)), auth ?? new FixedAuth(), new CopilotHeaderFactory(),
        Options.Create(new UpstreamTimeoutOptions { FirstByteTimeoutSeconds = 0 }),
        NullLogger<ImagesClient>.Instance);

    private sealed class FakeImagesClient(HttpResponseMessage response) : IImagesClient
    {
        public int Calls { get; private set; }
        public ImageOperation Operation { get; private set; }
        public byte[] Body { get; private set; } = [];
        public string? ContentType { get; private set; }
        public ValueTask<HttpResponseMessage> PostImageAsync(ImageOperation operation, ReadOnlyMemory<byte> body, string? contentType, CancellationToken ct = default)
        {
            Calls++; Operation = operation; Body = body.ToArray(); ContentType = contentType;
            return ValueTask.FromResult(response);
        }
    }

    private sealed class OversizedUploadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new BadHttpRequestException("Request body too large", 413);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new BadHttpRequestException("Request body too large", 413));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CaptureHandler(bool rejectFirst = false, bool fail = false, HttpStatusCode? rejectStatus = null) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }
        public string? Integration { get; private set; }
        public bool Vision { get; private set; }
        public List<byte[]> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            ContentType = request.Content.Headers.ContentType?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Integration = request.Headers.GetValues("Copilot-Integration-Id").Single();
            Vision = request.Headers.TryGetValues("Copilot-Vision-Request", out var values) && values.Single() == "true";
            if (fail) throw new HttpRequestException("transport failure");
            return new HttpResponseMessage(rejectStatus ?? (rejectFirst && Bodies.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK))
            { Content = new StringContent("{}") };
        }
    }

    private sealed class FixedAuth(bool failRecovery = false) : IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "unused";
        public string? CopilotApiBaseUrl => "https://copilot.example";
        public DateTimeOffset? CopilotTokenExpiry => null;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void SignOut() => throw new NotSupportedException();
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(CopilotLeaseRejection? rejection = null, CancellationToken ct = default)
        {
            if (rejection is not null && failRecovery) throw new InvalidOperationException("recovery unavailable");
            return ValueTask.FromResult(new CopilotAuthLease
            {
                Token = "lease-token", ApiBaseUrl = "https://copilot.example", IntegrationId = "copilot-developer-cli",
                RefreshAt = DateTimeOffset.MaxValue, ServerExpiresAt = DateTimeOffset.MaxValue, Generation = 1,
            });
        }
    }
}
