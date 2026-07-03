# Design — Fix summary-line trace id collision

## Context

Trace correlation in this bridge rests on one id per request, `BuildTraceId` =
`{yyyyMMdd-HHmmss}-{seq:D4}`, appearing at every diagnostic surface:

- the four audit files `<traceId>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`
- every pipeline/stage/detector log line, via a Serilog `LogContext` property
  named `ReqTrace` (rendered by `ReqTraceFormatEnricher` as `[<id>] `)
- the per-request INFO summary line, as `req#<traceId>`

The #18 change ("pipeline trace-id correlation") wired up the `ReqTrace` scope
and — per its own design notes — deliberately avoided naming that scope property
`TraceId` because MEL already injects a property of that name. It correctly
fixed the pipeline lines. It did **not** touch the summary line, on the (wrong)
assumption that the summary "keeps rendering its own `req#<id>` from its
message." This change repairs that last surface.

## The collision, precisely

`WebApplication.CreateSlimBuilder()` configures MEL logging with the framework
default `ActivityTrackingOptions = TraceId | SpanId | ParentId`. For every log
record emitted inside an HTTP request's Activity, MEL adds scope properties
`TraceId`, `SpanId`, `ParentId`. Through `SerilogLoggerProvider` +
`Enrich.FromLogContext()` these become Serilog event properties.

Serilog renders a message template hole `{TraceId}` by name. When both the
message template argument list *and* an ambient scope define `TraceId`, the
scope property wins for `{Message:lj}` rendering. So:

```
template:  "req#{TraceId} ..."   arg: s.TraceId = "20260702-092136-0250"
scope:     TraceId = "b77a7e803f295e88031e91d8ab74e4de"   (Activity.TraceId)
rendered:  "req#b77a7e803f295e88031e91d8ab74e4de ..."     ← scope shadowed the arg
```

Pipeline lines escape this because their id lives under a *differently named*
property (`ReqTrace`), not `TraceId`.

### Why this is the id we see

- 32 lowercase hex chars = W3C trace-id shape (16 bytes).
- Distinct per request in the log (0249/0250/0251 each differ) → per-Activity,
  not a process constant.
- No bridge code path can emit that shape: `BuildTraceId` is date-seq only, and
  a repo-wide search for `Guid…"N"` / `Activity` / any 32-hex generator in the
  request path returns nothing. It is injected by the framework, not authored.

## Decision: remove the id from the summary message; render it only via the shared prefix

The id already reaches every *pipeline* line through one mechanism: the endpoint
pushes `ReqTrace` onto the log context, and `ReqTraceFormatEnricher` renders it as
the `[<id>] ` prefix. The summary line was the lone exception — it self-rendered
`req#<id>` in its own message, a historical artifact of once being logged in
`finally`, outside the request scope, where the prefix couldn't reach it.

Options considered:

| Option | Effect | Cost / risk |
|---|---|---|
| **A. Remove the id from the summary message; let the enricher prefix supply it** (chosen) | Summary carries the id via the SAME `[<id>] ` prefix as every in-request line. No id hole in the message. | The collision is impossible by construction (nothing to shadow); no enricher special-case; `RequestSummary.TraceId` becomes dead and is deleted. The `req#` grep-anchor is lost (mitigated below). |
| B. Rename the summary hole `{TraceId}` → `{ReqTrace}` | Summary arg stops colliding, still self-renders `req#<id>` | Once the scope is widened to correlate enter/exit, the summary sits inside it and the enricher ALSO prefixes it → the id prints twice (`[T] req#T`). Requires an enricher special-case ("skip the prefix when the message leads with `req#`") — a patch on a patch. Keeps a redundant second render path for the id. |
| C. Set `ActivityTrackingOptions = None` globally | Removes the `TraceId`/`SpanId`/`ParentId` scopes | Behaviour-wide switch to fix a local logging bug; loses Activity ids some operators may rely on. |

**Choose A.** It removes the whole class of problem — a message that renders its
own id can be shadowed (by the framework `TraceId` scope) or doubled (by the
request-scope prefix); a message that renders NO id can be neither. One id, one
render site (the enricher), uniform across enter / pipeline / summary / exit.
Option B only relocates the collision and then needs a second special-case to
undo the doubling it introduces.

**Grep anchor.** `req#` is gone, so `grep req#` no longer isolates the summary.
The summary is still distinguishable: it is the one INFO line per request that
leads with the kind word (`messages` / `responses` / `count_tokens`) after the
`[id] ` prefix and carries `status=` / `usage=` / `duration_ms=`. If a dedicated
anchor is later wanted, add a stable literal token to the summary template (e.g.
a leading `summary`) — but that is a separate cosmetic choice, not needed to fix
the id.

## Decision: scope spans the whole handler, including enter/exit

Today (both endpoints):

```
seq/traceId computed
endpointLog.LogDebug("endpoint … enter …")     // (1) BEFORE scope → no id
try {
    using var _traceScope = PushProperty("ReqTrace", traceId)   // scope starts here
    … pipeline (correct id) …
}                                               // using disposed at try's close
finally {
    summaryLogger.Log(summary)                  // (2) summary — see collision above
    endpointLog.LogDebug("endpoint … exit …")   // (3) AFTER dispose → no id
}
```

Move the `using` up so it encloses (1), the `try`, and the `finally`:

```
seq/traceId computed
using var _traceScope = PushProperty("ReqTrace", traceId)   // scope starts first
endpointLog.LogDebug("endpoint … enter …")     // now carries [id]
try { … }
finally {
    summaryLogger.Log(summary)                  // carries id (+ hole renamed)
    endpointLog.LogDebug("endpoint … exit …")   // now carries [id]
}
```

`LogContext.PushProperty` is async-local and cheap; widening its span to the
whole method is free and makes every line of the request — boundary included —
grep-correlatable. Non-request lines are unaffected (no scope, no id), preserving
the existing "startup banner renders clean" behaviour.

The two changes compose cleanly: dropping the id from the summary message
(above) means widening the scope over the summary does NOT double it — the
summary now renders the id only through the shared prefix, exactly like the
enter/exit and pipeline lines. Had the summary kept a self-rendered `req#<id>`,
the widened scope would have printed the id twice (`[T] req#T …`) and forced an
enricher special-case; removing the id from the message avoids that entirely.

## Risks / trade-offs

- **Someone greps for the old hex `req#` id.** Unlikely — the hex was never a
  documented/stable id and couldn't be tied to trace files anyway (that was the
  bug). The repaired id is the one the docs already promise.
- **Test that reproduces the collision is over-specified.** Mitigate by
  configuring the test logger with the *framework default* ActivityTracking (not
  a hand-built scope named `TraceId`), so it fails for the real reason and stays
  honest if the framework changes the property name.
- **AOT.** No new reflection; `LogContext`/enricher path already ships in v0.3.0
  under AOT. Publish sanity-check per CLAUDE.md still required.

## Migration

None. Log-text repair only; no config keys, wire bytes, file names, or audit
JSON change.
