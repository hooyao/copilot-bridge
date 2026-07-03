## ADDED Requirements

### Requirement: Per-request pipeline scope

Each forwarded request SHALL execute on its own dependency-injection scope. The
per-request request context and every component that assembles or drives the
forwarding pipeline for a request — the request stages, the upstream strategies, the
client adapters, the pipeline runner, the `Pipeline` object, and the response
detectors — SHALL be resolved from that request's scope, so that no such instance is
shared between two requests served concurrently.

The scope, and every disposable scoped service resolved within it, SHALL be disposed
when the request completes, including after a streaming response has been fully
relayed. A component carrying per-request state (for example the response-leak
detector's streaming automaton) therefore SHALL NOT be able to observe, or be
observed by, any other request.

#### Scenario: Concurrent requests do not share pipeline component instances
- **WHEN** two requests are each served on their own request scope
- **THEN** each request resolves its own distinct instances of the request context
  and the pipeline components (stages, strategies, adapters, runner, pipeline, and
  detectors), and neither request's instances are the same objects as the other's

#### Scenario: Scoped components are released at request end
- **WHEN** a request scope is disposed after the response has been delivered
- **THEN** the scoped context and pipeline components created for that request are
  released with the scope, and are not retained for reuse by a later request

### Requirement: Shared infrastructure remains process-scoped

Process-level shared infrastructure SHALL remain registered as singletons and SHALL
NOT be re-created per request. This includes the outbound `HttpClient` and Copilot
HTTP client, the authentication service that owns the token-refresh timer and
in-memory token cache, the immutable model/profile catalog and registry lookup
tables, the trace sink, the request-summary logger, and the configuration options. A
single shared instance of each SHALL be observed across all request scopes.

#### Scenario: Singleton infrastructure is identical across requests
- **WHEN** the authentication service (or any other shared-infrastructure singleton)
  is resolved from two different request scopes
- **THEN** both scopes observe the same single instance

#### Scenario: No per-request HTTP client
- **WHEN** requests are forwarded upstream
- **THEN** the outbound HTTP client is the shared singleton and is not allocated per
  request, so request forwarding does not create a new client (and its socket pool)
  for each request

### Requirement: Guaranteed detector execution order

The response-inspection stage SHALL run its detectors in a deterministic order that
is fixed by the order in which the detectors are registered, and SHALL NOT depend on
the container's `IEnumerable<T>` resolution order to establish it. The registration
order SHALL be materialized as an explicit per-detector order value assigned at
registration time, and the stage SHALL order detectors by that value before running
them. When two detectors would both act on the same event, the detector with the
lower order value SHALL take precedence.

#### Scenario: Detectors run in registration order
- **WHEN** detectors are registered in a given order and the stage inspects a response
- **THEN** the detectors are applied in that registration order, and the first
  non-passthrough action (by that order) is the one that takes effect

#### Scenario: Order is independent of resolution order
- **WHEN** the container returns the registered detectors in some order
- **THEN** the stage re-establishes the registration order from the explicit
  per-detector order values, so the applied order is unchanged even if the
  container's enumeration order differs

### Requirement: Captive-dependency safety is enforced at build time

The host service container SHALL be built with scope validation and build-time
validation enabled, so that a captive dependency — a singleton that depends on a
scoped service — is surfaced as a startup/build failure rather than silently
capturing a per-request instance for the process lifetime.

#### Scenario: Container build rejects a captive dependency
- **WHEN** the production service container is built with scope and build validation
  enabled
- **THEN** the build succeeds only if no singleton captures a scoped service, and a
  captive dependency causes the build to fail rather than leak silently
