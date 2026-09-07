using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for the stock flagship compatibility route. Assertions name the
/// required source and target directly; the appsettings document is input, not
/// an implementation oracle.
/// </summary>
public sealed class Gpt6AstraStockRoutingTests
{
    [Theory]
    [InlineData("none", "low")]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    public void StockRoute_MapsFlagshipToAstraWithRequiredEffort(string inbound, string expected)
    {
        var ctx = Context("gpt-5.6-sol", inbound);
        var target = CountTokensTestServices.Planner(LoadStockRoutes()).Plan(ctx);

        Assert.Equal(BackendVendor.CopilotResponses, target.Vendor);
        Assert.Equal("/responses", target.Endpoint);
        Assert.Equal("gpt-6-astra", target.ModelId);
        Assert.Equal("gpt-6-astra", ctx.Request.Body.Model);
        Assert.Equal(expected, ctx.Request.Body.OutputConfig?.Effort);
    }

    [Theory]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol-fast")]
    [InlineData("gpt-5.6-terra")]
    public void StockRoute_DoesNotCollapseOtherGpt56Tiers(string model)
    {
        var target = CountTokensTestServices.Planner(LoadStockRoutes()).Plan(Context(model, "max"));

        Assert.Equal(model, target.ModelId);
    }

    private static RoutesConfig LoadStockRoutes()
    {
        var path = FindRepoFile("src", "CopilotBridge.Cli", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var routes = new RoutesConfig();
        config.GetSection("Routing").Bind(routes);
        return routes;
    }

    private static BridgeContext<MessagesRequest> Context(string model, string effort) => new()
    {
        Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/codex/responses",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Body = new MessagesRequest
            {
                Model = model,
                Messages = [],
                OutputConfig = new OutputConfig { Effort = effort },
            },
        },
        Response = new BridgeResponse(),
    };

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Could not locate repository appsettings.json.");
    }
}
