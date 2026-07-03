## ADDED Requirements

### Requirement: Per-signature detection toggles

The guard SHALL expose an independent enable switch for each leak signature under
`Pipeline:Detectors:ToolLeakGuard:Signatures`, with the boolean keys `Invoke`,
`TaskNotification`, `TeammateMessage`, `Channel`, `CrossSessionMessage`, and
`Tick`, each defaulting to true. A signature whose switch is false SHALL NOT be
evaluated — its matcher is not constructed and the corresponding markup passes
through unchanged — while every still-enabled signature continues to trip the guard
normally. This is a false-positive escape hatch for a session that legitimately
echoes the markup (for example a user discussing how `<invoke>` tool-use or a
`<task-notification>` envelope works, whose faithful sample reply would otherwise
be caught).

The per-signature switches SHALL be read when the detector is constructed. Because
the switches are captured at startup, a change to a switch SHALL require a restart
of the bridge to take effect. The detector SHALL compute its enabled-signature set
per request from the current options and SHALL NOT retain a process-wide cache of
that set, so that the only change needed to make a flipped switch take effect
without a restart is sourcing the options from a live (reloading) provider — with
no change to detection logic.

When a leak is detected, the retry error surfaced to the client SHALL name the
tripped signature and the exact `Pipeline:Detectors:ToolLeakGuard:Signatures` key
that disables it, and SHALL note that a restart is required after changing the
switch.

#### Scenario: Disabled signature passes through, siblings still trip
- **WHEN** `Signatures:Invoke` is false and a response contains a closed,
  in-tools, unfenced `<invoke>` leak
- **THEN** the block is NOT classified as a leak and passes through unchanged,
  while a `<task-notification>` (or any other still-enabled signature) in the same
  configuration is still classified as a leak

#### Scenario: All signatures default on
- **WHEN** no `Signatures` values are overridden
- **THEN** all six signatures are enabled and behave exactly as when the sub-block
  is absent

#### Scenario: Retry error names the signature, disable key, and restart
- **WHEN** a leak is detected for a given signature
- **THEN** the retry error delivered to the client names that signature, names the
  exact `Pipeline:Detectors:ToolLeakGuard:Signatures` key that disables it, and
  states that a restart is required for the change to take effect

## MODIFIED Requirements

### Requirement: Configuration and default-off behavior

The guard SHALL be configured under `Pipeline:Detectors:ToolLeakGuard` with keys
`Enabled` (default true), `PreserveStream` (default true), `Signal` (default
`OverloadedError`), `ScanThinking` (default true), `MaxScanChars` (default 10000),
and a `Signatures` sub-block of per-signature toggles (see the Per-signature
detection toggles requirement; all default true). When `Enabled` is false the
guard SHALL be a no-op that performs no scanning, no accumulation, and no
allocation on the response path.

#### Scenario: Disabled guard is inert
- **WHEN** `Enabled` is false and a response contains a leak
- **THEN** the response is relayed unchanged with no scanning or allocation, as
  if the guard were absent

#### Scenario: Defaults
- **WHEN** no `Pipeline:Detectors:ToolLeakGuard` values are overridden
- **THEN** the guard is enabled, stream-preserving, uses `OverloadedError`, scans
  thinking blocks, caps per-block accumulation at 10000 characters, and enables
  every per-signature toggle
