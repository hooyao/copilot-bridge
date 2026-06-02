using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins down <see cref="TracingOptions"/> binding from the <c>Tracing</c>
/// section of the configuration. The source-generated binder needs the POCO
/// to be public-shaped (public read-write properties) — this test catches a
/// regression that would silently fall back to defaults.
/// </summary>
public class TracingOptionsBindingTests
{
    private static TracingOptions ResolveOptions(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.Configure<TracingOptions>(config.GetSection("Tracing"));
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<TracingOptions>>()
            .Value;
    }

    private static IConfiguration ConfigFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Default_NoSection_DisabledWithFallbackDirectory()
    {
        var opts = ResolveOptions(ConfigFrom(new()));
        Assert.False(opts.Enabled);
        Assert.Equal("request-traces", opts.Directory);
    }

    [Fact]
    public void Bind_EnabledTrueAndCustomDirectory()
    {
        var opts = ResolveOptions(ConfigFrom(new()
        {
            ["Tracing:Enabled"] = "true",
            ["Tracing:Directory"] = "audit/today",
        }));
        Assert.True(opts.Enabled);
        Assert.Equal("audit/today", opts.Directory);
    }

    [Fact]
    public void Bind_OnlyEnabledFlag_KeepsDirectoryDefault()
    {
        var opts = ResolveOptions(ConfigFrom(new()
        {
            ["Tracing:Enabled"] = "true",
        }));
        Assert.True(opts.Enabled);
        Assert.Equal("request-traces", opts.Directory);
    }
}
