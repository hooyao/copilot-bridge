# Implementation Tasks

## 1. Automaton: expose the matched tool name

- [x] 1.1 Add `MatchedToolName` (string?, null until tripped) to `ToolLeakAutomaton`; set it alongside `_tripped = true` when the close condition passes (the name is already in `_pendingName` at that point). Reset it in `Reset(...)`.
- [x] 1.2 Unit-test: on a genuine leak the automaton exposes the matched name; on a non-leak it stays null. Mutation-check (break the assignment → test red).

## 2. Detector: log at the detection point

- [x] 2.1 Inject `ILogger<ToolLeakDetector>` into `ToolLeakDetector`; thread it through `DetectorSetFactory.Build` (resolve from DI, pass to the per-request detector).
- [x] 2.2 On detection, emit ONE `Warning` with structured fields `{Tool}` (from `MatchedToolName`), `{Block}` (text/thinking, from the current block scope), `{Signal}` (overloaded_error/api_error), `{Delivery}` (stream/buffer, from `RequiresBuffering`). Do NOT include the leaked text.
- [x] 2.3 Downgrade the two `ResponseInspectionStage` abort Warnings to `Debug` (keep a terse breadcrumb) so there is exactly one Warning per leak.

## 3. Trace-id correlation

- [x] 3.1 Add `string? TraceId` to `BridgeContext`; set it in `ClaudeCodeMessagesEndpoint` right after `BuildTraceId(seq, utc)`, before `runner.RunAsync`.
- [x] 3.2 Open a `LogContext.PushProperty("ReqTrace", "req#<traceId> ")` scope in `ClaudeCodeMessagesEndpoint` (NOT `PipelineRunner` — the streaming detector fires in the endpoint's relay loop *after* `RunAsync` returns, so a runner scope would be disposed too early). The scope covers request stages, the strategy, and the relay-loop enumeration.
- [x] 3.3 Make the scope visible in the text log: add `Enrich.FromLogContext()` to both loggers in `SerilogBootstrapper` and prepend `{ReqTrace}` to `OutputTemplate` (distinct from the summary's `{TraceId}` message property to avoid collision). Verified empirically: unit test proves the enricher flows the property; a real startup confirms non-request lines render cleanly.

## 4. Tests (contract-derived, mutation-checked)

- [x] 4.1 Detector emits a Warning naming tool + block + action on detection; assert the leaked `<invoke>`/parameter text is NOT in the record. Mutation-check.
- [x] 4.2 Exactly one Warning per leak (stage no longer emits a second).
- [x] 4.3 A pipeline-emitted log record carries the trace id from `BridgeContext.TraceId` (scope present). Mutation-check (remove the scope → id absent).
- [x] 4.4 Regression: the `req#<traceId>` summary line and `<traceId>-*.json` file naming are unchanged.

## 5. Verification & docs

- [x] 5.1 `dotnet test --filter "Category!=Integration"` green; build clean (0 warnings).
- [x] 5.2 Real startup: run the bridge on a non-8765 port, confirm normal log lines now carry `req#<traceId>` where applicable and the startup banner still reads cleanly. If feasible, drive a simulated leak and confirm the single tool-naming Warning carries the id.
- [x] 5.3 AOT sanity: publish per the CLAUDE.md PowerShell block; no new IL/trim warnings from the logging/scope change.
- [x] 5.4 Fold the durable fact (pipeline logs are trace-correlated via a runner scope; tool-leak logs at the detection point) into `docs/` where diagnostics/logging is documented.
