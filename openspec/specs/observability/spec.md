# observability

## Purpose

How the bridge surfaces operationally-important events in its logs so an operator
can diagnose a request. Specifically: tool-leak detections are logged at the
detection point with enough detail to act on (which tool leaked, in which block,
what the guard did), and log records emitted while handling a request are
correlated to the request's trace id so an operator can move between a log line
and its trace JSON files.

## Requirements

### Requirement: Tool-leak detection is logged at the detection point

When the tool-leak guard detects a leak, the bridge SHALL emit exactly one
`Warning` log record at the point of detection identifying the leaked tool by
name, the content block type it was found in (`text` or `thinking`), and the
action taken (the error signal and whether it was delivered by injecting a
mid-stream event or by buffering). The record SHALL NOT contain the leaked
`<invoke>` markup or any tool parameter values.

#### Scenario: Leak Warning names tool, block, and action
- **WHEN** a leak is detected in a `text` block for a tool named `Read` with the
  `OverloadedError` signal on the streaming path
- **THEN** a single `Warning` is emitted naming `Read`, block `text`, signal
  `overloaded_error`, and stream delivery

#### Scenario: Leaked content is not logged
- **WHEN** any leak is detected
- **THEN** the emitted log record does not contain the `<invoke>` text or any
  parameter values from the leaked block

#### Scenario: Exactly one Warning per leak
- **WHEN** a leak is detected and the stream is aborted
- **THEN** exactly one `Warning`-level record describes the leak (the stage does
  not emit a second, redundant `Warning` for the same event)

### Requirement: Pipeline log records are correlated with the request trace id

Log records emitted while processing a request through the pipeline SHALL carry
the request's trace id (the same id used to name the request's trace JSON files
and the request-summary line), so an operator can move between a pipeline log
line and its trace artifacts. Records emitted outside a request context SHALL be
unaffected.

#### Scenario: A pipeline log line carries the trace id
- **WHEN** a stage or detector emits a log record during a request whose trace id
  is `T`
- **THEN** that record carries `T`, matching the `req#T` summary line and the
  `T-*.json` trace files for the same request

#### Scenario: The tool-leak Warning is traceable end-to-end
- **WHEN** a leak is detected during a request with trace id `T`
- **THEN** the detection `Warning` carries both the tool/block/action detail and
  the trace id `T`, so it can be tied to `T`'s trace JSON

#### Scenario: Non-request logs are not forced to carry an id
- **WHEN** a log record is emitted outside any request (e.g. the startup banner)
- **THEN** it renders without a trace id and is otherwise unchanged

### Requirement: Existing trace artifacts are preserved

This change SHALL NOT alter the request-summary line format's trace id
(`req#<traceId>`) or the naming of the per-request trace JSON files
(`<traceId>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`).

#### Scenario: Summary line and trace files unchanged
- **WHEN** a request completes
- **THEN** the summary line still begins with `req#<traceId>` and the four trace
  files are still named `<traceId>-*.json` with the same id
