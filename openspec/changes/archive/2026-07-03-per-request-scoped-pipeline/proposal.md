## Why

The request-forwarding pipeline is assembled from components registered with a
mix of DI lifetimes. `Pipeline<MessagesRequest>` and the response-detection
framework are already scoped (they hold per-request streaming state), but the six
request stages, both upstream strategies, the four client adapters, and the
pipeline runner are singletons. They are stateless *today* only by the discipline
of never adding a mutable field — a fragile invariant, invisible at the
registration site, that would fail silently as a cross-request state leak the first
time someone adds per-request state to a "singleton" stage.

The cleaner model is to make the request context itself a scoped service. Today
`BridgeContext` is hand-constructed by each endpoint and threaded through every
stage/strategy/detector as an explicit method argument, which also forces the
detectors into a two-phase `Begin(ctx)` initialization (constructor can't see the
context because it isn't a DI service). Making `BridgeContext` a scoped service lets
the whole per-request assembly tree inject the one shared context, removes the ctx
argument from the public pipeline surface, and turns per-request isolation into a
structural guarantee: injecting a scoped context makes a singleton stage a
build-time captive-dependency error, so the stages *must* be scoped.

Separately, detector execution order is currently an implicit dependency on the
`IEnumerable<IResponseDetector>` resolution order matching registration order — a
real but undocumented DI behavior. The requirement is that order be *guaranteed*, so
it should be an explicit, first-class property (as ASP.NET Core does with
`IOrderedFilter.Order`), not an implicit resolution-order assumption.

## What Changes

- **`BridgeContext<MessagesRequest>` becomes a scoped DI service.** Endpoints resolve
  it from the request scope and populate it; stages, strategies, adapters, the
  pipeline runner, and the detectors constructor-inject the same instance. The `ctx`
  parameter is removed from the public pipeline method signatures
  (`IRequestStage.ApplyAsync`, `IUpstreamStrategy.ForwardAsync`,
  `IPipelineRunner.RunAsync`, adapter adapt methods). `BridgeContext`'s
  `Request`/`Response`/`Ct`/`TraceId` change from `required init` to settable so the
  endpoint can fill the DI-created shell.
- **The pipeline-assembly tier flips from `AddSingleton` to `AddScoped`**: the 6
  request stages, 2 strategies, 4 adapters, and `IPipelineRunner<MessagesRequest>`.
  This is now *enforced* — a singleton injecting the scoped `BridgeContext` is a
  captive dependency.
- **`IResponseDetector` gains an `Order` property**, auto-assigned by an incrementing
  counter at registration time in `BridgeServiceCollectionExtensions` (first
  registered → 0, next → 1, …). `ResponseInspectionStage` sorts detectors by `Order`
  instead of relying on `IEnumerable` resolution order. Registration order remains
  the single source of truth, now materialized as explicit, unique `Order` values.
- **`IResponseDetector.Begin(ctx)` becomes parameterless `Begin()`**; the detector
  reads the injected context. The stage still calls `Begin()` once per request after
  the context is populated and before any inspection — the per-request
  initialization timing hook is preserved, only its argument is dropped.
- **Container guardrails**: enable `ValidateScopes` + `ValidateOnBuild` on the host
  service provider so any captive dependency fails fast at startup/tests instead of
  leaking silently.
- Process-level shared infrastructure stays singleton (unchanged): `HttpClient`,
  `ICopilotClient`, `CopilotHeaderFactory`, `AuthService`/`IAuthService`, the
  catalog/registry lookup tables, `BridgeIoSink`, `RequestSummaryLogger`, options,
  hosted services, `KestrelOptionsConfigurator`.

## Capabilities

### New Capabilities
- `pipeline-request-isolation`: each forwarded request runs on its own per-request DI
  scope; the request context and every pipeline-assembly component are resolved per
  request from that scope and never shared across concurrent requests; the scope is
  disposed at request end; process-level infrastructure stays singleton; and the
  container is validated so a captive dependency is a build-time failure. Includes
  the guaranteed, explicit detector execution order.

### Modified Capabilities
<!-- None. observability and tool-leak-guard requirements (what the guard detects,
     what it logs) are unchanged; this change restructures DI lifetime and the
     detector-ordering mechanism without changing their spec-level behavior. -->

## Impact

- **Code**: `Hosting/BridgeServiceCollectionExtensions.cs` (registrations + Order
  counter + validation options), `Pipeline/BridgeContext.cs` (settable members,
  scoped), `Pipeline/PipelineRunner.cs` and the `IRequestStage`/`IUpstreamStrategy`/
  adapter interfaces + all implementations (drop ctx param, inject context),
  `Pipeline/Response/Detection/IResponseDetector.cs` (+`Order`, parameterless
  `Begin`) and the three detectors + `ResponseInspectionStage` (sort by Order), and
  the two endpoints (resolve+populate the scoped context). Static helpers
  (`ModelRouteResolver`, `ProfileAdjuster`, `MatchExpression`) keep taking `ctx` as a
  parameter — they are not DI services and cannot inject it; the caller passes its
  injected context in.
- **Behavior**: none observable to clients. Byte-identical forwarding; lifetime +
  wiring change only. Detector order is unchanged (same registration order, now
  explicit).
- **Performance**: a small per-request allocation of the context + stage/strategy/
  adapter tree; negligible against LLM-scale latency. No per-request `HttpClient`
  (stays singleton — socket-exhaustion anti-pattern avoided).
- **Tests**: `new BridgeContext{...}` test builders still compile (settable members);
  tests that call `stage.ApplyAsync(ctx)` directly move to injecting the context.
  Extend `DetectorCompositionTests` with isolation + Order + `ValidateOnBuild`
  contracts.
- **AOT**: unaffected — `AddScoped<TService,TImpl>()` overloads are trim/AOT-clean.
- **Deferred non-goal**: hoisting stage-local variables into instance fields — an
  anti-pattern; the stages' per-call locals stay local even though the instances are
  now scoped.
