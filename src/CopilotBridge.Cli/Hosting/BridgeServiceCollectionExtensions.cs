using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Single DI-registration entry point. Holds <see cref="AddBridgeServer"/>
/// (every service / option / hosted service the <c>serve</c> command needs)
/// plus <see cref="AddBridgeLogging"/> (MEL→Serilog wiring). Keeps
/// <see cref="ServeCommand"/> short enough to read in one screen.
/// </summary>
internal static class BridgeServiceCollectionExtensions
{
    /// <summary>
    /// Replace the slim builder's default logging providers with the
    /// Serilog bridge. Bootstrap Serilog (console-only, stderr) must
    /// already be running — see <see cref="SerilogBootstrapper.BuildBootstrap"/>.
    /// </summary>
    public static ILoggingBuilder AddBridgeLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddProvider(new SerilogLoggerProvider(dispose: false));
        return logging;
    }

    /// <summary>
    /// Register every singleton, option, and hosted service the bridge needs.
    /// Hosted-service ordering matters: <see cref="SerilogReplacerHostedService"/>
    /// is registered first so it runs before <see cref="BridgeStartupHostedService"/>
    /// — that way the auth-bootstrap output and startup banner land in the
    /// full Serilog pipeline (rolling file + audit), not the bootstrap
    /// console-only one.
    /// </summary>
    /// <param name="services">DI container being built.</param>
    /// <param name="config">Configuration root (already loaded from
    /// <c>appsettings.json</c>).</param>
    /// <param name="cliPort">Optional CLI-provided port override; wins over
    /// the appsettings value when present.</param>
    /// <param name="deviceCodePrinter">Optional callback the auth service calls
    /// when it issues a device-code challenge. Production passes a stdout
    /// printer; tests may pass <c>null</c> to no-op.</param>
    public static IServiceCollection AddBridgeServer(
        this IServiceCollection services,
        IConfiguration config,
        int? cliPort = null,
        Action<DeviceCodeChallenge>? deviceCodePrinter = null)
    {
        // --- Options -------------------------------------------------------
        services.Configure<BridgeServerOptions>(config.GetSection("Server"));
        if (cliPort.HasValue)
        {
            var port = cliPort.Value;
            services.PostConfigure<BridgeServerOptions>(opts => opts.Port = port);
        }
        services.Configure<TracingOptions>(config.GetSection("Tracing"));
        services.Configure<RoutesConfig>(config.GetSection("Routing"));

        // Kestrel listens on the (post-PostConfigure) port + uses our generous
        // keep-alive limits. Configured via IConfigureOptions so it can pull
        // BridgeServerOptions from DI; doing this with services.Configure
        // doesn't have access to other options.
        services.AddSingleton<IConfigureOptions<KestrelServerOptions>, KestrelOptionsConfigurator>();

        // --- JSON ----------------------------------------------------------
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default);
        });

        // --- HTTP + auth + Copilot client ---------------------------------
        // HttpClient and AuthService keep factory registrations: the former
        // needs UserAgent setup, the latter takes the closure-captured
        // deviceCodePrinter (DI can't supply it). Everything else with a
        // straightforward constructor uses the two-param overload so the
        // container does its own activation — fewer moving pieces, same
        // AOT-safety (Microsoft.Extensions.DependencyInjection's two-param
        // AddSingleton<TService, TImpl> is trim-clean since .NET 8).
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
            return http;
        });
        services.AddSingleton(sp => new AuthService(
            sp.GetRequiredService<HttpClient>(),
            deviceCodePrinter));
        // Forwarding singleton — same AuthService instance reachable through
        // both the concrete type and the IAuthService interface. Must stay
        // a factory; AddSingleton<IAuthService, AuthService>() would create
        // a second instance.
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<CopilotHeaderFactory>();
        services.AddSingleton<ICopilotClient, CopilotClient>();

        // --- Logging sink (DI-owned, optional based on TracingOptions) ----
        // Registered via the non-generic overload because the factory may
        // legitimately return null when tracing is disabled — the generic
        // AddSingleton<T> requires T : class which collides with the
        // nullable annotation. Consumers ask for it with GetService<BridgeIoSink>()
        // and check for null themselves.
        services.AddSingleton(typeof(BridgeIoSink), (IServiceProvider sp) =>
        {
            var tracing = sp.GetRequiredService<IOptions<TracingOptions>>();
            if (!tracing.Value.Enabled) return null!;
            var dir = SerilogBootstrapper.ResolveTracingDirectory(tracing);
            return new BridgeIoSink(dir);
        });

        // --- Pipeline ------------------------------------------------------
        services.AddSingleton<ModelProfileCatalog>();
        services.AddSingleton<IModelRegistry, CopilotModelRegistry>();
        services.AddSingleton<ClaudeCodeInboundAdapter>();
        services.AddSingleton<ClaudeCodeOutboundAdapter>();
        services.AddSingleton<IPipelineRunner<MessagesRequest>, PipelineRunner<MessagesRequest>>();
        services.AddSingleton(sp => BuildAnthropicPipeline(sp));

        // --- New per-request summary logger -------------------------------
        services.AddSingleton<RequestSummaryLogger>();

        // --- Hosted services (ordering matters!) --------------------------
        // 1. Replace bootstrap Serilog with the full logger before anything
        //    else runs, so subsequent startup logs land in console+file+audit.
        services.AddHostedService<SerilogReplacerHostedService>();
        // 2. Auth + routes validation + startup banner.
        services.AddHostedService<BridgeStartupHostedService>();

        return services;
    }

    private static Pipeline<MessagesRequest> BuildAnthropicPipeline(IServiceProvider sp)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        return new Pipeline<MessagesRequest>
        {
            Name = "Anthropic-IR",
            RequestStages =
            [
                // 1. Routing + per-model body coercion. Normalize → first
                //    matching user rule (model redirect only) → profile
                //    lookup in ModelProfileCatalog → ProfileAdjuster
                //    mechanically shapes the body to what the target
                //    profile accepts. A missing profile throws
                //    UnknownModelException, surfaced as a 400 by the endpoint.
                new ModelRouterStage(
                    sp.GetRequiredService<IModelRegistry>(),
                    sp.GetRequiredService<ModelProfileCatalog>(),
                    sp.GetRequiredService<IOptions<RoutesConfig>>(),
                    loggerFactory.CreateLogger<ModelRouterStage>(),
                    loggerFactory.CreateLogger<ModelRouteResolverLog>(),
                    loggerFactory.CreateLogger<ProfileAdjusterLog>()),

                // 2-5. Body-level cleanups, each independent of model family.
                //
                //  Note: CacheControlCleanStage from research §3.6 rule 1 is
                //  intentionally not present. The DTO does not model
                //  `cache_control.scope`, so the field is silently dropped at
                //  deserialize time. If the DTO ever grows a Scope property,
                //  add the stage to actively clear it.
                new AssistantThinkingFilterStage(loggerFactory.CreateLogger<AssistantThinkingFilterStage>()),
                new SystemSanitizeStage(loggerFactory.CreateLogger<SystemSanitizeStage>()),
                new MessagesSanitizeStage(loggerFactory.CreateLogger<MessagesSanitizeStage>()),
                new ToolsSanitizeStage(loggerFactory.CreateLogger<ToolsSanitizeStage>()),

                // 6. Always last — generates outbound headers from the FINAL body shape.
                new HeadersOutboundStage(loggerFactory.CreateLogger<HeadersOutboundStage>()),
            ],
            ResponseStages =
            [
                new DoneFilterStage(loggerFactory.CreateLogger<DoneFilterStage>()),
            ],
            Strategies = new StrategyRegistry<MessagesRequest>(
            [
                new CopilotMessagesPassthroughStrategy(
                    sp.GetRequiredService<ICopilotClient>(),
                    loggerFactory.CreateLogger<CopilotMessagesPassthroughStrategy>()),
            ]),
        };
    }
}

/// <summary>
/// Marker logger categories used by the static routing helpers
/// (<see cref="ModelRouteResolver"/> / <see cref="ProfileAdjuster"/>) — they
/// can't take a typed <c>ILogger&lt;T&gt;</c> because they're static, so we
/// give them dedicated category types created by the stage that calls them.
/// </summary>
internal sealed class ModelRouteResolverLog { }
internal sealed class ProfileAdjusterLog { }

/// <summary>
/// Configures Kestrel to listen on the port stored in
/// <see cref="BridgeServerOptions"/>. Lives as its own
/// <see cref="IConfigureOptions{TOptions}"/> so it can inject another
/// <see cref="IOptions{TOptions}"/> — <c>services.Configure&lt;KestrelServerOptions&gt;</c>
/// can't take constructor dependencies.
/// </summary>
internal sealed class KestrelOptionsConfigurator : IConfigureOptions<KestrelServerOptions>
{
    private readonly IOptions<BridgeServerOptions> _server;

    public KestrelOptionsConfigurator(IOptions<BridgeServerOptions> server)
    {
        _server = server;
    }

    public void Configure(KestrelServerOptions options)
    {
        var port = _server.Value.Port;
        if (port is < 1 or > 65535)
        {
            throw new BridgeStartupException(
                $"Invalid port {port}. Must be in 1..65535. Set Server:Port in appsettings.json or pass --port.");
        }
        options.ListenLocalhost(port);
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
    }
}
