# response-leak-guard Specification

## Purpose

GitHub-Copilot-served Claude models intermittently leak structured protocol
markup as literal text inside a `text` or `thinking` content block instead of the
real structured block. Two families are known: a tool call leaked as
`<invoke name="X"><parameter …>…</parameter></invoke>` XML (instead of a
`tool_use` block), and a Claude Code control/event envelope
(`<task-notification>`, `<teammate-message>`, `<channel>`,
`<cross-session-message>`, `<tick>`) echoed back as the model's own output
(instead of remaining injected context). Claude Code renders such markup as text
and acts on nothing, the dirty bytes commit to the transcript, and the failure
self-reinforces on replay. The bridge sits on the one wire where this is both
detectable (it sees every SSE event) and fixable without polluting history (it can
force a clean retry before the dirty bytes commit). This capability defines a
response-pipeline guard that structurally detects each leak signature and forces
the client to retry the turn.
## Requirements
### Requirement: Structural leak detection

The bridge SHALL classify a response content block as a tool-call leak only when
ALL of the following structural conditions hold, evaluated over a single `text`
or `thinking` content block:

1. The block contains a closed, balanced `<invoke name="X">` … `</invoke>`
   sequence containing at least one closed `<parameter …>` … `</parameter>`.
2. `X` equals the `name` of a tool present in the originating request's `tools[]`.
3. The `<invoke>` is NOT inside a markdown code fence (```` ``` ````). `thinking`
   blocks have no fence concept and are treated as unfenced.

Detection SHALL NOT depend on any leading/prefix token (e.g. `court`, `call`),
on the message `stop_reason`, or on the presence of a bare unbalanced `<invoke`
substring.

Detection over `thinking` blocks SHALL be governed by the `ScanThinking` option;
when disabled, `thinking` blocks are never classified as leaks.

#### Scenario: Closed, in-tools, unfenced invoke is a leak
- **WHEN** a `text` block's accumulated content contains
  `<invoke name="Read">…<parameter name="file_path">/x</parameter>…</invoke>`,
  `Read` is in the request `tools[]`, and it is not inside a code fence
- **THEN** the block is classified as a leak

#### Scenario: Unbalanced invoke (prose quotation) is not a leak
- **WHEN** a `text` block contains `<invoke name="Read">` with no matching
  `</invoke>`, or more `<invoke>` opens than `</invoke>` closes
- **THEN** the block is NOT classified as a leak and the content passes through
  unchanged

#### Scenario: Invoke inside a code fence is not a leak
- **WHEN** a `text` block contains a fully closed `<invoke name="Read">…</invoke>`
  but it appears inside a ```` ``` ```` markdown fence
- **THEN** the block is NOT classified as a leak

#### Scenario: Tool name not in request tools is not a leak
- **WHEN** a `text` block contains a closed `<invoke name="FooTool">…</invoke>`
  and `FooTool` is not present in the request `tools[]`
- **THEN** the block is NOT classified as a leak

#### Scenario: Prefix token and stop_reason are irrelevant
- **WHEN** two responses each contain the same closed, in-tools, unfenced invoke,
  one preceded by the token `court` with `stop_reason=tool_use` and the other
  preceded by `call` with `stop_reason=end_turn`
- **THEN** both are classified as leaks identically

#### Scenario: Thinking scan disabled
- **WHEN** `ScanThinking` is false and a `thinking` block contains a closed,
  in-tools invoke
- **THEN** the block is NOT classified as a leak

### Requirement: Single-pass detection

The bridge SHALL detect the leak signature with a single-pass streaming scan
whose state is reset at each `content_block_start`. A signature that spans
multiple deltas within one block SHALL be detectable regardless of the block's
total length, without retaining the block's content. Detection SHALL examine each
streamed character at most once.

A signature SHALL NOT be assembled across a content-block boundary (the trailing
bytes of one block plus the leading bytes of the next).

The response-leak scan SHALL NOT be bounded by any scan-window or content-length
cap: it is a single-pass character-fed automaton that retains no block content, so
an arbitrarily long leaked block is detected without a window the opening tag could
scroll out of.

#### Scenario: Leak split across many deltas
- **WHEN** `<invoke name="Read">…</invoke>` arrives split across multiple
  `text_delta` events within one block
- **THEN** the leak is detected once the closing `</invoke>` delta has been
  scanned

#### Scenario: Large leaked block detected without a window
- **WHEN** a leaked block is several thousand characters (opening `<invoke>` far
  from the closing `</invoke>`)
- **THEN** the leak is still detected (no window in which the opening tag could be
  lost) and no per-block content buffer is retained

#### Scenario: No cross-block assembly
- **WHEN** one block ends with `</parameter>` and the next begins with `<invoke`
- **THEN** no leak is assembled from the concatenation

#### Scenario: Detection invariant across all split boundaries
- **WHEN** a known leak string is delivered split at any character boundary
  (char-by-char, mid-token `<inv`|`oke`, mid-name `Re`|`ad`, inside `</invoke>`,
  inside the fence marker)
- **THEN** the leak is detected identically regardless of where the splits fall

#### Scenario: Overlapping restart is not missed
- **WHEN** the text contains `<<invoke name="X">…</invoke>` (a false `<` start
  immediately before the real token) with `X` in `tools[]`, unfenced
- **THEN** the leak is still detected (the scan does not discard the valid restart)

### Requirement: Stream-preserving delivery

When `PreserveStream` is true, the bridge SHALL relay all response events
unchanged until a leak is detected, preserving time-to-first-token. Upon
detection the bridge SHALL inject a single SSE `error` event carrying the
configured signal and then end the stream, emitting no further upstream events.
The already-committed HTTP 200 status SHALL be left unchanged.

#### Scenario: Clean stream passes through
- **WHEN** `PreserveStream` is true and no block is classified as a leak
- **THEN** every upstream event is relayed unchanged and the stream ends normally

#### Scenario: Injected error ends the stream
- **WHEN** `PreserveStream` is true and a leak is detected mid-stream
- **THEN** the bridge emits an SSE `event: error` whose data carries the
  configured signal, stops relaying further upstream events, and leaves the HTTP
  status at 200

### Requirement: Buffered delivery with real status

When `PreserveStream` is false, the bridge SHALL buffer the entire response
before writing any byte to the client, classify it, and on a detected leak
respond with a real HTTP status determined by the signal (`OverloadedError`→529,
`ApiError`→500) and an Anthropic-format error body. A clean response SHALL be
delivered normally.

#### Scenario: Dirty buffered response yields error status
- **WHEN** `PreserveStream` is false, `Signal` is `OverloadedError`, and a leak
  is detected
- **THEN** the client receives HTTP 529 with an `overloaded_error` body and none
  of the leaked content

#### Scenario: Clean buffered response delivered normally
- **WHEN** `PreserveStream` is false and no leak is detected
- **THEN** the client receives the response content unchanged

### Requirement: Signal selection

The `Signal` option SHALL select both the Anthropic `error.type` string emitted
and, in buffered delivery, the HTTP status: `OverloadedError` → `overloaded_error`
/ 529; `ApiError` → `api_error` / 500. No other signal values SHALL be accepted.

#### Scenario: OverloadedError signal
- **WHEN** `Signal` is `OverloadedError` and a leak is detected
- **THEN** the emitted error's type is `overloaded_error` (stream: SSE error
  event; buffer: HTTP 529)

#### Scenario: ApiError signal
- **WHEN** `Signal` is `ApiError` and a leak is detected
- **THEN** the emitted error's type is `api_error` (stream: SSE error event;
  buffer: HTTP 500)

### Requirement: Configuration and default-off behavior

The guard SHALL be configured under `Pipeline:Detectors:ResponseLeakGuard` with keys
`Enabled` (default true), `PreserveStream` (default true), `BufferScannableBlocks`
(default false), `Signal` (default `OverloadedError`), `ScanThinking` (default true),
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
- **THEN** the guard is enabled, stream-preserving, does not buffer scannable blocks,
  uses `OverloadedError`, scans thinking blocks, and enables every per-signature
  toggle

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

### Requirement: Block-buffered delivery for airtight streaming suppression

The response-leak guard SHALL support an opt-in delivery mode,
`BufferScannableBlocks`, that closes the residual gap where a leak on a streaming
response is detected only *after* its bytes have already been relayed to the client.
When `BufferScannableBlocks` is true and `PreserveStream` is true, the guard SHALL,
for each **scannable** content block (`text`, and `thinking` when `ScanThinking` is
enabled), withhold that block's `content_block_delta` events until the block
completes (`content_block_stop`), scan the assembled block, and only then relay the
block's events — so a block classified as a leak is aborted **before any of its
leaked bytes reach the client**. Non-scannable blocks (e.g. `tool_use`,
`input_json_delta`) SHALL continue to stream live without being withheld, preserving
time-to-first-token for the parts of the response that cannot leak.

On a leak detected in a withheld block, the guard SHALL abort with the configured
`Signal` and SHALL NOT relay any of that block's events, using the same clean client
retry as the other delivery modes. A withheld block found clean SHALL be relayed in
full with its original events unchanged.

This mode changes only the *timing and suppression* of scannable-block delivery; the
structural detection conditions, the per-signature toggles, the signal selection, and
the observability contract are unchanged. `BufferScannableBlocks` SHALL default to
`false`; when false, streaming delivery is exactly the pre-existing
stream-preserving behaviour (relay until detection, then inject an error), and the
residual "leaked bytes may already be on the client" property SHALL be documented
rather than silent.

`BufferScannableBlocks` SHALL have no effect when `PreserveStream` is false (that
mode already buffers the whole response and emits a real status before any byte is
written) or when the guard is disabled. Configuration SHALL be read at startup; a
restart is required after changing it.

#### Scenario: Leak in a withheld block is suppressed before any byte reaches the client

- **WHEN** `BufferScannableBlocks` is true, `PreserveStream` is true, and a `text`
  block streams a closed control-envelope or `<invoke>` leak
- **THEN** the guard withholds that block's deltas until block end, detects the
  leak, aborts with the configured `Signal`, and the client receives **none** of the
  leaked block's content

#### Scenario: Clean withheld block is relayed unchanged

- **WHEN** `BufferScannableBlocks` is true and a scannable block completes with no
  leak
- **THEN** all of that block's `content_block_delta` events are relayed unchanged
  after block end and the stream continues normally

#### Scenario: Non-scannable blocks still stream live

- **WHEN** `BufferScannableBlocks` is true and the response contains a `tool_use`
  block (whose `input_json_delta` events cannot carry a text leak)
- **THEN** that block's events are streamed live without being withheld, so
  time-to-first-token for non-scannable content is preserved

#### Scenario: Default-off preserves current streaming behaviour

- **WHEN** `BufferScannableBlocks` is false (default) and a leak is detected
  mid-stream
- **THEN** delivery is exactly the pre-existing stream-preserving behaviour — events
  relayed until detection, then a single SSE `error` event and end-of-stream — with
  no block withholding

