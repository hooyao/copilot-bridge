## Why

When the tool-leak guard fires, the log gives an operator almost nothing to act
on, and no way to find the matching trace. Two same-root observability gaps:

1. **The detection point logs nothing useful.** `ToolLeakDetector` detects a leak
   and returns `Abort(...)`, but it has no logger; the only lines emitted are two
   hollow Warnings in `ResponseInspectionStage` that name the detector
   (`"ToolLeak"`) but not the leaked tool, the block type, or the action taken.
   The stage can't know those — only the detector/automaton does.
2. **Pipeline logs have no trace-id correlation.** `traceId` is a local variable
   in the endpoint; it names the four trace JSON files and the request-summary
   line, but `BridgeContext` doesn't carry it, so any log a stage or detector
   emits is orphaned — you can't jump from a log line to its `<traceId>-*.json`.

Together these mean a real tool-leak event produces a Warning with neither the
tool detail nor a trace id — you can't tell what leaked, in which turn, or open
the trace. Fixing both makes the tool-leak log line fully actionable.

## What Changes

- **Log the leak at the detection point.** `ToolLeakDetector` gets an
  `ILogger<ToolLeakDetector>` (threaded via `DetectorSetFactory`) and emits a
  single `Warning` when it detects a leak, naming: the **leaked tool name**, the
  **block type** (`text`/`thinking`), and the **action taken** (signal
  `overloaded_error`/`api_error`, delivery stream/buffer). It does **not** log the
  leaked `<invoke>` text or parameter values (no user content in the ordinary
  log).
- **Expose the matched tool name from the automaton.** `ToolLeakAutomaton` today
  exposes only a `bool Tripped`; it will also expose the tool name it matched so
  the detector can name it.
- **Correlate pipeline logs with the trace id.** Put `traceId` on
  `BridgeContext` (the endpoint sets it after `BuildTraceId`), and thread it into
  pipeline logging so a stage/detector log line carries `req#<traceId>` — the same
  id used by the trace files and the summary line. The mechanism (scope vs.
  explicit) is a design decision because the current Serilog `OutputTemplate`
  renders no scope properties and there is no `BeginScope` precedent in the repo.
- **Trim the now-redundant stage Warnings** in `ResponseInspectionStage` so the
  detector's richer line is the single source of truth (the stage may keep a
  Debug-level note).

## Capabilities

### New Capabilities
- `observability`: How the bridge surfaces operationally-important events —
  specifically, tool-leak detections are logged at the detection point with tool
  + block + action detail, and pipeline log lines are correlated to the
  per-request trace id so an operator can move between a log line and its trace
  JSON.

### Modified Capabilities
<!-- None — the tool-leak-guard capability's detection/delivery requirements are
     unchanged; this change adds observability requirements only. -->

## Impact

- **Code**: `ToolLeakAutomaton` (expose matched name), `ToolLeakDetector` (inject
  logger, log on detection), `DetectorSetFactory` (pass the logger),
  `ResponseInspectionStage` (trim redundant Warnings), `BridgeContext` (carry
  `traceId`), `ClaudeCodeMessagesEndpoint` (set it), and the pipeline logging path
  (`PipelineRunner` and/or the Serilog `OutputTemplate` in `SerilogBootstrapper`)
  to surface the id.
- **Constraints**: Native AOT — no reflection; the trace-id mechanism must be
  AOT-clean. Must not regress the existing `req#<traceId>` summary line or the
  `<traceId>-*.json` file naming. No change to detection behavior, request/response
  wire, or the `Pipeline:Detectors:ToolLeakGuard` config.
- **Tests**: contract-derived + mutation-checked — the detector emits a Warning
  naming tool/block/action (and not the leaked text) on detection; a
  pipeline-emitted log carries the trace id.
