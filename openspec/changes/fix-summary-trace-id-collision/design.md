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

## Decision: rename the hole, don't disable ActivityTracking

Two ways to stop the shadow:

| Option | Effect | Cost / risk |
|---|---|---|
| **A. Rename summary hole `{TraceId}` → `{ReqTrace}`** | Summary arg no longer collides; renders `s.TraceId`. Symmetric with the pipeline property name already in use. | Trivial, local, one template. Must confirm it doesn't collide with the pipeline `ReqTrace` *scope* on the summary line (it doesn't — the summary is emitted after the scope is pushed, but both carry the SAME id, so even if shadowed the value is identical). |
| B. Set `ActivityTrackingOptions = None` on the LoggerFactory | Removes the `TraceId`/`SpanId`/`ParentId` scopes globally | Broader blast radius: changes every log record, not just the summary; loses Activity ids some operators may rely on; a behaviour-wide switch to fix a one-line naming bug. |

**Choose A.** It's the minimal, local repair and it matches the naming the
pipeline path already settled on. Option B is a bigger hammer with side effects
unrelated to the defect.

One subtlety A must respect: on the summary line the `ReqTrace` scope property
is *also* in scope (the summary is logged inside the request). If we name the
hole `{ReqTrace}`, could the scope shadow it the way `TraceId` was shadowed?
Yes — but the scope's `ReqTrace` value **is** `s.TraceId` (same `BuildTraceId`
string), so the rendered id is correct either way. There is no third value to
be confused with. (If we wanted zero shadowing at all, a name used by neither
the framework nor the pipeline — e.g. `{ReqId}` — also works; `ReqTrace` is
preferred for symmetry and because the value is provably identical.)

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

The two defects are independent (rename fixes the value; scope-span fixes the
two boundary lines), but they're one coherent "make the summary + boundary lines
carry the real trace id" change and ship together.

## Consequence of the scope-span: the summary must not double its id

Widening the `ReqTrace` scope to cover the whole handler means the summary line
is now emitted *inside* the scope (previously it ran in `finally`, outside the
`try`-scoped push). `ReqTraceFormatEnricher` therefore sees a `ReqTrace` property
on the summary event and would add the `[<id>] ` bracket prefix — on top of the
summary's own self-rendered `req#<id>` — printing the id twice:
`[T] req#T responses …`. That regresses the "summary self-renders its own id"
design the pipeline-correlation change relied on.

Fix in the one presentation place: `ReqTraceFormatEnricher` skips the bracket
prefix when the message template already leads with `req#` (the summary owns its
id; every other in-request line, which does not self-render the id, still gets
the prefix). This keeps DATA/PRESENTATION separation intact and needs no change
to the summary logger or the endpoints. Guarded by a from-contract test that
renders the FULL production template (`{ReqTraceFmt}{Message:lj}`) under an
active `ReqTrace` scope and asserts the id occurs exactly once.

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
