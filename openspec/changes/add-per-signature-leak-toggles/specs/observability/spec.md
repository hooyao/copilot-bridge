## MODIFIED Requirements

### Requirement: Tool-leak detection is logged at the detection point

When the tool-leak guard detects a leak, the bridge SHALL emit exactly one
`Warning` log record at the point of detection — for both a leaked `<invoke>`
tool call and a leaked Claude Code control envelope (task notification,
teammate/channel/cross-session message, or tick) — identifying the leaked subject
(the tool name for an `<invoke>` leak, or the control-envelope subject such as
`task-notification` for a control-envelope leak), the content block type it was
found in (`text` or `thinking`), the action taken (the error signal and whether it
was delivered by injecting a mid-stream event or by buffering), and the exact
`Pipeline:Detectors:ToolLeakGuard:Signatures` configuration key that disables the
tripped signature (with the note that a restart is required to apply the change).
The record SHALL NOT contain the leaked markup or any tool parameter, envelope
child, or body values.

#### Scenario: Leak Warning names subject, block, and action
- **WHEN** a leak is detected in a `text` block — a tool named `Read`, or a
  `task-notification` control envelope — with the `OverloadedError` signal on the
  streaming path
- **THEN** a single `Warning` is emitted naming the subject (`Read` or
  `task-notification`), block `text`, signal `overloaded_error`, and stream
  delivery

#### Scenario: Leak Warning names the disable key and restart
- **WHEN** a leak is detected for a given signature
- **THEN** the single `Warning` also names the exact
  `Pipeline:Detectors:ToolLeakGuard:Signatures` key that disables that signature
  and notes that a restart is required for the change to take effect

#### Scenario: Leaked content is not logged
- **WHEN** any leak is detected
- **THEN** the emitted log record does not contain the leaked markup or any
  parameter, envelope child, or body values from the leaked block

#### Scenario: Exactly one Warning per leak
- **WHEN** a leak is detected and the stream is aborted
- **THEN** exactly one `Warning`-level record describes the leak (the stage does
  not emit a second, redundant `Warning` for the same event)
