## Context

The tool-leak guard (shipped 0.2.2-beta) detects a Copilot-served model leaking a
tool call as text and forces the client to retry. When it fires, the only log
output is two hollow Warnings in `ResponseInspectionStage`
(`"detector {Detector} aborted the stream"` / `"buffered abort (tool leak);
status={Status}"`) that name the detector but not the leaked tool, block type, or
action. And no pipeline log line carries the per-request trace id
(`BridgeIoSeq.BuildTraceId`), which names the four `<traceId>-*.json` trace files
and the `req#<traceId>` summary line — so a log line can't be tied back to its
trace. Current facts:

- `ToolLeakDetector` has no `ILogger`; detection detail (tool name, block type)
  lives only in it and `ToolLeakAutomaton` (which today exposes just `bool Tripped`).
- `traceId` is a local in `ClaudeCodeMessagesEndpoint`; `BridgeContext` doesn't
  carry it; `PipelineRunner.RunAsync` has an `ILogger` but opens no scope.
- Serilog is configured in `SerilogBootstrapper` with
  `OutputTemplate = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"`
  — **renders no scope/context properties** — and no `Enrich.FromLogContext()`.
  The MEL→Serilog bridge is `new SerilogLoggerProvider(dispose: false)`
  (`BridgeServiceCollectionExtensions.cs:46`), which does **not** forward MEL
  `BeginScope` state to Serilog's `LogContext` by default.
- Native AOT: no reflection; all logging must stay AOT-clean.

## Goals / Non-Goals

**Goals**
- A single, information-rich Warning at the moment of detection: leaked tool name
  + block type + action taken.
- Every pipeline-emitted log line (stages, detectors, the runner) carries
  `req#<traceId>`, matching the trace-file / summary id, so an operator can move
  between a log line and its trace JSON.
- AOT-clean; no regression to the `req#<traceId>` summary line or `<traceId>-*.json`
  naming.

**Non-Goals**
- No change to leak *detection* behavior, delivery modes, the wire, or the
  `Pipeline:Detectors:ToolLeakGuard` config.
- Not logging the leaked `<invoke>` text / parameter values in the ordinary log
  (user content stays in the opt-in trace files only).
- Not a general structured-logging overhaul — only the trace-id correlation and
  the tool-leak line.

## Decisions

### D1. Log inside `ToolLeakDetector` at the detection point
`ToolLeakDetector` gains an `ILogger<ToolLeakDetector>`, passed through
`DetectorSetFactory.Build` (the factory already constructs the detector
per-request; it takes the logger from DI). When the automaton trips, the detector
emits one `Warning` with structured fields: `{Tool}` (leaked tool name),
`{Block}` (`text`/`thinking`), `{Signal}` (`overloaded_error`/`api_error`),
`{Delivery}` (`stream`/`buffer`). It does **not** include the leaked text.

Rationale: the detector is the only place that knows the tool name and block
type; logging there (vs. one level out in the stage) is the only way to name
them. Alternative — bubble the detail out through `DetectionAction` so the stage
logs it — rejected: it widens the action type for a logging concern and still
splits detection from its log.

### D2. `ToolLeakAutomaton` exposes the matched tool name
Today `Tripped` is a `bool`. Add a `MatchedToolName` (string, set when the close
condition passes, alongside `_tripped = true`). The detector reads it for the
`{Tool}` field. Cheap; no behavior change — the name is already captured in
`_pendingName` at the moment of the match.

### D3. Delivery/block context comes from the detector, not the automaton
The automaton knows the tool + (implicitly) the block via its per-block reset,
but the *block type* and *delivery mode* are the detector's knowledge
(`content_block_start` type; `RequiresBuffering`/`_opts`). The detector composes
the full Warning from its own state + the automaton's `MatchedToolName`, so the
automaton stays a pure matcher.

### D4. Trace id on `BridgeContext`, surfaced via `LogContext` (as-built)
Add `string? TraceId` to `BridgeContext` (set by the endpoint after
`BuildTraceId`). Correlate log lines **without** each call site hand-formatting
the id.

**As-built refinement (differs from the initial "scope in `PipelineRunner`"
sketch):** the streaming tool-leak detector's `InspectEvent` does **not** run
during `PipelineRunner.RunAsync` — response stages only *wrap* the `EventStream`
there; the actual enumeration (where the detector fires and logs) happens later,
in the **endpoint's relay loop**, after `RunAsync` returns. A scope opened in the
runner would already be disposed by then — exactly missing the tool-leak line. So
the correlation scope is opened in **each pipeline-driving endpoint**
(`ClaudeCodeMessagesEndpoint` for `/cc` and `CodexResponsesEndpoint` for
`/codex` — both own `RunAsync` and a relay loop over the shared pipeline):

1. **Push the property** at the top of the endpoint's `try`:
   `using var _ = Serilog.Context.LogContext.PushProperty("ReqTrace", $"req#{traceId} ")`.
   This covers request stages, the strategy, the response-stage wrappers, AND the
   relay-loop enumeration where the streaming detector emits.
2. **Flow + render.** Add `Enrich.FromLogContext()` to both Serilog loggers in
   `SerilogBootstrapper` and prepend `{ReqTrace}` to `OutputTemplate`:
   `"{Timestamp:HH:mm:ss.fff} [{Level:u3}] {ReqTrace}{Message:lj}{NewLine}{Exception}"`.
   The value already carries the trailing space, so present → `req#<id> message`,
   absent → `message` (Serilog drops the missing-property token). Non-request
   lines (startup banner) are unaffected.

**Property name `ReqTrace`, not `TraceId`:** `RequestSummaryLogger` already emits
a message-template property named `TraceId`; reusing that name in the scope would
collide. `ReqTrace` is distinct, and the summary line keeps rendering its own
`req#<id>` from its message.

Using Serilog's `LogContext.PushProperty` directly (rather than MEL `BeginScope`)
is the robust path: `Enrich.FromLogContext()` attaches it to every Serilog event
in the async scope regardless of how the MEL→Serilog bridge treats scopes, and it
is AOT-clean (no reflection). Verified empirically: a `TraceIdLogCorrelationTests`
unit test asserts the pushed property is enriched onto in-scope events and absent
outside; a real startup confirms non-request lines render cleanly.

Rationale for a scope over "hand-format each log": the scope makes *all* current
and future request-path logs correlated for free; hand-formatting only tags lines
someone remembers to touch. The trade is one new logging pattern (documented
here) + a two-line Serilog config change.

### D5. Trim the redundant stage Warnings
With the detector emitting the authoritative line, `ResponseInspectionStage`'s two
abort Warnings become redundant. Downgrade them to `Debug` (keep a terse
"aborted (tool leak)" breadcrumb for stage-level tracing) so there's exactly one
Warning per leak, now carrying both the tool detail (D1) and the trace id (D4).

## Risks / Trade-offs

- **MEL `BeginScope` may not reach Serilog with the current provider ctor.** →
  Mitigation: D4's fallback (push onto Serilog `LogContext` from the runner);
  verified during apply with a real log line, not assumed.
- **`OutputTemplate` change touches every log line's format** (adds a `{TraceId}`
  slot). → Mitigation: Serilog renders a missing property as empty, so non-scoped
  lines only gain a space; verify the banner/summary lines still read cleanly.
- **Scope allocates a dictionary per request.** → Negligible (one small dict per
  request, only on the pipeline path); not on any hot inner loop.
- **Regressing the `req#<traceId>` summary line.** → It's emitted by
  `RequestSummaryLogger` independently of the scope; unchanged. The scope adds the
  id to *other* lines, doesn't move the summary.

## Migration Plan

Additive and internal — no config, wire, or API change. Rollback is reverting the
commit; the logging change has no persisted state. Ship behind no flag (logging
detail is always desirable), but the `OutputTemplate` change is the one
externally-visible effect (log format) — call it out in the PR.

## Open Questions

- Does `new SerilogLoggerProvider(dispose: false)` forward MEL scope dictionaries
  to Serilog `LogContext` as-is, or is an explicit `LogContext.PushProperty`
  needed? Resolved empirically in apply (D4 fallback covers both).
