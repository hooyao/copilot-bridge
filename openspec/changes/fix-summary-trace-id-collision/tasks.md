# Tasks — Fix summary-line trace id collision

## 1. Reproduce the collision as a failing contract test (RED first)

- [x] 1.1 Add a unit test that builds a `RequestSummaryLogger` over a real
  `SerilogLoggerProvider` with the **framework-default** `ActivityTrackingOptions`
  (`TraceId | SpanId | ParentId`) inside an active `Activity`, so an ambient
  `TraceId` scope property is present — reproducing the real host condition.
  Confirm the original `req#{TraceId}` template renders the 32-hex Activity id
  (the bug), then that the fixed design renders the `BuildTraceId` id exactly once
  via the enricher prefix and never the hex.
- [x] 1.2 State the contract in the test as a comment: *the summary line must
  carry the request's `BuildTraceId` id exactly once, by the same `[id] ` prefix
  as every in-request line, and never the ambient framework Activity id.*

## 2. Eliminate the collision (remove the id from the summary message)

- [x] 2.1 In `RequestSummaryLogger.cs` drop the `req#{TraceId}` hole and its
  `s.TraceId` argument entirely. The id now comes only from the shared
  `ReqTraceFormatEnricher` `[<id>] ` prefix — the same as pipeline/enter/exit.
- [x] 2.2 Remove the now-dead `RequestSummary.TraceId` field and the `TraceId =`
  assignments at all three `new RequestSummary { … }` sites (Codex, messages,
  count_tokens). The compiler confirms nothing else read it.
- [x] 2.3 Delete the enricher's `req#`-lead special-case (added no longer needed):
  every in-request line — summary included — gets the prefix uniformly.
- [x] 2.4 Update `RequestSummaryFormatterTests` (drop `TraceId =` initializers and
  the `ReqTrace` property assertion — the summary no longer renders an id
  property). Full suite green.

## 3. Trace the endpoint boundary (enter/exit)

- [x] 3.1 In `CodexResponsesEndpoint.cs` move the
  `using var _traceScope = LogContext.PushProperty("ReqTrace", traceId)` up so it
  precedes the `endpoint … enter` log and encloses the `try`/`finally`, so both
  boundary lines and the summary are inside the scope.
- [x] 3.2 Same move in `ClaudeCodeMessagesEndpoint.cs`.
- [x] 3.3 Verify `ClaudeCodeCountTokensEndpoint.cs`: it emits no enter/exit lines
  and no pipeline stages, so no scope move is needed; its summary is fixed by §2.
- [x] 3.4 Drive the REAL `HandleAsync` (Codex) with a real Serilog logger behind
  the endpoint-tag `ILogger` and assert the actual `endpoint enter`/`exit` records
  carry the `ReqTrace` id (BuildTraceId shape). Mutation-check: move the scope
  back inside `try` → the enter/exit assertion goes RED.

## 4. Verification

- [x] 4.1 `dotnet test --filter "Category!=Integration"` green; build clean (0 warnings).
- [x] 4.2 Real run on a non-8765 port: drive one `/codex/responses` and one
  `/cc/v1/messages` request; confirm for each request the `endpoint enter`, every
  pipeline `[…]` line, the summary, and `endpoint exit` all show the SAME
  `[yyyyMMdd-HHmmss-nnnn]` id, no line shows a 32-hex id, and no line contains
  `req#`.
- [x] 4.3 AOT publish per the CLAUDE.md PowerShell block; no new IL/trim warnings.
- [x] 4.4 Fold the durable fact into `docs/`/README diagnostics: the summary line
  carries its trace id via the shared `[id] ` prefix (no `req#` self-label), and
  the request scope spans the whole handler so enter/exit are correlated too.
