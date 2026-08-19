using System.Net;
using System.Net.Sockets;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins down the (CLI &gt; appsettings &gt; built-in default) precedence chain
/// for the new <see cref="BridgeServerOptions"/>. Pure JIT test — the
/// source-generated binder + the JIT reflection binder both have to honor
/// PostConfigure, so a green run here covers both paths.
/// </summary>
public class BridgeServerOptionsBindingTests
{
    private static BridgeServerOptions ResolveOptions(
        IConfiguration config, int? cliOverride = null)
    {
        var services = new ServiceCollection();
        services.Configure<BridgeServerOptions>(config.GetSection("Server"));
        if (cliOverride.HasValue)
        {
            var port = cliOverride.Value;
            services.PostConfigure<BridgeServerOptions>(o => o.Port = port);
        }
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<BridgeServerOptions>>()
            .Value;
    }

    private static IConfiguration ConfigFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Default_NoSection_NoCli_ReturnsBuiltInDefault8765()
    {
        var opts = ResolveOptions(ConfigFrom(new()));
        Assert.Equal(8765, opts.Port);
        Assert.Equal(104_857_600, opts.MaxRequestBodySizeBytes);
    }

    [Fact]
    public void Appsettings_MaxRequestBodySizeBytes_OverridesDefault()
    {
        var opts = ResolveOptions(ConfigFrom(new()
        {
            ["Server:MaxRequestBodySizeBytes"] = "125829120",
        }));

        Assert.Equal(125_829_120, opts.MaxRequestBodySizeBytes);
    }

    [Fact]
    public void Appsettings_PortValue_OverridesDefault()
    {
        var opts = ResolveOptions(ConfigFrom(new() { ["Server:Port"] = "9000" }));
        Assert.Equal(9000, opts.Port);
    }

    [Fact]
    public void CliOverride_BeatsAppsettings()
    {
        var opts = ResolveOptions(
            ConfigFrom(new() { ["Server:Port"] = "9000" }),
            cliOverride: 12345);
        Assert.Equal(12345, opts.Port);
    }

    [Fact]
    public void CliOverride_BeatsDefault_WhenNoAppsettings()
    {
        var opts = ResolveOptions(ConfigFrom(new()), cliOverride: 12345);
        Assert.Equal(12345, opts.Port);
    }

    [Fact]
    public void ShippedAppsettings_ExposesSameRequestBodyDefaultAsOptions()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var opts = ResolveOptions(config);

        Assert.Equal("104857600", config["Server:MaxRequestBodySizeBytes"]);
        Assert.Equal(104_857_600, opts.MaxRequestBodySizeBytes);
    }

    [Fact]
    public void KestrelConfiguration_AppliesBoundRequestBodyLimit()
    {
        var options = ConfigureKestrel(new BridgeServerOptions
        {
            MaxRequestBodySizeBytes = 125_829_120,
        });

        Assert.Equal(125_829_120, options.Limits.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void KestrelConfiguration_NonPositiveRequestBodyLimit_FailsStartup(long value)
    {
        var ex = Assert.Throws<BridgeStartupException>(() =>
            ConfigureKestrel(new BridgeServerOptions { MaxRequestBodySizeBytes = value }));

        Assert.Contains(nameof(BridgeServerOptions.MaxRequestBodySizeBytes), ex.Message);
        Assert.Contains("greater than zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultKestrelLimit_RequestAboveFormerThirtyMillionByteLimit_ReachesEndpoint()
    {
        const long bodySize = 30_000_001;
        var port = ReserveLoopbackPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            new KestrelOptionsConfigurator(Options.Create(new BridgeServerOptions { Port = port }))
                .Configure(options));

        await using var app = builder.Build();
        var endpointReadBody = false;
        app.MapPost("/", async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            endpointReadBody = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        await app.StartAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var content = new StreamContent(new FixedLengthZeroStream(bodySize));
            content.Headers.ContentLength = bodySize;

            using var response = await client.PostAsync($"http://127.0.0.1:{port}/", content);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(endpointReadBody);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static KestrelServerOptions ConfigureKestrel(BridgeServerOptions server)
    {
        var options = new KestrelServerOptions();
        new KestrelOptionsConfigurator(Options.Create(server)).Configure(options);
        return options;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FixedLengthZeroStream : Stream
    {
        private readonly long _length;
        private long _remaining;

        public FixedLengthZeroStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            buffer[..count].Clear();
            _remaining -= count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
