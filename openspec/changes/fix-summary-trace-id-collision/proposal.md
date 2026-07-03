# Fix summary-line trace id collision + untraced endpoint enter/exit

## Why

The per-request INFO summary line is supposed to begin with `req#<traceId>`
where `<traceId>` is the same `BuildTraceId` value (`yyyyMMdd-HHmmss-seq`) that
names the request's four trace JSON files and prefixes every pipeline log line.
The archived `observability` spec asserts exactly this ("the summary line still
begins with `req#<traceId>`"). **It doesn't.** In a real v0.3.0 run the summary
line renders a different id than the pipeline lines for the same request:

```
17:21:36.291 [DBG] [20260702-092136-0250] adapter ResponsesToIrInbound(T1): ...   ← pipeline: correct id
   ... entire pipeline uses [20260702-092136-0250] ...
17:22:55.455 [INF] req#b77a7e803f295e88031e91d8ab74e4de responses ... duration_ms=79169   ← summary: WRONG id
```

The `req#` id (`b77a7e80…`, a 32-char lowercase hex) is **not** produced by any
bridge code — `BuildTraceId` can only emit `yyyyMMdd-HHmmss-seq`. It is the
ASP.NET Core per-request **`Activity.TraceId`** (W3C trace id). It changes every
request (seq 0249 → `6ebfb62a…`, 0250 → `b77a7e80…`, 0251 → `4b49f76d…`),
confirming its per-request-Activity origin.

**Root cause — a message-template hole name collides with a framework log-scope
property.** `WebApplication.CreateSlimBuilder()` leaves MEL's default
`ActivityTrackingOptions = TraceId | SpanId | ParentId` on, which injects the
current Activity's trace id into every in-request log record as a scope property
literally named **`TraceId`**. The summary template
(`RequestSummaryLogger.cs`) writes:

```csharp
"req#{TraceId} {Kind} ...", s.TraceId, ...
```

The hole is named `{TraceId}`. When Serilog renders `{Message:lj}`, the
framework's `TraceId` scope property shadows the template's own `{TraceId}`
argument, so `req#` prints the Activity hex instead of `s.TraceId`. The pipeline
lines are correct only because the trace-correlation change (#18) deliberately
named its scope property `ReqTrace` to dodge this very collision — but it left
the summary line's own `{TraceId}` hole unchanged, and its design note wrongly
assumed "the summary line keeps rendering its own `req#<id>` from its message."

**Secondary defect — `endpoint enter`/`endpoint exit` carry no trace id.** Both
`/cc` and `/codex` endpoints emit `endpoint … enter` *before* the `ReqTrace`
scope is pushed and `endpoint … exit` in the `finally` *after* the `using`
scope is disposed. With no id and no ordering guarantee, a long request's
`enter` and `exit` lines interleave with other requests and cannot be paired by
eye (in the sample, seq 0250's `exit` prints only after seq 0251 fully
completes).

## What Changes

- **Eliminate the collision by removing the id from the summary message.** The
  summary template no longer self-renders the id (`req#{TraceId} …` → drop it and
  the `s.TraceId` argument). The id now reaches the summary the same way it
  reaches the pipeline and enter/exit lines: the `[<traceId>] ` prefix the
  `ReqTraceFormatEnricher` builds from the `ReqTrace` log-context property. With
  no id hole in the message there is nothing for the framework's ambient
  `Activity.TraceId` scope to shadow — the collision is structurally impossible,
  not merely renamed around. `RequestSummary.TraceId` becomes dead state and is
  removed.
- **Trace the endpoint boundary.** Move the `ReqTrace` scope so it wraps the
  whole handler body — pushed before the `endpoint … enter` line and still active
  when the `finally` emits `endpoint … exit` — so the boundary lines, pipeline
  lines, and summary all carry the same id via the one shared prefix. Applies to
  both endpoints.
- **Guard the contract with from-contract tests.** Under a logger configured with
  the framework's default `ActivityTrackingOptions` (the real host condition) and
  an active `ReqTrace` scope, assert the rendered summary line carries the
  `BuildTraceId`-shaped id exactly once (via the `[id] ` prefix), never a 32-hex
  Activity id, and contains no `req#` self-label. Drive the real endpoint
  `HandleAsync` to assert the actual enter/exit lines carry the id
  (mutation-check: narrow the scope back inside `try` → RED).

Out of scope: changing `ActivityTrackingOptions` globally (a broader,
behaviour-wide switch), the trace-file naming, the audit JSON, or any
request/response wire bytes.

## Impact

- Affected specs: `observability` (repairs the existing "Existing trace artifacts
  are preserved" requirement, which is currently violated).
- Affected code: `RequestSummaryLogger.cs` (drop the id hole), `RequestSummary.cs`
  (remove the dead `TraceId` field), `ReqTraceFormatEnricher.cs` (no special-case;
  every in-request line gets the prefix), `CodexResponsesEndpoint.cs` +
  `ClaudeCodeMessagesEndpoint.cs` + `ClaudeCodeCountTokensEndpoint.cs` (scope span
  / drop the `TraceId =` assignment); new + updated unit tests.
- The one operator-visible change: the summary line no longer starts with `req#`;
  its id is now the same `[<traceId>] ` prefix every other in-request line uses.
- No behaviour change to detection, routing, translation, or wire bytes. AOT: no
  reflection introduced; Serilog `LogContext` is already AOT-safe.
