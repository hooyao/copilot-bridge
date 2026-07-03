using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// DI-composition contract for the scoped detector framework. Guards the textbook
/// scoped-DI wiring the refactor established: detectors are scoped services
/// resolved as an ordered set into a scoped <see cref="ResponseInspectionStage"/>,
/// carried by a scoped <see cref="Pipeline{TBody}"/>. Protects against a future
/// captive-dependency regression (e.g. making the pipeline a singleton again).
/// </summary>
public class DetectorCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeServer(config, cliPort: 12345, deviceCodePrinter: null);
        // ValidateScopes surfaces captive dependencies (a singleton capturing a
        // scoped service) the moment the offending service is resolved.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Detectors_ResolveAsScopedSet_InRegistrationOrder()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        var detectors = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();

        // Registration order = precedence order: DONE-filter → model-rewrite → tool-leak.
        Assert.Equal(3, detectors.Length);
        Assert.Equal("DoneFilter", detectors[0].Name);
        Assert.Equal("ModelRewrite", detectors[1].Name);
        Assert.Equal("ToolLeak", detectors[2].Name);
    }

    [Fact]
    public void Detectors_AreScoped_FreshPerScope()
    {
        using var sp = BuildProvider();

        IResponseDetector[] first, second, sameScopeAgain;
        using (var scope = sp.CreateScope())
        {
            first = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();
            sameScopeAgain = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();
        }
        using (var scope = sp.CreateScope())
        {
            second = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();
        }

        // Same instances within a scope (so streaming state is coherent for one
        // request), fresh instances across scopes (so state never crosses requests).
        Assert.Same(first[2], sameScopeAgain[2]);
        Assert.NotSame(first[2], second[2]);
    }

    [Fact]
    public void Pipeline_IsScoped_ResolvesWithinScope_NotFromRoot()
    {
        using var sp = BuildProvider();

        using (var scope = sp.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<Pipeline<MessagesRequest>>();
            Assert.NotNull(pipeline);
            Assert.Single(pipeline.ResponseStages);
        }

        // Scoped services must not be served from the root provider when scope
        // validation is on — proves the pipeline is genuinely request-scoped.
        Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<Pipeline<MessagesRequest>>());
    }
}
