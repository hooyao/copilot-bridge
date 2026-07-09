# response-leak-guard (delta)

## ADDED Requirements

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

## MODIFIED Requirements

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
