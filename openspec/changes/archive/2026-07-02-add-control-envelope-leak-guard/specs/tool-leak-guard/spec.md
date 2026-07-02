## ADDED Requirements

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

Control-envelope detection SHALL share the single-pass, per-block streaming
discipline of tool-call leak detection (state reset at each `content_block_start`,
detection invariant across delta split boundaries, no signature assembled across a
block boundary, retaining no block content), the delivery modes
(stream-preserving vs. buffered), the signal selection, and the clean client
retry. The same `Pipeline:Detectors:ToolLeakGuard` configuration governs both;
when the guard is disabled it performs no control-envelope scanning.

Detection SHALL NOT classify a block as a control-envelope leak when the envelope
is unclosed, is missing its required child or attribute, has an empty value where
non-empty content is required, or appears inside a code fence (text blocks). The
distinct `<channel-message>` … `</channel-message>` wrapper SHALL NOT be
classified as a `<channel>` leak.

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

#### Scenario: Missing required proof is not a leak
- **WHEN** a control envelope is unclosed, or a `<task-notification>` lacks a
  closed `<task-id>` or any proof child, or `teammate_id`/`source`/`from` is
  absent or empty, or a `<tick>` has empty inner text
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
  `<<task-notification` or `<task<task-notification`)
- **THEN** the leak is detected identically regardless of where the splits fall
  and the valid restart is not dropped

#### Scenario: Runaway capture fails open with a bounded buffer
- **WHEN** a required attribute value runs on far past any real identifier
- **THEN** the envelope is abandoned (fail-open) without an unbounded buffer and
  is NOT classified as a leak
