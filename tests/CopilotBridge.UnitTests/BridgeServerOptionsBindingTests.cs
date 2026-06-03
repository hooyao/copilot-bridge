using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
