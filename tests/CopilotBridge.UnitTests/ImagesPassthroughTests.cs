using System.Net;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.Images;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
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

        await ImagesEndpoint.HandleAsync(context, client, NullLogger<ImagesEndpointTag>.Instance, ImageOperation.Generate);

        Assert.Equal(ImageOperation.Generate, client.Operation);
        Assert.Equal(Encoding.UTF8.GetBytes(input), client.Body);
        Assert.Equal("application/json", client.ContentType);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes(output), ((MemoryStream)context.Response.Body).ToArray());
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
        public ImageOperation Operation { get; private set; }
        public byte[] Body { get; private set; } = [];
        public string? ContentType { get; private set; }
        public ValueTask<HttpResponseMessage> PostImageAsync(ImageOperation operation, ReadOnlyMemory<byte> body, string? contentType, CancellationToken ct = default)
        {
            Operation = operation; Body = body.ToArray(); ContentType = contentType;
            return ValueTask.FromResult(response);
        }
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
