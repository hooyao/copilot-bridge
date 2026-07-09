# runaway-guard

## Purpose

The runaway guard is the response volume/degeneracy circuit-breaker
(`RunawayGuardDetector`). It aborts a degenerate-generation runaway — a model stuck
emitting an unbounded stream of tiny fragments, or one repeating the same token —
before it hangs the client for minutes, using a retryable error the client treats as
transient. It applies on both the streaming (`text/event-stream`) and buffered
(`application/json`) delivery paths. This spec covers the content-keyed
**repetition-density** and **consecutive-run** signals and the buffered-path
detection; the pre-existing byte (`MaxDeltaBytes`) and per-block delta-count
(`MaxDeltaCount`) volume budgets are established context and, being streaming-only,
are not restated here.

## Requirements

### Requirement: Repetition-density runaway detection

The runaway guard SHALL detect a degenerate response that repeats the same token
(or a very small set of tokens) within a single content block, independently of
the cumulative byte and per-block delta-count budgets. Detection SHALL use a
**per-content-block sliding window** over the block's accumulated visible text:
the guard tracks the trailing `RepetitionWindow` whitespace-delimited tokens and
SHALL abort when the window is **full** and its unique-token ratio
(`distinct tokens / RepetitionWindow`) is **strictly less than**
`RepetitionMinUniqueRatio`.

The signal SHALL only consume text carried by `text_delta` (`.text`) and
`thinking_delta` (`.thinking`) deltas on the streaming path, and by `text` /
`thinking` block content on the buffered path, consistent with the leak guard's
extraction; deltas or blocks that carry no such text (e.g. `input_json_delta`,
`tool_use`) SHALL NOT feed the window. On a trip it SHALL use the same abort
machinery as the existing budgets: the configured `Signal` envelope (default
`overloaded_error` / HTTP 529 in buffered delivery) and SHALL set the
request-summary `runaway=true` flag. It SHALL NOT introduce a new error wire shape
or a new context flag.

The sliding-window signal is one of several runaway signals; a block that stays
under the window-fullness gate (fewer than `RepetitionWindow` total tokens) is NOT
thereby exempt from runaway detection — the consecutive-identical-token run-length
signal covers the short-flood case the window cannot reach.

#### Scenario: Single-token repetition loop trips the guard

- **WHEN** a single content block streams text that, after some initial prose,
  degenerates into the same token repeated so that the trailing `RepetitionWindow`
  tokens contain fewer than `RepetitionMinUniqueRatio × RepetitionWindow` distinct
  values
- **THEN** the guard aborts the response with the configured retryable `Signal`
  envelope and marks `runaway=true` on the request summary

#### Scenario: Volume budgets miss it but the repetition signal catches it

- **WHEN** a runaway repeats a single token to the model's `max_tokens` cap using
  a delta count below `MaxDeltaCount` and a total payload below `MaxDeltaBytes`
  (the observed shape: ~1,010 deltas, ~500 KB, ~32,000 identical tokens)
- **THEN** the repetition-density signal trips even though neither volume budget
  is exceeded

#### Scenario: Diverse legitimate output never trips

- **WHEN** a content block streams a large but linguistically diverse response
  whose trailing-window unique-token ratio stays at or above
  `RepetitionMinUniqueRatio` (a normal response measured ~0.88 versus the ~0.002
  of a runaway)
- **THEN** the guard emits no action and the response is relayed unchanged

#### Scenario: Repetitive text shorter than the window is covered by the run-length signal

- **WHEN** a block repeats a token but the total accumulated token count for that
  block has not yet filled `RepetitionWindow`
- **THEN** the repetition-density (window) signal does not evaluate (the window
  must be full before the ratio is checked), but the consecutive-identical-token
  run-length signal still applies, so a short single-token flood is not exempt from
  detection

#### Scenario: A token split across deltas is counted once

- **WHEN** a token's characters arrive across multiple `content_block_delta` events
  with its terminating whitespace in a later delta
- **THEN** the guard reassembles it into a single token (a carried partial token),
  so a repeated split token still collapses the unique-token ratio and trips

#### Scenario: A whitespace-free run does not grow the carried tail unboundedly

- **WHEN** a stream carries a long run with no whitespace (e.g. base64, minified
  JSON, or CJK text) so no token boundary is reached across many deltas
- **THEN** the carried partial token SHALL be bounded (it never needs to exceed the
  window to affect the ratio), so it cannot cause unbounded reallocation, and — being
  one unfinished token — it does not fill the window or falsely trip

### Requirement: Repetition signal per-block reset and configuration

The repetition window and the run-length counter SHALL reset at each
`content_block_start` (streaming) and per block (buffered), so diversity and
consecutive-run counts are measured per content block and an earlier block cannot
poison a later one — the same per-block scoping the delta-count budget already uses.
The signals SHALL be configurable under `Pipeline:Detectors:RunawayGuard` via
`RepetitionWindow` (trailing token window size), `RepetitionMinUniqueRatio` (the
ratio floor), and `RepetitionMaxConsecutiveRepeat` (the consecutive-run threshold).
A `RepetitionWindow` value of `0` or less SHALL disable the repetition-density
(window) signal entirely, leaving the byte, delta-count, and run-length signals in
force. To bound per-request memory (the ring buffer is allocated per request),
`RepetitionWindow` SHALL be clamped to a fixed maximum so an absurd configured value
cannot exhaust memory. `RepetitionMinUniqueRatio` MUST be in the open interval
`(0, 1)`; a value at or outside that range (`≤ 0` or `≥ 1`) SHALL disable the
repetition-density signal rather than force a trip on every full window. A
`RepetitionMaxConsecutiveRepeat` value of `0` or less SHALL disable the run-length
signal. Configuration SHALL be read at startup; a restart is required after changing
a value.

#### Scenario: Window and run counters reset on a new content block

- **WHEN** one content block ends and a new `content_block_start` arrives
- **THEN** the trailing-token window and the consecutive-run counter are both
  cleared, so the new block's diversity and run length are computed only from its
  own text

#### Scenario: Repetition-density signal disabled by non-positive window

- **WHEN** `RepetitionWindow` is set to `0` (or a negative value)
- **THEN** the guard performs no window tracking and never trips on the
  repetition-density signal, while `MaxDeltaBytes`, `MaxDeltaCount`, and
  `RepetitionMaxConsecutiveRepeat` continue to apply

#### Scenario: Out-of-range ratio disables the density signal rather than force-tripping

- **WHEN** `RepetitionMinUniqueRatio` is configured at or outside `(0, 1)` — e.g.
  `0`, a negative value, `1`, or a typo like `5` for `0.5`
- **THEN** the repetition-density signal is disabled (no trip), rather than aborting
  every response once the window fills

#### Scenario: Absurd window is clamped, not fatal

- **WHEN** `RepetitionWindow` is configured far above any legitimate size (e.g.
  hundreds of millions)
- **THEN** the window is clamped to the fixed maximum so the per-request ring
  allocation stays bounded, and the signal still functions under the clamped window

#### Scenario: Master switch still governs the whole guard

- **WHEN** the runaway guard's `Enabled` flag is `false`
- **THEN** no signal runs — byte, delta-count, repetition-density, and run-length
  alike — and the detector is never begun

### Requirement: Runaway detection applies on the buffered delivery path

The runaway guard SHALL detect a degenerate runaway on a **buffered**
(non-streaming, `application/json`) upstream response, not only on the streaming
(`text/event-stream`) path. Copilot may ignore a request's `stream:true` and return
a one-shot Anthropic Messages JSON body; that body SHALL be scanned with the same
signals and the same abort semantics as the streaming path.

The buffered scan SHALL parse the response body as an Anthropic Messages object and
feed the visible text of each `text` content block and each `thinking` content block
through the same per-block repetition and run-length logic used for streaming deltas —
matching the streaming path, which feeds both `text_delta` and `thinking_delta`
unconditionally (the runaway guard has no thinking-scan gate). It SHALL treat each
content block as its own scope (reset between blocks), mirroring the streaming
per-`content_block_start` reset. On a detected runaway it SHALL abort with the
configured `Signal` (a real HTTP status in buffered delivery —
`overloaded_error`→529, `api_error`→500 — and an Anthropic-format error body) and
SHALL set the request-summary `runaway=true` flag, using the same abort machinery and
context flag as the streaming path — no new wire shape, no new flag.

A body that does not parse as an Anthropic Messages object (or has no `content`
array) SHALL fail open: the guard performs no action and the body is delivered
unchanged, so a parse hiccup never turns a real response into an error.

#### Scenario: Buffered single-token flood trips the guard

- **WHEN** Copilot returns a buffered `application/json` response whose `text`
  block, after some initial prose, degenerates into one token repeated enough to
  satisfy a runaway signal, followed by a valid `tool_use` block
- **THEN** the guard aborts with the configured retryable `Signal` (HTTP 529 for
  `OverloadedError`) and marks `runaway=true`, and the client receives none of the
  leaked repeated content

#### Scenario: Clean buffered response is delivered unchanged

- **WHEN** a buffered response's blocks are within all runaway signal thresholds
- **THEN** the guard emits no action and the body is delivered byte-for-byte
  unchanged

#### Scenario: Unparseable buffered body fails open

- **WHEN** a buffered response body is not a parseable Anthropic Messages object (or
  lacks a `content` array)
- **THEN** the guard takes no action and the body is delivered unchanged

#### Scenario: Buffered scan covers thinking blocks like the streaming path

- **WHEN** a runaway occurs inside a `thinking` block of a buffered response
- **THEN** the guard trips on it, matching the streaming path (which feeds
  `thinking_delta` unconditionally); the runaway guard has no thinking-scan gate

### Requirement: Consecutive-identical-token run-length signal

The runaway guard SHALL provide a per-content-block **run-length** signal that trips
when the same token is emitted `RepetitionMaxConsecutiveRepeat` or more times
**consecutively**, independently of the sliding-window fullness and of the block's
total token count. This catches a short degenerate flood — one that repeats a token
far fewer than `RepetitionWindow` times, so the window never fills — which the
sliding-window ratio signal cannot detect.

The signal SHALL consume the same visible text as the repetition-density signal
(`text_delta`/`thinking_delta` on the streaming path; `text`/`thinking` block
content on the buffered path), SHALL count consecutive equal whitespace-delimited
tokens with a small bounded counter (it retains at most the previous token and a
count, not block content), and SHALL reset that counter at each
`content_block_start` (streaming) or per block (buffered). A token identical to the
immediately preceding one increments the run; any different token resets the run to
one. On a trip it SHALL use the same abort machinery, `Signal` envelope, and
`runaway=true` flag as the other runaway signals — no new wire shape or context
flag.

The signal SHALL be configurable under `Pipeline:Detectors:RunawayGuard` via
`RepetitionMaxConsecutiveRepeat`. A value of `0` or less SHALL disable the
run-length signal, leaving the byte, delta-count, and repetition-density signals in
force. Configuration SHALL be read at startup; a restart is required after changing
a value. Because the threshold is deliberately low (default on the order of tens of
consecutive repeats), a legitimate repetitive output that trips it is resolved by
raising `RepetitionMaxConsecutiveRepeat`, not by disabling the guard.

#### Scenario: Short consecutive flood trips even though the window never fills

- **WHEN** a content block repeats a single token consecutively at least
  `RepetitionMaxConsecutiveRepeat` times but the block's total token count stays
  below `RepetitionWindow` (so the sliding-window signal cannot evaluate)
- **THEN** the run-length signal trips and the guard aborts with the configured
  retryable `Signal` and marks `runaway=true`

#### Scenario: Run-length signal trips on both delivery paths

- **WHEN** the same consecutive-repeat flood occurs once in a streaming response and
  once in a buffered response
- **THEN** the guard trips identically on both paths

#### Scenario: A non-repeating token resets the run

- **WHEN** a block emits fewer than `RepetitionMaxConsecutiveRepeat` consecutive
  identical tokens, interrupted by a different token, then resumes repeating
- **THEN** the run counter resets on the interrupting token, so the signal trips
  only if a single uninterrupted run reaches the threshold

#### Scenario: Diverse legitimate output does not trip the run-length signal

- **WHEN** a large linguistically diverse response never repeats the same token more
  than a few times in a row
- **THEN** the run-length signal does not trip and the response is relayed unchanged

#### Scenario: Run-length signal disabled by non-positive threshold

- **WHEN** `RepetitionMaxConsecutiveRepeat` is set to `0` (or a negative value)
- **THEN** the run-length signal performs no tracking and never trips, while the
  byte, delta-count, and repetition-density signals continue to apply

#### Scenario: A run split across deltas is counted as consecutive

- **WHEN** consecutive identical tokens arrive across multiple `content_block_delta`
  events (including a token split at its terminating whitespace across a delta
  boundary)
- **THEN** the reassembled tokens are counted as one uninterrupted run, so a
  delta-fragmented flood still trips

