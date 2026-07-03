# observability

## MODIFIED Requirements

### Requirement: Existing trace artifacts are preserved

The request-summary line SHALL carry the request's trace id (the `BuildTraceId`
value `{yyyyMMdd-HHmmss}-{seq:D4}`) — the SAME id that names the per-request
trace JSON files
(`<traceId>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`) and that
prefixes the pipeline and enter/exit lines — rendered EXACTLY ONCE and by the
SAME mechanism as every other in-request line: the `[<traceId>] ` prefix produced
from the `ReqTrace` log-context property. The summary message itself SHALL NOT
self-render the id (no `req#<traceId>` hole in the template). The rendered id
SHALL never be a 32-character hex Activity id.

Rationale: rendering the id only through the shared prefix, and never as a hole
in the summary message, structurally rules out both historical failure modes — a
`{TraceId}` message hole shadowed by the framework's ambient `Activity.TraceId`
scope (nothing to shadow), and a self-rendered id doubled by the prefix once the
summary sits inside the request scope (nothing to double).

#### Scenario: Summary line and trace files share one id

- **WHEN** a request with trace id `T` (shape `yyyyMMdd-HHmmss-nnnn`) completes
- **THEN** the summary line carries `T` via its `[T] ` prefix, the four trace
  files are named `T-*.json`, and every pipeline log line for that request
  carries `T` — all surfaces showing the identical `T`

#### Scenario: Summary id is rendered exactly once, not doubled

- **WHEN** the summary line is emitted inside the request's `ReqTrace` scope (the
  same scope that correlates the enter/exit and pipeline lines)
- **THEN** its trace id appears exactly once — as the `[T] ` prefix — and the
  summary message contains no additional `req#T` self-label

#### Scenario: Framework Activity id cannot reach the summary id

- **WHEN** the host runs with the framework-default activity tracking that
  injects an ambient log-scope property named `TraceId` (the current
  `Activity.TraceId`) into in-request log records
- **THEN** the summary line's id is still `T` (from the `ReqTrace` prefix) and
  never the 32-char hex Activity id — the summary message has no `{TraceId}` hole
  for the ambient scope to shadow

#### Scenario: Every summary-emitting endpoint carries the id, including count_tokens

- **WHEN** a request to an endpoint that emits a summary but runs no pipeline and
  no enter/exit lines (`/cc/v1/messages/count_tokens`) completes
- **THEN** its summary line still carries `T` via the `[T] ` prefix — the endpoint
  establishes the `ReqTrace` scope for its handler so the summary is prefixed like
  every other in-request line, not left id-less

## ADDED Requirements

### Requirement: Endpoint boundary lines carry the request trace id

A pipeline-driving endpoint SHALL emit its `endpoint … enter` and
`endpoint … exit` diagnostic lines carrying the request's trace id, matching the
pipeline lines and the summary line, so an operator can pair a request's start
and end and tie them to its trace artifacts even when concurrent requests
interleave. This applies to every endpoint that emits those boundary lines
(`/cc/v1/messages`, `/codex/responses`). The `count_tokens` passthrough emits no
`enter`/`exit` lines and is out of scope for this requirement (its summary line
is still governed by the summary-id requirement above).

#### Scenario: Enter and exit lines are trace-correlated

- **WHEN** a request with trace id `T` is handled end-to-end
- **THEN** both its `endpoint … enter` line and its `endpoint … exit` line carry
  `T`, identical to the id on that request's pipeline and summary lines

#### Scenario: Interleaved requests remain pairable

- **WHEN** two requests overlap in time so that one request's `exit` line is
  written after another request's lines
- **THEN** each `enter`/`exit` line carries its own request's trace id, so the
  pair for a given request is unambiguous from the id alone

#### Scenario: Non-request boundary logs are unaffected

- **WHEN** a log record is emitted outside any request (e.g. the startup banner)
- **THEN** it renders without a trace id, unchanged by this requirement
