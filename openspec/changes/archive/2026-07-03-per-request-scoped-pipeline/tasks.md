# Implementation Tasks

## 1. BridgeContext as a scoped service

- [x] 1.1 In `Pipeline/BridgeContext.cs`, change `Request`/`Response`/`Ct`/`TraceId`
  from `required init` to settable, and add an XML-doc note that the DI shell is
  populated by the endpoint before the pipeline runs — constructors of injected
  components MUST NOT read `Request` (unpopulated at construction).
- [x] 1.2 Register `AddScoped<BridgeContext<MessagesRequest>>()` in
  `Hosting/BridgeServiceCollectionExtensions.cs`.

## 2. Detector order mechanism

- [x] 2.1 Add `DetectorOrder<TDetector>(int Value)` record (constraint
  `where TDetector : IResponseDetector`).
- [x] 2.2 Add `RegisterResponseDetector<TDetector>()` extension: compute the order as
  the current count of `IResponseDetector` registrations, `AddScoped<IResponseDetector,
  TDetector>()`, and `AddSingleton(new DetectorOrder<TDetector>(order))`.
- [x] 2.3 Add `AbstractOrderAwareDetector<TSelf>` base (CRTP:
  `where TSelf : AbstractOrderAwareDetector<TSelf>`) whose ctor takes
  `DetectorOrder<TSelf>` and sets `Order`; it also holds the default members
  (`RequiresBuffering`, parameterless `Begin()`, `InspectBuffered`) and leaves
  `Name`/`Enabled`/`InspectEvent` abstract.
- [x] 2.4 Move `DoneFilterDetector`, `ModelRewriteDetector`, `ToolLeakDetector` onto
  the base (`: AbstractOrderAwareDetector<TSelf>` with each passing itself); each ctor
  injects `DetectorOrder<TSelf>` and forwards it via `: base(order)`.
- [x] 2.5 Replace the three `AddScoped<IResponseDetector, …>()` lines with
  `RegisterResponseDetector<…>()` in the intended precedence order (DONE-filter →
  model-rewrite → tool-leak).
- [x] 2.6 In `ResponseInspectionStage`, sort the injected detectors by `Order`
  (`detectors.OrderBy(d => d.Order)`) instead of relying on resolution order.

## 3. Remove ctx from the pipeline surface

- [x] 3.1 `IResponseDetector.Begin(ctx)` → `Begin()`; detectors read the injected
  `_ctx`. Inject `BridgeContext<MessagesRequest>` into each detector.
- [x] 3.2 `IRequestStage.ApplyAsync(ctx)` → `ApplyAsync()`; inject the context into
  the six stages and `CopilotAnthropicOnlyStage`; read `_ctx`. Static helper calls
  (`ModelRouteResolver.Apply`, `ProfileAdjuster.Apply`, `MatchExpression.Matches`)
  keep receiving the injected context as an argument.
- [x] 3.3 `IUpstreamStrategy.ForwardAsync(ctx)` → `ForwardAsync()`; inject the context
  into both strategies; read `_ctx`.
- [x] 3.4 Adapter adapt methods drop the `ctx` parameter (keep stream/`ct`); inject
  the context into the four adapters.
- [x] 3.5 `IPipelineRunner.RunAsync(pipeline, ctx)` → `RunAsync(pipeline)`; inject the
  context into `PipelineRunner`; read `_ctx`.

## 4. Lifetime flip + validation

- [x] 4.1 Flip the 6 stages, 2 strategies, 4 adapters, and
  `IPipelineRunner<MessagesRequest>` from `AddSingleton` to `AddScoped`.
- [x] 4.2 Leave shared-infrastructure singletons untouched (`HttpClient`,
  `ICopilotClient`, `CopilotHeaderFactory`, `AuthService`/`IAuthService`, catalogs,
  registry, `BridgeIoSink`, `RequestSummaryLogger`, options, hosted services,
  `KestrelOptionsConfigurator`). Confirm `BuildAnthropicPipeline` is unchanged.
- [x] 4.3 Enable `ValidateScopes = true` + `ValidateOnBuild = true` on the host
  service-provider options.

## 5. Endpoints

- [x] 5.1 `ClaudeCodeMessagesEndpoint.HandleAsync`: resolve the scoped
  `BridgeContext<MessagesRequest>` as a handler parameter, populate its fields
  (Request/Response/Ct/TraceId/InboundBetas) instead of `new`-ing it, and call
  `runner.RunAsync(pipeline)`.
- [x] 5.2 `CodexResponsesEndpoint.HandleAsync`: same treatment.

## 6. Tests (contract-derived, mutation-checked)

### 6a. Signature migration

- [x] 6.1 Update tests that call `stage.ApplyAsync(ctx)` / `detector.Begin(ctx)` /
  `runner.RunAsync(pipeline, ctx)` directly to inject the context and call the new
  signatures. `new BridgeContext{…}` builders still compile (settable members).

### 6b. DI lifetime contract (structural — extend DetectorCompositionTests)

- [x] 6.2 Distinct-per-scope: resolving the pipeline component tree (a stage, a
  strategy, an adapter, the runner, the pipeline, the context, a detector) from two
  different scopes yields distinct instances per scope. This same scoped-lifetime
  contract also covers the "released at request end" scenario — a scoped registration
  is exactly what makes an instance both per-scope AND released with the scope, so the
  6.7 mutation (revert to singleton) guards both facets. No separate dispose probe is
  added: disposing a scoped `IDisposable` at scope end is a framework guarantee, not
  our contract to re-test.
- [x] 6.3 Singleton-shared: `HttpClient`, `ICopilotClient`, `AuthService`/
  `IAuthService`, and a catalog each resolve to the SAME instance across two scopes —
  explicitly covering the "no per-request HTTP client" scenario, not only AuthService.
- [x] 6.4 Container health: the real production registrations build under
  `ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }` with no
  captive dependency.

### 6c. Observable cross-request isolation (behavioural — from the requirement text)

- [x] 6.5 Two sequential requests through an in-process pipeline (stubbed upstream):
  the second request's observable output (e.g. its dropped-events list / summary /
  response bytes) contains only its own per-request data, none of the first request's
  residue. Derived from "a component carrying per-request state SHALL NOT be observed
  by any other request" — asserts observable behaviour, not instance identity.
  (Detector *concurrency* isolation is intentionally NOT written as a flaky parallel
  unit test; it is guarded structurally by 6.2 + 6.4.)

### 6d. Detector order

- [x] 6.6 Order contract: (a) when two detectors would both act on the same event, the
  lower-`Order` one takes effect (precedence — "detectors run in registration order");
  and (b) the stage re-establishes order from the explicit `Order` values even when the
  detectors are supplied in a different enumeration order ("order independent of
  resolution order").

### 6e. Mutation checks

- [x] 6.7 Mutation-check each contract test (break product code → watch red → revert):
  revert a flipped registration to `AddSingleton` → 6.2 red; register `AuthService` as
  scoped → 6.3 red; introduce a singleton capturing a scoped stage/context → 6.4 red;
  register `BridgeContext` as singleton → 6.5 red (first request's residue visible to
  the second); perturb the `Order` assignment → 6.6 red.

## 7. Verification

- [x] 7.1 `dotnet build src\CopilotBridge.Cli\CopilotBridge.Cli.csproj -c Debug --nologo`
  — 0 warnings.
- [x] 7.2 `dotnet test CopilotBridge.slnx --filter "Category!=Integration" --nologo`
  — green.
- [x] 7.3 Smoke-run the bridge on a non-8765 port to confirm the host starts under the
  new validation flags.

## 8. Docs / specs

- [x] 8.1 Fold the durable architectural facts into `docs/pipeline-design.md`: the
  per-request scope boundary (scoped context + assembly tier vs singleton
  infrastructure, and why), the `RegisterResponseDetector`/`DetectorOrder` ordering
  mechanism, and the `ValidateScopes`/`ValidateOnBuild` guardrail.
- [ ] 8.2 `openspec validate per-request-scoped-pipeline --strict` passes; then
  `openspec archive per-request-scoped-pipeline -y` to sync the new
  `pipeline-request-isolation` spec into the main specs (normalize the spec file's
  line endings afterward per the repo's CRLF/LF gotcha).
