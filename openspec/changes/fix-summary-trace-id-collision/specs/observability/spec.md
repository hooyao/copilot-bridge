# observability

## MODIFIED Requirements

### Requirement: Existing trace artifacts are preserved

The request-summary line SHALL begin with `req#<traceId>` where `<traceId>` is
the request's `BuildTraceId` value (`{yyyyMMdd-HHmmss}-{seq:D4}`) — the SAME id
that names the per-request trace JSON files
(`<traceId>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`) and
prefixes the pipeline log lines. The rendered `req#` id SHALL NOT be shadowed by
any framework-injected log-scope property (notably the ambient
`Activity.TraceId`), so it is never a 32-character hex Activity id. The summary
line self-labels its id via `req#<traceId>` and SHALL NOT additionally carry the
`[<traceId>] ` bracket prefix the other in-request lines use, so its id appears
exactly once.

#### Scenario: Summary line and trace files share one id

- **WHEN** a request with trace id `T` (shape `yyyyMMdd-HHmmss-nnnn`) completes
- **THEN** the summary line begins with `req#T`, the four trace files are named
  `T-*.json`, and every pipeline log line for that request carries `T` — all
  three surfaces showing the identical `T`

#### Scenario: Framework Activity id does not shadow the summary id

- **WHEN** the host runs with the framework-default activity tracking that
  injects an ambient log-scope property named `TraceId` (the current
  `Activity.TraceId`) into in-request log records
- **THEN** the summary line's `req#` id still renders the request's
  `BuildTraceId` value, NOT the ambient Activity hex id

#### Scenario: Summary id is not doubled by the request scope

- **WHEN** the summary line is emitted inside the request's `ReqTrace` scope (the
  same scope that correlates the enter/exit boundary lines)
- **THEN** its trace id appears exactly once — self-rendered as `req#T`, with no
  redundant `[T] ` bracket prefix

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
