using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerilogGlobalLoggerCollection
{
    public const string Name = "Process-global Serilog logger";
}

/// <summary>
/// Contract: the production logger must replace the bootstrap logger before DI
/// constructs later hosted services. Otherwise their injected MEL loggers keep
/// targeting the disposed bootstrap instance and startup/auth events disappear.
/// </summary>
[Collection(SerilogGlobalLoggerCollection.Name)]
public sealed class SerilogHostedServiceOrderingTests
{
    [Fact]
    public async Task Hosted_service_start_event_reaches_full_rolling_log()
    {
        var canary = $"hosted-start-{Guid.NewGuid():N}";
        var originalLogger = Log.Logger;
        Log.Logger = SerilogBootstrapper.BuildBootstrap();

        try
        {
            var services = new ServiceCollection();
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SerilogLoggerProvider(dispose: false));
            });
            services.AddSingleton<IHostedService, SerilogReplacerHostedService>();
            services.AddSingleton(new StartupLogProbe(canary));
            services.AddSingleton<IHostedService>(sp =>
                new ProbeHostedService(
                    sp.GetRequiredService<StartupLogProbe>(),
                    sp.GetRequiredService<ILogger<ProbeHostedService>>()));

            await using var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<IHostedService>().ToArray();
            foreach (var service in hostedServices)
                await service.StartAsync(CancellationToken.None);

            Log.CloseAndFlush();

            var logDirectory = Path.Combine(AppContext.BaseDirectory, "log");
            var matchingFiles = Directory.Exists(logDirectory)
                ? Directory.GetFiles(logDirectory, "bridge-*.log")
                    .Where(path => File.ReadAllText(path).Contains(canary, StringComparison.Ordinal))
                    .ToArray()
                : [];
            Assert.Single(matchingFiles);
        }
        finally
        {
            var testLogger = Log.Logger;
            Log.Logger = originalLogger;
            if (!ReferenceEquals(testLogger, originalLogger))
                (testLogger as IDisposable)?.Dispose();
        }
    }

    private sealed record StartupLogProbe(string Canary);

    private sealed class ProbeHostedService(
        StartupLogProbe probe,
        ILogger<ProbeHostedService> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            log.LogInformation("Hosted startup canary {Canary}", probe.Canary);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
