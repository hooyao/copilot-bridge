## Context

The forwarding pipeline is assembled in `Hosting/BridgeServiceCollectionExtensions.cs`
from three lifetime tiers that grew organically:

- **Already scoped**: `Pipeline<MessagesRequest>` (via `BuildAnthropicPipeline`),
  `ResponseInspectionStage`, and the three `IResponseDetector`s — these hold
  per-request streaming state (e.g. `ToolLeakDetector`'s automaton).
- **Singleton but stateless**: the six request stages, two upstream strategies, four
  client adapters, and `IPipelineRunner<MessagesRequest>`. Per-request state flows
  through the `BridgeContext` passed as a method argument; these types have no
  mutable fields (verified this session). Correct as singletons *only* while that
  discipline holds.
- **Singleton and shared**: `HttpClient`, `ICopilotClient`, `CopilotHeaderFactory`,
  `AuthService`/`IAuthService`, catalog/registry lookup tables, `BridgeIoSink`,
  `RequestSummaryLogger`, options, hosted services.

The requirement is a structural guarantee: each forwarded request gets its own
isolated DI object tree, created at request start, disposed at request end, fully
isolated between concurrent requests. ASP.NET Core already creates and disposes
exactly such a scope per HTTP request (`HttpContext.RequestServices`), and
minimal-API handler parameters already resolve from it. Two things are missing: the
context is hand-threaded rather than injected (which also forces the detectors'
two-phase `Begin(ctx)` init), and the stateless tier is registered one level too
high (singleton), so it is shared rather than per-request.

## Goals / Non-Goals

**Goals:**

- The request context and the whole per-request assembly tree resolve within the
  request scope; isolation is a property of the registration, not of a "never add a
  field" convention.
- Remove the `ctx` argument from the public pipeline surface by injecting the
  scoped context.
- Detector execution order is an explicit, guaranteed, first-class property — not an
  implicit dependency on `IEnumerable<T>` resolution order.
- Captive dependencies become build-time failures.
- Behavior is byte-for-byte unchanged; shared infrastructure stays singleton.

**Non-Goals:**

- **Not** hoisting stage-local variables into instance fields. The stages are pure
  transforms over the injected context; per-call locals stay local even though the
  instances are now scoped.
- **Not** replacing `Serilog.Context.LogContext.PushProperty("ReqTrace", …)` with
  `ILogger.BeginScope`. The `ReqTrace` property is consumed by
  `ReqTraceFormatEnricher` via Serilog's `LogContext` channel (`Enrich.FromLogContext`);
  routing it through MEL scopes would need `IncludeScopes` + an enricher rewrite and
  is an orthogonal, out-of-scope change that risks the `[<traceId>]` prefix silently
  disappearing.
- **Not** touching `/cc` byte-level passthrough, routing, or any request/response
  transform. **Not** implementing config hot-reload.

## Decisions

### Decision: `BridgeContext<MessagesRequest>` becomes a scoped service

Register `AddScoped<BridgeContext<MessagesRequest>>()`. Endpoints resolve it from the
request scope and populate it; stages, strategies, adapters, the runner, and the
detectors constructor-inject the same instance. Its `Request`/`Response`/`Ct`/
`TraceId` change from `required init` to settable so the DI-created shell can be
filled by the endpoint after resolution.

- **Why not an `IHttpContextAccessor`-style holder / `AsyncLocal`**: unnecessary.
  Unlike `HttpContext` (not a DI service, accessed ambiently at arbitrary depth),
  `BridgeContext` *can* be a scoped service and shared by plain constructor
  injection. A holder would add an indirection layer for no benefit. Rejected.
- **Timing**: DI constructs the empty shell at scope start; the endpoint fills the
  runtime-only data (`RawBody`, `InboundBetas`, `TraceId`, `seq`, `Stopwatch`) — data
  DI cannot supply — before running the pipeline. Every stage reads it only during
  `ApplyAsync`, which is strictly after population. A constructor must NOT read
  `ctx.Request` (it may be unpopulated at construction).
- **Forcing function**: a singleton that injects the scoped context is a captive
  dependency, so the stage/strategy/adapter/runner tier *must* be scoped — see next
  decision. This upgrades the flip from "behavior-equivalent choice" to "enforced by
  the type graph."

### Decision: flip the assembly tier to scoped; keep infrastructure singleton

`AddScoped` for the 6 stages, 2 strategies, 4 adapters, and
`IPipelineRunner<MessagesRequest>` → `PipelineRunner<MessagesRequest>`. Keep singleton
for shared infrastructure. The boundary is **"per-request work unit" vs "process-level
shared resource"**:

- `HttpClient`/`ICopilotClient`: per-request client is the classic socket-exhaustion
  anti-pattern. Singleton.
- `AuthService`: owns the background token-refresh timer + in-memory token cache;
  per-request would re-run device auth. Singleton.
- Catalogs/registries: immutable lookup tables; per-request rebuild is waste.
- `BridgeIoSink` (shared handle), `RequestSummaryLogger` (stateless), options, hosted
  services: process-level by nature.

`BuildAnthropicPipeline` needs no change — its `IServiceProvider` is the request
scope, so its `GetRequiredService<StageX>()` calls resolve the now-scoped instances
automatically.

### Decision: drop `ctx` from public signatures; static helpers keep it

`IRequestStage.ApplyAsync()`, `IUpstreamStrategy.ForwardAsync()`,
`IPipelineRunner.RunAsync(pipeline)`, and the adapter adapt methods lose the `ctx`
parameter; implementations read the injected `_ctx`.

**Honest boundary**: the static helpers `ModelRouteResolver.Apply`,
`ProfileAdjuster.Apply`, and `MatchExpression.Matches` are NOT DI services and cannot
inject the context. `ModelRouterStage` (which injects `_ctx`) still passes it as an
argument into those static calls. So "no explicit passing" holds for the *service
surface*, but inside a service the context still flows as a parameter to static
helpers. This is acceptable and not worth converting the static utilities into
services.

### Decision: guaranteed detector order via `RegisterResponseDetector<T>` + injected `DetectorOrder<T>`

Detector order today is an implicit dependency on `IEnumerable<IResponseDetector>`
resolving in registration order — real but undocumented MS.DI behavior. Make it
explicit and guaranteed, the way ASP.NET Core does with `IOrderedFilter.Order`.

Mechanism (self-contained; no explicit snapshot registration at the call site):

```csharp
internal sealed record DetectorOrder<TDetector>(int Value)
    where TDetector : IResponseDetector;

public static IServiceCollection RegisterResponseDetector<TDetector>(this IServiceCollection services)
    where TDetector : class, IResponseDetector
{
    // order = how many IResponseDetectors are already registered → registration index (0,1,2…)
    var order = services.Count(d => d.ServiceType == typeof(IResponseDetector));
    services.AddScoped<IResponseDetector, TDetector>();
    services.AddSingleton(new DetectorOrder<TDetector>(order));
    return services;
}
```

- **Order source**: `services.Count(…)` reads the count off the service collection
  itself — no `static` counter (which would accumulate across repeated
  `AddBridgeServer` calls in tests). One build → one numbering, isolated per
  `IServiceCollection`.
- **Why `AddSingleton`, not `AddScoped`, for the order**: it is an immutable constant
  value, and only `AddSingleton` has an instance overload; a singleton injected into
  a scoped detector is safe (the unsafe direction is scoped→singleton).
- **Why `DetectorOrder<TDetector>` (closed generic), not `DetectorOrder<IResponseDetector>`**:
  a shared closed type would collide — every detector would resolve the last-registered
  order. Each detector type gets its own closed `DetectorOrder<TSelf>`.
- **Consumption**: `ResponseInspectionStage` sorts `detectors.OrderBy(d => d.Order)`
  instead of trusting resolution order. Unique per-detector order values mean no ties,
  so stable-sort assumptions are irrelevant.
- **Call site** reads as pure declaration order; the numbering is hidden inside the
  extension method:
  ```csharp
  services.RegisterResponseDetector<DoneFilterDetector>();     // 0
  services.RegisterResponseDetector<ModelRewriteDetector>();   // 1
  services.RegisterResponseDetector<ToolLeakDetector>();       // 2
  ```

### Decision: an `AbstractOrderAwareDetector<TSelf>` base carries the order boilerplate (CRTP)

Insert `ToolLeakDetector : AbstractOrderAwareDetector<ToolLeakDetector> : IResponseDetector`.
The base holds the `Order` property and the default `IResponseDetector` members so each
detector stops repeating them, and it takes the strongly-typed `DetectorOrder<TSelf>`
directly (CRTP — the base is generic over the deriving type).

```csharp
internal abstract class AbstractOrderAwareDetector<TSelf> : IResponseDetector
    where TSelf : AbstractOrderAwareDetector<TSelf>
{
    protected AbstractOrderAwareDetector(DetectorOrder<TSelf> order) => Order = order.Value;

    public int Order { get; }
    public abstract string Name { get; }
    public abstract bool Enabled { get; }
    public virtual  bool RequiresBuffering => false;
    public virtual  void Begin() { }                                   // parameterless; see below
    public abstract DetectionAction InspectEvent(in SseItem<string> evt);
    public virtual  DetectionAction InspectBuffered(byte[] body) => DetectionAction.None;
}

internal sealed class ToolLeakDetector : AbstractOrderAwareDetector<ToolLeakDetector>
{
    public ToolLeakDetector(
        DetectorOrder<ToolLeakDetector> order,   // DI injects the derived-typed order
        IOptionsSnapshot<ToolLeakGuardOptions> opts,
        ILogger<ToolLeakDetector> log) : base(order) { … }
}
```

- **Base is generic over the deriving type (`<TSelf>`, CRTP)**: this puts the
  strongly-typed `DetectorOrder<TSelf>` in the base signature, so the "order comes from
  an injected `DetectorOrder`" contract is visible on the base itself. The
  `where TSelf : AbstractOrderAwareDetector<TSelf>` constraint ties the generic to the
  deriving type, and the derived declaration passes itself
  (`: AbstractOrderAwareDetector<ToolLeakDetector>`).
- **Note on DI mechanics**: MS.DI injects only the most-derived constructor; a base
  constructor's parameters are supplied by the derived `: base(order)`, not resolved
  independently. So the derived class still declares `DetectorOrder<TSelf>` as its own
  constructor parameter and forwards it — CRTP does not remove that (nor does the `int`
  alternative). What CRTP buys is the explicit `DetectorOrder` in the base signature;
  its cost is the self-referencing generic on every derived declaration, which the user
  chose to accept.
- `DoneFilterDetector` inherits the empty `Begin()`, default `RequiresBuffering`, and
  default `InspectBuffered`, shrinking to just `Name`/`Enabled`/`InspectEvent`.

### Decision: `Begin()` kept, parameterless

The per-request init hook stays; only its `ctx` argument is dropped. The stage still
calls `Begin()` once per request, after the context is populated and before any
inspection, so a detector builds its automaton from `_ctx.Request.Body.Tools` at the
right moment. Preserving an explicit hook (over lazy-init on first `InspectEvent`)
keeps the initialization timing observable and testable.

### Decision: enable `ValidateScopes` + `ValidateOnBuild`

Configure the host `ServiceProviderOptions` with both true. Converts a captive
dependency (a leftover or future singleton capturing a now-scoped stage/context) into
a fail-fast at startup and in the DI-graph test, instead of a silent per-request leak.

## Risks / Trade-offs

- **[A pre-existing captive dependency elsewhere is exposed by `ValidateOnBuild`]** →
  It is a genuine latent defect; fix it (or resolve via a factory). Do not disable
  validation to hide it.
- **[Someone reads `ctx.Request` in a constructor]** → NullReference / empty-body bug,
  because the shell is unpopulated at construction. Mitigated: constructors take only
  DI services + `DetectorOrder`; request data is read in `ApplyAsync`/`Begin`. Called
  out in code comments on `BridgeContext`.
- **[Someone registers the same detector type twice]** → two `DetectorOrder<TSelf>`
  registrations; last wins, orders could confuse. Mitigated: unlikely by construction;
  `ValidateOnBuild` + the order test guard the graph.
- **[`BridgeContext` settable members lose "constructed-complete" immutability]** →
  Accepted trade: `required init` → settable is the cost of making it a DI shell the
  endpoint fills. The populate-before-run ordering is enforced by the runner.
- **[Extra per-request allocation]** → context + a handful of tiny stage/strategy/
  adapter objects; negligible vs LLM latency. No large/pooled object is scoped; no
  per-request `HttpClient`.
- **[Streaming lifetime]** → the endpoint awaits the full SSE relay inside the handler
  before returning, so the scope outlives stream completion; scoped detectors/adapters
  stay valid throughout. Asserted by the "released at request end" scenario.

## Migration Plan

1. Make `BridgeContext` settable + register it scoped.
2. Add `DetectorOrder<T>`, `RegisterResponseDetector<T>`, `AbstractOrderAwareDetector`;
   move the three detectors onto the base; switch the registration block to
   `RegisterResponseDetector<…>()` in the intended order.
3. Drop `ctx` from the stage/strategy/adapter/runner/detector signatures; inject the
   context; update static-helper call sites to pass the injected context.
4. Flip the assembly tier to `AddScoped`; enable `ValidateScopes` + `ValidateOnBuild`.
5. Update endpoints to resolve + populate the injected context.
6. Update tests (inject the context where they called `ApplyAsync(ctx)`); extend
   `DetectorCompositionTests` with isolation + order + `ValidateOnBuild` contracts;
   mutation-check each.
7. Land on a fresh branch off `main` (unrelated to PR #21).

Rollback is a revert of the registration/lifetime/signature changes; no data or
wire-format migration is involved.

## Open Questions

- None. Base-class style is decided: CRTP (`AbstractOrderAwareDetector<TSelf>` taking
  `DetectorOrder<TSelf>`).
