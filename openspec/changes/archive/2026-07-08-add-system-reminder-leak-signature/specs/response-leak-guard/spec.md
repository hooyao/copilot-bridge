## MODIFIED Requirements

### Requirement: Control-envelope leak detection

The bridge SHALL additionally classify a response content block as a
control-envelope leak when the block contains a CLOSED, shape-valid Claude Code
control envelope emitted as literal text, evaluated over a single `text` or
`thinking` content block that is NOT inside a markdown code fence (```` ``` ````).
`thinking` blocks have no fence concept and are treated as unfenced; detection
over `thinking` blocks is governed by the same `ScanThinking` option as tool-call
leak detection.

The recognized envelopes and their required shape are:

1. `<task-notification>` … `</task-notification>` closed, containing a closed
   `<task-id>` … `</task-id>` child AND at least one closed `<summary>`,
   `<status>`, or `<output-file>` child.
2. `<teammate-message …>` … `</teammate-message>` closed, with a non-empty
   `teammate_id="…"` attribute before the opening `>`.
3. `<channel …>` … `</channel>` closed, with a non-empty `source="…"` attribute
   before the opening `>`.
4. `<cross-session-message …>` … `</cross-session-message>` closed, with a
   non-empty `from="…"` attribute before the opening `>`.
5. `<tick>` … `</tick>` closed, with non-empty inner text.
6. `<system-reminder>` … `</system-reminder>` closed, with non-empty inner text.
   The wrapper is the bare tag with no attributes (as Claude Code's
   `wrapInSystemReminder` renders it); its only proof is that it is closed and its
   inner content is non-empty.

Control-envelope detection SHALL share the single-pass, per-block streaming
discipline of tool-call leak detection (state reset at each `content_block_start`,
detection invariant across delta split boundaries, no signature assembled across a
block boundary, retaining no block content), the delivery modes
(stream-preserving vs. buffered), the signal selection, and the clean client
retry. The same `Pipeline:Detectors:ResponseLeakGuard` configuration governs both;
when the guard is disabled it performs no control-envelope scanning.

Detection SHALL NOT classify a block as a control-envelope leak when the envelope
is unclosed, is missing its required child or attribute, has an empty value where
non-empty content is required (including an empty
`<system-reminder></system-reminder>`), or appears inside a code fence (text
blocks). The distinct `<channel-message>` … `</channel-message>` wrapper SHALL NOT
be classified as a `<channel>` leak.

#### Scenario: Closed task-notification is a leak
- **WHEN** a `text` block contains a closed `<task-notification>` with a closed
  `<task-id>` and at least one closed `<summary>`/`<status>`/`<output-file>`
  child, not inside a code fence
- **THEN** the block is classified as a leak and the turn is retried

#### Scenario: Attribute-bearing envelope is a leak
- **WHEN** a `text` block contains a closed `<teammate-message teammate_id="…">`,
  `<channel source="…">`, or `<cross-session-message from="…">` whose required
  attribute has a non-empty value, not inside a code fence
- **THEN** the block is classified as a leak

#### Scenario: Closed non-empty tick is a leak
- **WHEN** a `text` block contains `<tick>…</tick>` with non-empty inner text, not
  inside a code fence
- **THEN** the block is classified as a leak

#### Scenario: Closed non-empty system-reminder is a leak
- **WHEN** a `text` block contains `<system-reminder>…</system-reminder>` with
  non-empty inner text, not inside a code fence
- **THEN** the block is classified as a leak and the turn is retried

#### Scenario: Missing required proof is not a leak
- **WHEN** a control envelope is unclosed, or a `<task-notification>` lacks a
  closed `<task-id>` or any proof child, or `teammate_id`/`source`/`from` is
  absent or empty, or a `<tick>` has empty inner text, or a `<system-reminder>`
  has empty inner text
- **THEN** the block is NOT classified as a leak and its content passes through
  unchanged

#### Scenario: Channel-message wrapper is not a channel leak
- **WHEN** a `text` block contains
  `<channel-message source="…">…</channel-message>`
- **THEN** it is NOT classified as a `<channel>` leak (its close tag differs)

#### Scenario: Fenced envelope is not a leak, thinking is unfenced
- **WHEN** a closed control envelope appears inside a ```` ``` ```` fence in a
  `text` block
- **THEN** the block is NOT classified as a leak; the same content in a `thinking`
  block (no fence concept) IS classified as a leak when `ScanThinking` is enabled

#### Scenario: Detection invariant across split boundaries and overlapping restart
- **WHEN** a recognized closed envelope is delivered split at any character
  boundary, or immediately preceded by a false partial start (e.g.
  `<<task-notification`, `<task<task-notification`, or
  `<system<system-reminder>`)
- **THEN** the leak is detected identically regardless of where the splits fall
  and the valid restart is not dropped

#### Scenario: Runaway capture fails open with a bounded buffer
- **WHEN** a required attribute value runs on far past any real identifier
- **THEN** the envelope is abandoned (fail-open) without an unbounded buffer and
  is NOT classified as a leak

### Requirement: Configuration and default-off behavior

The guard SHALL be configured under `Pipeline:Detectors:ResponseLeakGuard` with keys
`Enabled` (default true), `PreserveStream` (default true), `Signal` (default
`OverloadedError`), `ScanThinking` (default true),
and a `Signatures` sub-block of per-signature toggles (see the Per-signature
detection toggles requirement; all default true). When `Enabled` is false the
guard SHALL be a no-op that performs no scanning, no accumulation, and no
allocation on the response path.

#### Scenario: Disabled guard is inert
- **WHEN** `Enabled` is false and a response contains a leak
- **THEN** the response is relayed unchanged with no scanning or allocation, as
  if the guard were absent

#### Scenario: Defaults
- **WHEN** no `Pipeline:Detectors:ResponseLeakGuard` values are overridden
- **THEN** the guard is enabled, stream-preserving, uses `OverloadedError`, scans
  thinking blocks, and enables every per-signature toggle

### Requirement: Per-signature detection toggles

The guard SHALL expose an independent enable switch for each leak signature under
`Pipeline:Detectors:ResponseLeakGuard:Signatures`, with the boolean keys `Invoke`,
`TaskNotification`, `TeammateMessage`, `Channel`, `CrossSessionMessage`, `Tick`,
and `SystemReminder`, each defaulting to true. A signature whose switch is false
SHALL NOT be evaluated — its matcher is not constructed and the corresponding
markup passes through unchanged — while every still-enabled signature continues to
trip the guard normally. This is a false-positive escape hatch for a session that
legitimately echoes the markup (for example a user discussing how `<invoke>`
tool-use, a `<task-notification>` envelope, or the `<system-reminder>` wrapper
works, whose faithful sample reply would otherwise be caught).

The per-signature switches SHALL be read per request when the detector initializes
for that request. Because the switches do not reload at runtime, a change to a
switch SHALL require a restart of the bridge to take effect. The detector SHALL
compute its enabled-signature set per request from the current options and SHALL
NOT retain a process-wide cache of that set, so that the only change needed to make
a flipped switch take effect without a restart is sourcing the options from a live
(reloading) provider — with no change to detection logic.

When a leak is detected, the retry error surfaced to the client SHALL name the
tripped signature and the exact `Pipeline:Detectors:ResponseLeakGuard:Signatures` key
that disables it, and SHALL note that a restart is required after changing the
switch.

#### Scenario: Disabled signature passes through, siblings still trip
- **WHEN** `Signatures:Invoke` is false and a response contains a closed,
  in-tools, unfenced `<invoke>` leak
- **THEN** the block is NOT classified as a leak and passes through unchanged,
  while a `<task-notification>` (or any other still-enabled signature) in the same
  configuration is still classified as a leak

#### Scenario: System-reminder signature can be disabled independently
- **WHEN** `Signatures:SystemReminder` is false and a response contains a closed,
  non-empty, unfenced `<system-reminder>` envelope
- **THEN** the block is NOT classified as a leak and passes through unchanged,
  while every other still-enabled signature in the same configuration still trips

#### Scenario: All signatures default on
- **WHEN** no `Signatures` values are overridden
- **THEN** all seven signatures are enabled and behave exactly as when the
  sub-block is absent

#### Scenario: Retry error names the signature, disable key, and restart
- **WHEN** a leak is detected for a given signature
- **THEN** the retry error delivered to the client names that signature, names the
  exact `Pipeline:Detectors:ResponseLeakGuard:Signatures` key that disables it, and
  states that a restart is required for the change to take effect
