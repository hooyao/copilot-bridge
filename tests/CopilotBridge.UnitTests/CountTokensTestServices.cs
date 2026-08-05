using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CopilotBridge.UnitTests;

internal static class CountTokensTestServices
{
    internal static ModelRoutePlanner Planner(
        RoutesConfig? routes = null,
        ILoggerFactory? loggerFactory = null)
    {
        var logs = loggerFactory ?? NullLoggerFactory.Instance;
        return new ModelRoutePlanner(
            new CopilotModelRegistry(),
            new ModelProfileCatalog(),
            new CodexModelProfileCatalog(),
            Options.Create(routes ?? new RoutesConfig()),
            Options.Create(new OutboundBetaPolicyOptions()),
            logs.CreateLogger<ModelRoutePlanner>(),
            logs.CreateLogger<ModelRouteResolverLog>(),
            logs.CreateLogger<ProfileAdjusterLog>());
    }

    internal static ResponsesAdmissionEstimator Estimator(
        ILoggerFactory? loggerFactory = null) =>
        new((loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<ResponsesAdmissionEstimator>());

    internal static BridgeContext<MessagesRequest> Context() => new();

    internal static IOptions<CcToResponsesOptions> CcOptions() =>
        Options.Create(new CcToResponsesOptions());
}
