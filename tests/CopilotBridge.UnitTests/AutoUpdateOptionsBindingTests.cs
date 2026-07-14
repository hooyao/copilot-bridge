using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins the code-default behavior of <see cref="AutoUpdateOptions"/> from the
/// "Serve-only startup update gate" requirement: an installation whose
/// <c>appsettings.json</c> predates the <c>AutoUpdate</c> section must still
/// default to enabled, stable-only checking from the POCO defaults alone.
/// </summary>
public class AutoUpdateOptionsBindingTests
{
    private static AutoUpdateOptions Resolve(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.Configure<AutoUpdateOptions>(config.GetSection("AutoUpdate"));
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<AutoUpdateOptions>>()
            .Value;
    }

    private static IConfiguration ConfigFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Absent_section_defaults_to_enabled_stable_only()
    {
        var opts = Resolve(ConfigFrom(new()));
        Assert.True(opts.EnableAutoUpdate);
        Assert.False(opts.AllowBetaUpdates);
    }

    [Fact]
    public void Absent_properties_within_present_section_keep_defaults()
    {
        // Section exists but neither property is set — still enabled, stable-only.
        var opts = Resolve(ConfigFrom(new() { ["AutoUpdate:_note"] = "x" }));
        Assert.True(opts.EnableAutoUpdate);
        Assert.False(opts.AllowBetaUpdates);
    }

    [Fact]
    public void Explicit_disable_is_honored()
    {
        var opts = Resolve(ConfigFrom(new() { ["AutoUpdate:EnableAutoUpdate"] = "false" }));
        Assert.False(opts.EnableAutoUpdate);
    }

    [Fact]
    public void Explicit_beta_opt_in_is_honored()
    {
        var opts = Resolve(ConfigFrom(new() { ["AutoUpdate:AllowBetaUpdates"] = "true" }));
        Assert.True(opts.AllowBetaUpdates);
    }
}
