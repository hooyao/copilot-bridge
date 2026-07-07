## Why

`RunawayGuardDetector`'s two existing budgets — `MaxDeltaBytes` (12 MiB) and
`MaxDeltaCount` (20,000 deltas per block) — were tuned for the gpt-5.5 runaway
that emitted ~27,000 tiny fragments totalling ~17 MB. A different degenerate
shape slips straight through both: real trace `20260707-043031-0007` captured
**8 separate `claude-opus-4.8` runaways** that locked into repeating a single
token (`court\n\ncourt\n\ncourt…`) ~32,000 times until they hit the request's
`max_tokens=64000` cap. Each was only **~1,010 medium deltas / ~500 KB** — under
20,000 deltas and under 12 MiB — so the guard never tripped, and the bridge
faithfully relayed ~500 KB of garbage for up to **8.5 minutes** (one cycle's
`duration_ms=511110`) while Claude Code appeared hung. Two of the eight were
only stopped because the user pressed ESC (`cancelled by client`).

The volume budgets cannot catch this: the pathology is not *how much* but *how
repetitive*. A repetition-density signal separates the two cleanly — in the
trace, the trailing-500-token unique-token ratio was **0.002** for every runaway
versus **0.88** for a normal response, a three-order-of-magnitude gap.

## What Changes

- Add a **third, orthogonal signal** to `RunawayGuardDetector`: a per-content-block
  **sliding-window unique-token ratio** over the block's accumulated
  `text_delta` / `thinking_delta` text. It trips when the trailing window is full
  **and** its unique-token ratio falls below a floor — catching single-token (and
  low-diversity) repetition loops the volume budgets miss.
- Two new config knobs under `Pipeline:Detectors:RunawayGuard`:
  - `RepetitionWindow` (default `500`) — trailing whitespace-token window size;
    `<= 0` **disables** the signal (off-by-default-safe).
  - `RepetitionMinUniqueRatio` (default `0.05`) — trip when
    `distinct/window < ratio`. Set well above the observed 0.002 and far below a
    normal 0.88 so it never fires on legitimate diverse output.
- On a trip it reuses the existing abort path: the same `Signal`-selected
  `overloaded_error`/529 envelope and the `runaway=true` request-summary flag —
  no new wire shape, no new context flag.
- Per-block reset: the window clears at each `content_block_start`, exactly like
  the existing `MaxDeltaCount` per-block counter, so a legitimate long block
  cannot be poisoned by an earlier one.
- Text extraction is parse-free and consistent with
  `ResponseLeakDetector.ExtractDeltaText` (`text_delta.text` +
  `thinking_delta.thinking`); non-text deltas (e.g. `input_json_delta`) do not
  feed the window.

## Capabilities

### New Capabilities
- `runaway-guard`: the streamed-response volume/degeneracy circuit-breaker
  (`RunawayGuardDetector`). This change introduces the capability's spec, scoped
  to the **repetition-density signal**; the pre-existing byte/delta-count budgets
  are documented as established context, not re-litigated here.

### Modified Capabilities
<!-- None. The runaway guard has no prior dedicated spec; this change creates one. -->

## Impact

- **Code**: `RunawayGuardDetector` (add the sliding-window signal + per-block
  reset), `RunawayGuardOptions` (two new knobs), `appsettings.json`
  (`Pipeline:Detectors:RunawayGuard` documented defaults). No change to the abort
  machinery, `ResponseLeakError`, `BridgeContext.RunawayDetected`, or the summary
  line.
- **Behaviour**: aborts a class of degenerate responses currently relayed in
  full. Gated behind `RepetitionWindow > 0`; conservative defaults chosen from
  trace data so legitimate large, diverse outputs are unaffected.
- **Tests**: new contract-first unit tests in `RunawayGuardDetectorTests` using
  the real runaway signature (single-token repetition trips; diverse prose does
  not; repetitive-but-shorter-than-window does not trip; `RepetitionWindow <= 0`
  disables).
- **Docs**: `docs/pipeline-design.md` runaway-guard section and the
  `appsettings.json` comment gain the repetition signal.
