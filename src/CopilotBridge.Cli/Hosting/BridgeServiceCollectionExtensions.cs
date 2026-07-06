using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
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
        services.Configure<OutboundBetaPolicyOptions>(config.GetSection("Pipeline:OutboundBeta"));
        services.Configure<ResponseModelRewriteOptions>(config.GetSection("Pipeline:Detectors:ModelRewrite"));
        services.Configure<UpstreamRetryOptions>(config.GetSection("Pipeline:UpstreamRetry"));
        services.Configure<ResponseLeakGuardOptions>(config.GetSection("Pipeline:Detectors:ResponseLeakGuard"));
        services.Configure<ToolInputValidationOptions>(config.GetSection("Pipeline:Detectors:ToolInputValidation"));
        services.Configure<RunawayGuardOptions>(config.GetSection("Pipeline:Detectors:RunawayGuard"));
        services.Configure<PoisonedContextOptions>(config.GetSection("Pipeline:PoisonedContext"));

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

        // The per-request context is a SCOPED service: the container creates one
        // empty shell per request scope, the endpoint populates it, and every
        // pipeline component below injects that same instance. This is what makes
        // per-request isolation structural — a singleton that injected it would be
        // a captive dependency, caught by ValidateOnBuild.
        services.AddScoped<BridgeContext<MessagesRequest>>();

        // The single per-request tracing seam (RequestAudit). Same scoped lifetime
        // as the context; owns the one Enabled flag + the four audit emissions + the
        // trace-only buffer factories, no-op when tracing is off. Endpoints and
        // strategies inject the same instance. Replaces the scattered _tracingEnabled
        // fields / tracingEnabled locals / unguarded serialize sites.
        services.AddScoped<RequestAudit>();

        // The whole per-request assembly tree is SCOPED (created per request scope,
        // disposed at request end): adapters, stages, strategies, the runner, and
        // the pipeline. The container — not the assembly point — instantiates each
        // and wires its ILogger<T> + the injected context. BuildAnthropicPipeline
        // only orders them. Process-level shared infrastructure (HttpClient, auth,
        // catalogs, registry, sink, options) stays singleton above/below.
        services.AddScoped<ClaudeCodeInboundAdapter>();
        services.AddScoped<ClaudeCodeOutboundAdapter>();
        services.AddScoped<IPipelineRunner<MessagesRequest>, PipelineRunner<MessagesRequest>>();

        services.AddScoped<ModelRouterStage>();
        services.AddScoped<PoisonedContextScanStage>();
        services.AddScoped<AssistantThinkingFilterStage>();
        services.AddScoped<SystemSanitizeStage>();
        services.AddScoped<MessagesSanitizeStage>();
        services.AddScoped<ToolsSanitizeStage>();
        services.AddScoped<HeadersOutboundStage>();
        // Response detection framework: one stage runs an ordered set of scoped
        // detectors over the response. The former standalone DoneFilterStage /
        // ResponseModelRewriteStage are now detectors. Each detector is a SCOPED
        // service (one instance per request scope) so cross-delta streaming state
        // (e.g. the response-leak automaton) never crosses requests; it self-gates on
        // its config (IResponseDetector.Enabled, backed by IOptionsSnapshot) and
        // reads its per-request data in Begin(). RegisterResponseDetector takes an
        // EXPLICIT Order (DONE-filter 0 → model-rewrite 1 → response-leak 2 →
        // runaway-guard 3); the stage runs them by that Order, so precedence does
        // NOT depend on IEnumerable<T> resolution order. Adding a detector = one
        // RegisterResponseDetector<...> line here with the next Order; a duplicate
        // type or duplicate Order throws at registration rather than silently making
        // precedence ambiguous.
        services.RegisterResponseDetector<DoneFilterDetector>(0);
        services.RegisterResponseDetector<ModelRewriteDetector>(1);
        services.RegisterResponseDetector<ResponseLeakDetector>(2);
        services.RegisterResponseDetector<RunawayGuardDetector>(3);
        services.RegisterResponseDetector<ToolInputValidationDetector>(4);
        services.AddScoped<ResponseInspectionStage>();
        services.AddScoped<CopilotMessagesPassthroughStrategy>();

        // --- Codex / Responses (change 3) ---------------------------------
        // The Codex client edge (T1/T4 real translators) + the Responses backend
        // strategy (T2/T3) + the per-model effort profile catalog. All register
        // into the SAME shared Pipeline<MessagesRequest> below; the strategy
        // registry picks by target.Vendor (CopilotAnthropic vs CopilotResponses).
        // The catalog is process-level (singleton); the adapters + strategy are
        // per-request (scoped), same as the Anthropic tier above.
        services.AddSingleton<CodexModelProfileCatalog>();
        services.AddScoped<ResponsesToIrInboundAdapter>();
        services.AddScoped<IrToResponsesOutboundAdapter>();
        services.AddScoped<CopilotResponsesStrategy>();

        // The Anthropic-IR pipeline is composed per request scope: its stages,
        // strategies, and the injected context are all scoped. BuildAnthropicPipeline
        // receives the request-scope IServiceProvider, so its GetRequiredService
        // calls resolve the scoped components.
        services.AddScoped(BuildAnthropicPipeline);

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

    /// <summary>
    /// Register a response detector as a scoped service AND its precedence
    /// <see cref="DetectorOrder{TDetector}"/> (a singleton constant), so the detector
    /// can expose <see cref="IResponseDetector.Order"/> and the inspection stage can
    /// run detectors in a guaranteed order independent of the container's
    /// <c>IEnumerable&lt;T&gt;</c> resolution order. <paramref name="order"/> is the
    /// EXPLICIT precedence (lower runs first). Passing it literally — rather than
    /// deriving it from the count of prior registrations — means precedence is a
    /// visible constant at the call site and does not silently shift if an unrelated
    /// <see cref="IResponseDetector"/> registration is inserted between two detectors.
    /// </summary>
    /// <remarks>
    /// Guards a miswiring loudly instead of producing nondeterministic precedence:
    /// registering the same detector type twice, or two detectors at the same
    /// <paramref name="order"/>, throws <see cref="System.InvalidOperationException"/>
    /// at registration. Without the guard a duplicate order lets the stage's
    /// <c>OrderBy(d =&gt; d.Order)</c> break the tie by container enumeration order —
    /// the exact ambiguity the explicit order exists to remove.
    /// </remarks>
    internal static IServiceCollection RegisterResponseDetector<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TDetector>(this IServiceCollection services, int order)
        where TDetector : class, IResponseDetector
    {
        foreach (var d in services)
        {
            // Duplicate detector type: the same concrete detector registered twice.
            if (d.ServiceType == typeof(IResponseDetector)
                && d.ImplementationType == typeof(TDetector))
            {
                throw new System.InvalidOperationException(
                    $"Response detector {typeof(TDetector).Name} is already registered; register each detector exactly once.");
            }
            // Duplicate order: another detector already claims this precedence.
            if (d.ServiceType is { IsGenericType: true } t
                && t.GetGenericTypeDefinition() == typeof(DetectorOrder<>)
                && d.ImplementationInstance is IDetectorOrder existing
                && existing.Value == order)
            {
                throw new System.InvalidOperationException(
                    $"Response detector order {order} is already claimed by {t.GetGenericArguments()[0].Name}; each detector needs a distinct order.");
            }
        }
        services.AddScoped<IResponseDetector, TDetector>();
        services.AddSingleton(new DetectorOrder<TDetector>(order));
        return services;
    }

    /// <summary>
    /// Compose the Anthropic-IR pipeline by ordering DI-resolved stages and
    /// strategies. Stages are scoped services registered in
    /// <see cref="AddBridgeServer"/>; <paramref name="sp"/> is the request-scope
    /// provider, so this resolves the per-request instances. This method only
    /// decides the order.
    /// </summary>
    private static Pipeline<MessagesRequest> BuildAnthropicPipeline(IServiceProvider sp)
    {
        // The scoped context shared by the gate decorator and the inner stages.
        var ctx = sp.GetRequiredService<BridgeContext<MessagesRequest>>();
        return new()
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
                //    NOT vendor-gated: this stage resolves ctx.Target that the
                //    gate below reads, and it short-circuits internally for
                //    CopilotResponses targets.
                sp.GetRequiredService<ModelRouterStage>(),

                // 1b. Observe-only poisoned-context scan. NOT vendor-gated and NOT
                //    wrapped by CopilotAnthropicOnlyStage: it runs for both /cc and
                //    /codex because a transcript poisoned with replayed API-error
                //    tool_results degrades any weaker backend model. It mutates
                //    nothing — counts the debris onto ctx.PoisonedToolResults and
                //    logs a "compact your session" WARNING when heavy — so it is safe
                //    on every path. Placed after routing so the count reflects the
                //    body actually being sent; the messages it inspects are not
                //    changed by routing.
                sp.GetRequiredService<PoisonedContextScanStage>(),

                // 2-6. Anthropic-backend-only stages. The shared pipeline also
                //    carries the Codex/Responses IR (gpt-* → CopilotResponses);
                //    these stages encode /v1/messages-specific assumptions and
                //    MUST NOT mutate a Codex request. MessagesSanitizeStage in
                //    particular appends a "Please continue." user turn when the
                //    IR ends with an assistant message — which is exactly the
                //    shape T1 produces for a Codex input[] ending in a reasoning
                //    item or bare function_call, corrupting it. So each is wrapped
                //    by CopilotAnthropicOnlyStage: for a CopilotAnthropic target it
                //    delegates verbatim (the /cc hot path is byte-identical); for
                //    any other vendor it is a no-op.
                //
                //  Note: CacheControlCleanStage from research §3.6 rule 1 is
                //  intentionally not present. The DTO does not model
                //  `cache_control.scope`, so the field is silently dropped at
                //  deserialize time. If the DTO ever grows a Scope property,
                //  add the stage to actively clear it.
                CopilotAnthropicOnlyStage.Wrap(sp.GetRequiredService<AssistantThinkingFilterStage>(), ctx),
                CopilotAnthropicOnlyStage.Wrap(sp.GetRequiredService<SystemSanitizeStage>(), ctx),
                CopilotAnthropicOnlyStage.Wrap(sp.GetRequiredService<MessagesSanitizeStage>(), ctx),
                CopilotAnthropicOnlyStage.Wrap(sp.GetRequiredService<ToolsSanitizeStage>(), ctx),

                // 6. Always last for the Anthropic backend — generates outbound
                //    headers from the FINAL body shape. Harmless to skip for Codex
                //    (the Responses strategy ignores ctx.Request.Headers and builds
                //    its own via CopilotHeaderFactory), but gated for consistency.
                CopilotAnthropicOnlyStage.Wrap(sp.GetRequiredService<HeadersOutboundStage>(), ctx),
            ],
            ResponseStages =
            [
                // One stage runs the whole detector framework in a single stream
                // wrap: DONE-filter (drop [DONE]) → model-rewrite (restore the
                // client model id) → response-leak guard (abort+retry on leaked XML)
                // → runaway guard (abort a degenerate volume runaway) → tool-input
                // validation (abort malformed tool arguments). Order inside
                // the set is the detectors' explicit Order (assigned by
                // RegisterResponseDetector), applied by the stage.
                sp.GetRequiredService<ResponseInspectionStage>(),
            ],
            Strategies = new StrategyRegistry<MessagesRequest>(
            [
                sp.GetRequiredService<CopilotMessagesPassthroughStrategy>(),
                // Codex/Responses backend — selected when the model resolves to
                // CopilotResponses (gpt-5.x). Same shared pipeline; routing by vendor.
                sp.GetRequiredService<CopilotResponsesStrategy>(),
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
/// Decorator that runs an Anthropic-backend-only request stage only when the
/// resolved target is <see cref="BackendVendor.CopilotAnthropic"/>, and no-ops
/// for any other vendor (today: <see cref="BackendVendor.CopilotResponses"/> —
/// the Codex/Responses path that shares this one <c>Pipeline&lt;MessagesRequest&gt;</c>).
/// </summary>
/// <remarks>
/// <para>The shared IR pipeline carries both the /cc Anthropic body and the
/// Codex IR. The body-mutating Anthropic stages (e.g. MessagesSanitizeStage
/// appending a "Please continue." turn after a trailing assistant message)
/// encode /v1/messages-specific assumptions that corrupt a Codex request, so
/// they must not touch a non-Anthropic target. Gating happens here, at pipeline
/// assembly, rather than inside each stage — the stages stay single-purpose and
/// vendor-agnostic.</para>
/// <para>For a CopilotAnthropic target the decorator delegates verbatim, so the
/// /cc hot path is byte-identical (this wrapper adds no behavior to it). The
/// gate runs after ModelRouterStage has set <c>ctx.Target</c>; if Target were
/// somehow still null the stage would be skipped, but the runner already
/// guarantees ModelRouterStage runs first.</para>
/// </remarks>
internal sealed class CopilotAnthropicOnlyStage : IRequestStage<MessagesRequest>
{
    private readonly IRequestStage<MessagesRequest> _inner;
    private readonly BridgeContext<MessagesRequest> _ctx;

    private CopilotAnthropicOnlyStage(IRequestStage<MessagesRequest> inner, BridgeContext<MessagesRequest> ctx)
    {
        _inner = inner;
        _ctx = ctx;
    }

    public static CopilotAnthropicOnlyStage Wrap(IRequestStage<MessagesRequest> inner, BridgeContext<MessagesRequest> ctx) =>
        new(inner, ctx);

    public string Name => _inner.Name;

    public Task ApplyAsync() =>
        _ctx.Target?.Vendor == BackendVendor.CopilotAnthropic
            ? _inner.ApplyAsync()
            : Task.CompletedTask;
}

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
