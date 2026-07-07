# runaway-guard

## ADDED Requirements

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
`thinking_delta` (`.thinking`) deltas, consistent with the leak guard's
extraction; deltas that carry no such text (e.g. `input_json_delta`) SHALL NOT
feed the window. On a trip it SHALL use the same abort machinery as the existing
budgets: the configured `Signal` envelope (default `overloaded_error` / HTTP 529
in buffered delivery) and SHALL set the request-summary `runaway=true` flag. It
SHALL NOT introduce a new error wire shape or a new context flag.

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

#### Scenario: Repetitive text shorter than the window does not trip

- **WHEN** a block repeats a token but the total accumulated token count for that
  block has not yet filled `RepetitionWindow`
- **THEN** the guard does not abort on the repetition signal (the window must be
  full before the ratio is evaluated), so a brief legitimate repetition is not a
  false positive

### Requirement: Repetition signal per-block reset and configuration

The repetition window SHALL reset at each `content_block_start`, so diversity is
measured per content block and an earlier block cannot poison a later one — the
same per-block scoping the delta-count budget already uses. The signal SHALL be
configurable under `Pipeline:Detectors:RunawayGuard` via `RepetitionWindow`
(trailing token window size) and `RepetitionMinUniqueRatio` (the floor). A
`RepetitionWindow` value of `0` or less SHALL disable the repetition signal
entirely, leaving the byte and delta-count budgets in force. Configuration SHALL
be read at startup; a restart is required after changing a value.

#### Scenario: Window resets on a new content block

- **WHEN** one content block ends and a new `content_block_start` arrives
- **THEN** the trailing-token window is cleared, so the new block's unique-token
  ratio is computed only from its own text

#### Scenario: Repetition signal disabled by non-positive window

- **WHEN** `RepetitionWindow` is set to `0` (or a negative value)
- **THEN** the guard performs no repetition tracking and never trips on the
  repetition signal, while `MaxDeltaBytes` and `MaxDeltaCount` continue to apply

#### Scenario: Master switch still governs the whole guard

- **WHEN** the runaway guard's `Enabled` flag is `false`
- **THEN** no signal runs — byte, delta-count, and repetition alike — and the
  detector is never begun
