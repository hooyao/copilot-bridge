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
    }

    [Fact]
    public void StockAppsettingsEnablesDiscovery()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        Assert.True(Resolve(configuration).Enabled);
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
