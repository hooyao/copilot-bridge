using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins down OutboundBetaPolicyOptions binding from the
/// <c>Pipeline:OutboundBeta</c> section — the operator-tunable list of
/// global <c>anthropic-beta</c> token strip patterns ProfileAdjuster
/// applies to every outbound request.
/// </summary>
public class OutboundBetaPolicyOptionsBindingTests
{
    private static OutboundBetaPolicyOptions ResolveOptions(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.Configure<OutboundBetaPolicyOptions>(config.GetSection("Pipeline:OutboundBeta"));
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<OutboundBetaPolicyOptions>>()
            .Value;
    }

    private static IConfiguration ConfigFrom(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Default_NoSection_ReturnsEmptyList()
    {
        var opts = ResolveOptions(ConfigFrom(new()));
        Assert.Empty(opts.GlobalStrip);
    }

    [Fact]
    public void Bind_GlobalStripArray_RoundTrips()
    {
        var opts = ResolveOptions(ConfigFrom(new()
        {
            ["Pipeline:OutboundBeta:GlobalStrip:0"] = "advisor-tool-*",
            ["Pipeline:OutboundBeta:GlobalStrip:1"] = "structured-outputs-*",
        }));

        Assert.Equal(2, opts.GlobalStrip.Count);
        Assert.Contains("advisor-tool-*", opts.GlobalStrip);
        Assert.Contains("structured-outputs-*", opts.GlobalStrip);
    }

    [Fact]
    public void Bind_EmptyArray_ReturnsEmpty()
    {
        // Operator deliberately clears the list (e.g. Bedrock-only tenant
        // who wants structured_outputs back). Bound to an empty collection,
        // not a defaulted one.
        var opts = ResolveOptions(ConfigFrom(new()
        {
            ["Pipeline:OutboundBeta:GlobalStrip"] = null,
        }));

        Assert.Empty(opts.GlobalStrip);
    }
}
