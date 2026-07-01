# tool-leak-guard Specification

## Purpose

GitHub-Copilot-served Claude models intermittently leak a tool call as literal
`<invoke name="X"><parameter …>…</parameter></invoke>` XML inside a `text` or
`thinking` content block instead of emitting a proper `tool_use` block. Claude
Code renders it as text and executes nothing, the dirty bytes commit to the
transcript, and the failure self-reinforces on replay. The bridge sits on the
one wire where this is both detectable (it sees every SSE event) and fixable
without polluting history (it can force a clean retry before the dirty bytes
commit). This capability defines a response-pipeline guard that structurally
detects the leak signature and forces the client to retry the turn.

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

The tool-leak scan SHALL NOT be bounded by `MaxScanChars` (it retains no
content); `MaxScanChars` applies only to detectors that buffer block content.

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

The guard SHALL be configured under `Pipeline:Detectors:ToolLeakGuard` with keys `Enabled`
(default true), `PreserveStream` (default true), `Signal` (default
`OverloadedError`), `ScanThinking` (default true), and `MaxScanChars` (default
10000). When `Enabled` is false the guard SHALL be a no-op that performs no
scanning, no accumulation, and no allocation on the response path.

#### Scenario: Disabled guard is inert
- **WHEN** `Enabled` is false and a response contains a leak
- **THEN** the response is relayed unchanged with no scanning or allocation, as
  if the guard were absent

#### Scenario: Defaults
- **WHEN** no `Pipeline:Detectors:ToolLeakGuard` values are overridden
- **THEN** the guard is enabled, stream-preserving, uses `OverloadedError`, scans
  thinking blocks, and caps per-block accumulation at 10000 characters
