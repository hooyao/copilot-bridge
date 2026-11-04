using System.Net;
using System.Net.Http.Headers;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.Images;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotBridge.Playground.Headless;

[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class ImagesRequestLimitTests
{
    [Fact]
    public async Task Kestrel_rejects_oversized_multipart_without_calling_upstream()
    {
        var client = new FailOnCallClient();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 256;
        });
        builder.Services.AddSingleton<IImagesClient>(client);
        await using var app = builder.Build();
        app.MapImages();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient();
        using var body = new MultipartFormDataContent();
        var image = new ByteArrayContent(new byte[1024]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        body.Add(image, "image", "oversized.png");
        using var response = await http.PostAsync(address + "/v1/images/edits", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, client.Calls);
    }

    private sealed class FailOnCallClient : IImagesClient
    {
        public int Calls { get; private set; }
        public ValueTask<HttpResponseMessage> PostImageAsync(ImageOperation operation, ReadOnlyMemory<byte> body, string? contentType, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("The rejected upload must not reach Copilot.");
        }
    }
}
