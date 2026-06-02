using System.Text;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Proves the routing POCOs bind from JSON config the way production does
/// (<c>builder.Configuration.GetSection("Routing")</c>) — in particular that a
/// nested <c>AllOf</c>/<c>AnyOf</c> tree round-trips to non-null lists.
/// <para><b>Scope:</b> this runs under the JIT reflection binder, so it proves
/// the POCO <i>shape</i> + JSON path are correct. It does NOT prove the AOT
/// source-generated binder behaves identically — that's verified by starting
/// the published exe and checking the location count (see the AOT smoke step in
/// the milestone notes). A green test here + a successful AOT startup together
/// cover both binders.</para>
/// </summary>
public class RoutesBindingTests
{
    private static RoutesConfig Bind(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var routes = new RoutesConfig();
        config.GetSection("Routing").Bind(routes);
        return routes;
    }

    [Fact]
    public void NestedAllOfAnyOf_BindsToNonNullLists_AndEvaluates()
    {
        var routes = Bind("""
        {
          "Routing": {
            "Locations": [
              {
                "When": {
                  "AllOf": [
                    { "AnyOf": [ { "Model": "claude-opus-4.7" }, { "Model": "claude-opus-4.8" } ] },
                    { "Header": { "Name": "anthropic-beta", "Contains": "context-1m-2025-08-07" } }
                  ]
                },
                "Use": { "Model": "claude-opus-4.7-1m-internal", "EffortMap": { "max": "xhigh" } }
              }
            ]
          }
        }
        """);

        Assert.Single(routes.Locations);
        var when = routes.Locations[0].When;

        // The recursive composites actually bound (this is the thing that could
        // silently come back null and turn into a match-all).
        Assert.NotNull(when.AllOf);
        Assert.Equal(2, when.AllOf!.Count);
        Assert.NotNull(when.AllOf[0].AnyOf);
        Assert.Equal(2, when.AllOf[0].AnyOf!.Count);
        Assert.Equal("claude-opus-4.7", when.AllOf[0].AnyOf![0].Model);
        Assert.Equal("anthropic-beta", when.AllOf[1].Header!.Name);

        // And the bound tree evaluates correctly.
        Assert.True(when.Matches(TestCtx.Build("claude-opus-4.8", betas: ["context-1m-2025-08-07"])));
        Assert.False(when.Matches(TestCtx.Build("claude-opus-4.6", betas: ["context-1m-2025-08-07"])));

        // Use bound too (Model + EffortMap dictionary).
        Assert.Equal("claude-opus-4.7-1m-internal", routes.Locations[0].Use.Model);
        Assert.Equal("xhigh", routes.Locations[0].Use.EffortMap!["max"]);
    }

    [Fact]
    public void HeaderSetRemove_Bind()
    {
        var routes = Bind("""
        {
          "Routing": {
            "Locations": [
              {
                "When": { "Model": "claude-opus-4.8" },
                "Use": {
                  "Headers": {
                    "Set": { "Editor-Version": "vscode/2.0.0" },
                    "Remove": [ "anthropic-beta:context-1m-*" ]
                  }
                }
              }
            ]
          }
        }
        """);

        var use = routes.Locations[0].Use;
        Assert.Equal("vscode/2.0.0", use.Headers!.Set!["Editor-Version"]);
        Assert.Equal("anthropic-beta:context-1m-*", Assert.Single(use.Headers.Remove!));
    }

    [Fact]
    public void EmptyRouting_BindsToEmptyList()
    {
        var routes = Bind("""{ "Routing": { "Locations": [] } }""");
        Assert.Empty(routes.Locations);
    }
}
