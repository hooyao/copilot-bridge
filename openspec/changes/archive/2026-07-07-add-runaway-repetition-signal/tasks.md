# Tasks

## 1. Config knobs

- [x] 1.1 Add `RepetitionWindow` (int, default `500`) and `RepetitionMinUniqueRatio` (double, default `0.05`) to `RunawayGuardOptions`, with XML docs stating `RepetitionWindow <= 0` disables the signal and the startup-read/restart note.
- [x] 1.2 Document both knobs in `appsettings.json` under `Pipeline:Detectors:RunawayGuard` (`_comment` / `_RepetitionWindow` / `_RepetitionMinUniqueRatio`) with the trace-grounded rationale (runaway ~0.002 vs normal ~0.88).

## 2. Detector logic

- [x] 2.1 Add per-request repetition state to `RunawayGuardDetector`: a fixed-capacity ring buffer of trailing tokens, a `Dictionary<string,int>` multiset of their counts, and a `_tokenTail` carry-over string. Initialise/clear them in `Begin()`.
- [x] 2.2 Add a parse-free-guarded text extraction consistent with `ResponseLeakDetector.ExtractDeltaText` (`text_delta.text` + `thinking_delta.thinking`); return early when `RepetitionWindow <= 0` so no extraction/allocation happens when disabled.
- [x] 2.3 In the `content_block_delta` arm (after the byte and delta-count checks), tokenise the extracted text with `_tokenTail` carry-over, push tokens through the ring/multiset (evict-oldest on full), and trip when the ring is full AND `distinct < RepetitionWindow * RepetitionMinUniqueRatio`. Reuse the existing `Trip(reason)` (Signal envelope + `runaway=true`).
- [x] 2.4 Clear the ring/multiset/`_tokenTail` in the `content_block_start` arm alongside `_blockDeltaCount`, so diversity is measured per content block.

## 3. Tests (contract-first)

- [x] 3.1 Single-token repetition trips: feed a `content_block_delta` stream that degenerates into one repeated token (split across delta boundaries as the trace did, ~12-char fragments) → assert `Abort` with the `overloaded_error`/529 envelope and `RunawayDetected == true`.
- [x] 3.2 Volume-budget miss but repetition catch: a stream under `MaxDeltaCount` and `MaxDeltaBytes` that still repeats one token past the window → asserts the repetition signal (not a volume budget) trips.
- [x] 3.3 Diverse prose does not trip: a long block whose trailing-window unique ratio stays ≥ ratio → asserts no action, flag clear.
- [x] 3.4 Short-but-repetitive under window does not trip: repeat a token fewer than `RepetitionWindow` times → asserts no action (window must be full).
- [x] 3.5 Per-block reset: a diverse block followed by a repetitive block trips only on the second; a repetitive block followed by a diverse block does not carry over → assert.
- [x] 3.6 Disabled by `RepetitionWindow <= 0`: same repetitive stream with window `0` → asserts no repetition trip while byte/count budgets still function.
- [x] 3.7 Mutation-check the two key tests (single-token trip, diverse-no-trip) by temporarily breaking the ratio comparison; confirm red, then revert.

## 4. Docs

- [x] 4.1 Update the runaway-guard section of `docs/pipeline-design.md` to describe the third (repetition-density) signal, its per-block window, and the disable knob.

## 5. Verify

- [x] 5.1 `dotnet test tests/CopilotBridge.UnitTests` green (full suite, not just the new tests).
- [x] 5.2 Confirm `DetectorCompositionTests` still passes (detector count/order unchanged — this change adds no detector).
