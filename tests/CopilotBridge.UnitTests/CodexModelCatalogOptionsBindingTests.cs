using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexModelCatalogOptionsBindingTests
{
    [Fact]
    public void MissingSectionDefaultsDiscoveryOn()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.True(options.Enabled);
        Assert.Equal(300, options.LiveOverlayFailureCooldownSeconds);
    }

    [Fact]
    public void StockAppsettingsEnablesDiscovery()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        var options = Resolve(configuration);
        Assert.True(options.Enabled);
        Assert.Equal(300, options.LiveOverlayFailureCooldownSeconds);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void EnabledBindsFromCodexModelCatalog(string configured, bool expected)
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Codex:ModelCatalog:Enabled"] = configured,
        });

        Assert.Equal(expected, options.Enabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(3600)]
    public void LiveOverlayFailureCooldownBindsExactValidValue(int configured)
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Codex:ModelCatalog:LiveOverlayFailureCooldownSeconds"] = configured.ToString(),
        });

        Assert.Equal(configured, options.LiveOverlayFailureCooldownSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void LiveOverlayFailureCooldownOutsideRangeIsRejectedWithActionableKey(
        int configured)
    {
        var options = new CodexModelCatalogOptions
        {
            LiveOverlayFailureCooldownSeconds = configured,
        };

        var result = new CodexModelCatalogOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains(
                "Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds",
                StringComparison.Ordinal)
            && failure.Contains(configured.ToString(), StringComparison.Ordinal)
            && failure.Contains("1..3600", StringComparison.Ordinal));
    }

    private static CodexModelCatalogOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return Resolve(configuration);
    }

    private static CodexModelCatalogOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.Configure<CodexModelCatalogOptions>(configuration.GetSection("Codex:ModelCatalog"));
        return services.BuildServiceProvider().GetRequiredService<IOptions<CodexModelCatalogOptions>>().Value;
    }
}
