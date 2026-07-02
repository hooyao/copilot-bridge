# Tasks — Fix summary-line trace id collision

## 1. Reproduce the collision as a failing contract test (RED first)

- [x] 1.1 Add a unit test that builds a `RequestSummaryLogger` over a logger
  configured with the **framework-default** `ActivityTrackingOptions`
  (`TraceId | SpanId | ParentId`) inside an active `Activity`, so an ambient
  `TraceId` scope property is present — reproducing the real host condition.
  Assert the rendered summary line's `req#` id equals the `RequestSummary.TraceId`
  (`BuildTraceId` shape `yyyyMMdd-HHmmss-nnnn`) and does NOT match a 32-hex
  Activity id. Confirm it FAILS on current code (renders the hex).
- [x] 1.2 State the contract in the test as a comment: *given a summary whose
  TraceId is `T`, the rendered line must begin `req#T`, because that id must
  match the trace files and pipeline lines — regardless of any ambient
  framework `TraceId` scope.*

## 2. Break the collision (summary value)

- [x] 2.1 In `RequestSummaryLogger.cs` rename the message-template hole
  `{TraceId}` → `{ReqTrace}` (arg still `s.TraceId`). No other template hole
  changes; the textual shape `req#<id> …` is unchanged.
- [x] 2.2 Test 1.1 now GREEN. Mutation-check: revert the hole name → RED again.
- [x] 2.3 Confirm the existing `RequestSummaryFormatterTests` still pass (they
  assert the human-readable line shape) — adjust only if they pin the literal
  hole name rather than the rendered output.

## 3. Trace the endpoint boundary (enter/exit)

- [x] 3.1 In `CodexResponsesEndpoint.cs` move the
  `using var _traceScope = LogContext.PushProperty("ReqTrace", traceId)` up so it
  precedes the `endpoint … enter` log and encloses the `try`/`finally`, so both
  boundary lines and the summary are inside the scope.
- [x] 3.2 Same move in `ClaudeCodeMessagesEndpoint.cs`.
- [x] 3.3 Verify `ClaudeCodeCountTokensEndpoint.cs`: ensure its enter/exit/summary
  are likewise scope-covered (apply the same move if it has boundary lines).
- [x] 3.4 Add/extend a test asserting an `endpoint … enter` (or exit) record
  emitted through the endpoint carries the `ReqTrace` property with the request's
  trace id. Mutation-check: narrow the scope back inside `try` → the enter/exit
  assertion goes RED. (If driving the full endpoint in a unit test is
  impractical, assert the narrower invariant: a record logged before the scope
  push has no `ReqTrace`, one after does — the structural guarantee the move
  provides.)
- [x] 3.5 Side effect of 3.1/3.2: the summary line now runs inside the scope, so
  the enricher would prefix it with `[<id>] ` on top of its own `req#<id>`,
  doubling the id. Fix in `ReqTraceFormatEnricher`: skip the bracket when the
  message template leads with `req#`. RED-first test renders the full
  `{ReqTraceFmt}{Message:lj}` template under an active scope and asserts the id
  occurs once; mutation-check (remove the skip → id doubled → RED).

## 4. Verification

- [x] 4.1 `dotnet test --filter "Category!=Integration"` green; build clean (0 warnings).
- [x] 4.2 Real run on a non-8765 port (e.g. `serve --port 18765`): drive one
  `/codex/responses` and one `/cc/v1/messages` request; confirm in the log that
  for each request the `endpoint enter`, every pipeline `[…]` line, the `req#`
  summary, and `endpoint exit` all show the SAME `yyyyMMdd-HHmmss-nnnn` id, and
  no line shows a 32-hex id.
- [x] 4.3 AOT publish per the CLAUDE.md PowerShell block; no new IL/trim warnings.
- [x] 4.4 Fold the durable fact into `docs/` diagnostics: the summary line's
  trace-id hole must not be named `TraceId` (collides with the framework
  `Activity.TraceId` scope); the request scope spans the whole handler so
  enter/exit are correlated too.
