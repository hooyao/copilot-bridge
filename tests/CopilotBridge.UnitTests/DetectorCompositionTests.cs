using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Anthropic;
using System.Net.ServerSentEvents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// DI-composition contract for the per-request-scoped pipeline. Guards the
/// textbook scoped-DI wiring the refactor established: the request context and the
/// whole assembly tree (stages, strategies, adapters, runner, pipeline, detectors)
/// are scoped services resolved per request, while shared infrastructure stays
/// singleton, and the real container builds with no captive dependency. Protects
/// against a future regression (e.g. making a pipeline component a singleton again,
/// which would capture the scoped context).
/// </summary>
public class DetectorCompositionTests
{
    private static ServiceProvider BuildProvider(bool validateOnBuild = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeServer(config, cliPort: 12345, deviceCodePrinter: null);
        // ValidateScopes surfaces captive dependencies (a singleton capturing a
        // scoped service) the moment the offending service is resolved;
        // ValidateOnBuild forces that check at build time across the whole graph.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = validateOnBuild,
        });
    }

    [Fact]
    public void Detectors_ResolveAsScopedSet_InRegistrationOrder()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        var detectors = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();

        // Registration order = precedence order:
        // DONE-filter → model-rewrite → response-leak → runaway-guard → tool-input validation.
        Assert.Equal(5, detectors.Length);
        Assert.Equal("DoneFilter", detectors[0].Name);
        Assert.Equal("ModelRewrite", detectors[1].Name);
        Assert.Equal("ResponseLeak", detectors[2].Name);
        Assert.Equal("RunawayGuard", detectors[3].Name);
        Assert.Equal("ToolInputValidation", detectors[4].Name);
    }

    [Fact]
    public void Detectors_CarryExplicitOrder_FromRegistrationSequence()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();

        var detectors = scope.ServiceProvider.GetServices<IResponseDetector>().ToArray();

        // The registration sequence is materialized as explicit, unique Order
        // values (0,1,2,3,4) — the guarantee that makes execution order independent of
        // the container's enumeration order.
        Assert.Equal(0, detectors.Single(d => d.Name == "DoneFilter").Order);
        Assert.Equal(1, detectors.Single(d => d.Name == "ModelRewrite").Order);
        Assert.Equal(2, detectors.Single(d => d.Name == "ResponseLeak").Order);
        Assert.Equal(3, detectors.Single(d => d.Name == "RunawayGuard").Order);
        Assert.Equal(4, detectors.Single(d => d.Name == "ToolInputValidation").Order);
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
    public void PipelineComponentTree_IsScoped_DistinctPerScope()
    {
        using var sp = BuildProvider();

        BridgeContext<MessagesRequest> ctx1, ctx2;
        ModelRouterStage stage1, stage2;
        CopilotMessagesPassthroughStrategy strat1, strat2;
        IPipelineRunner<MessagesRequest> runner1, runner2;
        Pipeline<MessagesRequest> pipe1, pipe2;

        using (var scope = sp.CreateScope())
        {
            var p = scope.ServiceProvider;
            ctx1 = p.GetRequiredService<BridgeContext<MessagesRequest>>();
            stage1 = p.GetRequiredService<ModelRouterStage>();
            strat1 = p.GetRequiredService<CopilotMessagesPassthroughStrategy>();
            runner1 = p.GetRequiredService<IPipelineRunner<MessagesRequest>>();
            pipe1 = p.GetRequiredService<Pipeline<MessagesRequest>>();

            // Same instance within one scope — one object tree per request.
            Assert.Same(ctx1, p.GetRequiredService<BridgeContext<MessagesRequest>>());
        }
        using (var scope = sp.CreateScope())
        {
            var p = scope.ServiceProvider;
            ctx2 = p.GetRequiredService<BridgeContext<MessagesRequest>>();
            stage2 = p.GetRequiredService<ModelRouterStage>();
            strat2 = p.GetRequiredService<CopilotMessagesPassthroughStrategy>();
            runner2 = p.GetRequiredService<IPipelineRunner<MessagesRequest>>();
            pipe2 = p.GetRequiredService<Pipeline<MessagesRequest>>();
        }

        // Every component of the assembly tree is a distinct instance per scope —
        // no request shares a pipeline object with a concurrent request.
        Assert.NotSame(ctx1, ctx2);
        Assert.NotSame(stage1, stage2);
        Assert.NotSame(strat1, strat2);
        Assert.NotSame(runner1, runner2);
        Assert.NotSame(pipe1, pipe2);
    }

    [Fact]
    public void SharedInfrastructure_IsSingleton_SameAcrossScopes()
    {
        using var sp = BuildProvider();

        HttpClient http1, http2;
        IAuthService auth1, auth2;
        ICopilotClient copilot1, copilot2;
        using (var scope = sp.CreateScope())
        {
            var p = scope.ServiceProvider;
            http1 = p.GetRequiredService<HttpClient>();
            auth1 = p.GetRequiredService<IAuthService>();
            copilot1 = p.GetRequiredService<ICopilotClient>();
        }
        using (var scope = sp.CreateScope())
        {
            var p = scope.ServiceProvider;
            http2 = p.GetRequiredService<HttpClient>();
            auth2 = p.GetRequiredService<IAuthService>();
            copilot2 = p.GetRequiredService<ICopilotClient>();
        }

        // Process-level shared resources: one instance across all request scopes.
        // Notably no per-request HttpClient (the socket-exhaustion anti-pattern).
        Assert.Same(http1, http2);
        Assert.Same(auth1, auth2);
        Assert.Same(copilot1, copilot2);
    }

    [Fact]
    public void RealContainer_BuildsUnderValidateOnBuild_NoCaptiveDependency()
    {
        // The whole-graph guard: with ValidateOnBuild the container walks every
        // registration at build time and throws on a captive dependency (a
        // singleton that injects the scoped context or any scoped component). A
        // clean build here is the structural guarantee behind per-request isolation.
        using var sp = BuildProvider(validateOnBuild: true);
        Assert.NotNull(sp);
    }

    [Fact]
    public async Task CrossRequestState_DoesNotLeak_BetweenScopes()
    {
        // Observable-behaviour contract (not instance identity): a detector that
        // carries per-request streaming state (the response-leak automaton) must not let
        // one request's state affect another's OUTPUT. Request 1 streams a real
        // <invoke> leak → the guard aborts and marks ResponseLeakDetected. Request 2, on
        // a fresh scope, streams a CLEAN response → it must pass through untouched
        // with ResponseLeakDetected false. If the detector were shared (singleton), the
        // first request's tripped automaton / block state could bleed into the
        // second; scoping is what guarantees it can't.
        using var sp = BuildProvider();

        // Request 1: a leaked tool call → abort + flag set.
        bool firstDetected;
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<BridgeContext<MessagesRequest>>();
            PopulateStreaming(ctx, LeakStream(), tools: new[] { "Read" });
            var stage = scope.ServiceProvider.GetRequiredService<ResponseInspectionStage>();
            await stage.ApplyAsync();
            await Drain(ctx.Response.EventStream!);   // consume to run detection
            firstDetected = ctx.ResponseLeakDetected;
        }

        // Request 2: a clean response on a fresh scope → no residue from request 1.
        bool secondDetected;
        List<SseItem<string>> secondEvents;
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<BridgeContext<MessagesRequest>>();
            PopulateStreaming(ctx, CleanStream(), tools: new[] { "Read" });
            var stage = scope.ServiceProvider.GetRequiredService<ResponseInspectionStage>();
            await stage.ApplyAsync();
            secondEvents = await Drain(ctx.Response.EventStream!);
            secondDetected = ctx.ResponseLeakDetected;
        }

        Assert.True(firstDetected, "request 1 must trip the guard (test precondition)");
        Assert.False(secondDetected, "request 2 must NOT inherit request 1's tripped state");
        Assert.Equal("message_stop", secondEvents[^1].EventType); // clean stream relayed whole
        Assert.DoesNotContain(secondEvents, e => e.EventType == "error");
    }

    private static void PopulateStreaming(
        BridgeContext<MessagesRequest> ctx,
        IAsyncEnumerable<SseItem<string>> stream,
        string[] tools)
    {
        ctx.Request = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = new MessagesRequest
            {
                Model = "claude-opus-4-8",
                Messages = Array.Empty<MessageParam>(),
                Tools = tools.Select(n => new Tool { Name = n }).ToArray(),
            },
        };
        ctx.Response = new BridgeResponse { Mode = ResponseMode.Streaming, EventStream = stream };
        ctx.Ct = default;
    }

    private static async IAsyncEnumerable<SseItem<string>> LeakStream()
    {
        const string leak = "Let me read it.\n<invoke name=\"Read\">\n<parameter name=\"file_path\">/x</parameter>\n</invoke>";
        yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
        yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start");
        yield return new SseItem<string>(
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{System.Text.Json.JsonSerializer.Serialize(leak)}}}}}",
            "content_block_delta");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SseItem<string>> CleanStream()
    {
        yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
        yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start");
        yield return new SseItem<string>(
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{System.Text.Json.JsonSerializer.Serialize("All done, no tools needed.")}}}}}",
            "content_block_delta");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
        await Task.CompletedTask;
    }

    private static async Task<List<SseItem<string>>> Drain(IAsyncEnumerable<SseItem<string>> s)
    {
        var list = new List<SseItem<string>>();
        await foreach (var e in s) list.Add(e);
        return list;
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

    [Fact]
    public void RegisterResponseDetector_RejectsDuplicateType()
    {
        // Contract: a detector type registered twice is a wiring mistake and must
        // fail LOUDLY at registration — never silently produce two entries whose
        // relative precedence is left to container enumeration order.
        var services = new ServiceCollection();
        services.RegisterResponseDetector<DoneFilterDetector>(0);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.RegisterResponseDetector<DoneFilterDetector>(1));
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void RegisterResponseDetector_RejectsDuplicateOrder()
    {
        // Contract: two distinct detectors claiming the same Order is the exact
        // ambiguity explicit ordering exists to remove (OrderBy would break the tie
        // by enumeration order). It must throw, not resolve nondeterministically.
        var services = new ServiceCollection();
        services.RegisterResponseDetector<DoneFilterDetector>(0);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.RegisterResponseDetector<ModelRewriteDetector>(0));
        Assert.Contains("already claimed", ex.Message);
    }

    [Fact]
    public void RegisterResponseDetector_AllowsDistinctTypesAndOrders()
    {
        // Counter-case: the guard must NOT reject a correct registration — distinct
        // types at distinct orders is the normal path and must pass.
        var services = new ServiceCollection();
        services.RegisterResponseDetector<DoneFilterDetector>(0);
        services.RegisterResponseDetector<ModelRewriteDetector>(1);
        services.RegisterResponseDetector<ResponseLeakDetector>(2);

        var orders = services
            .Where(d => d.ServiceType == typeof(IResponseDetector))
            .Count();
        Assert.Equal(3, orders);
    }
}
