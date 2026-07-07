## Context

`RunawayGuardDetector` (`src/CopilotBridge.Cli/Pipeline/Response/Detection/`) is a
scoped response detector inside `ResponseInspectionStage`. It inspects each
streamed SSE event via `InspectEvent(in SseItem<string> evt)` and, on a trip,
returns `DetectionAction.Abort(...)` built from `ResponseLeakError` and sets
`BridgeContext.RunawayDetected = true`. Today it carries two per-request counters,
reset in `Begin()` / at `content_block_start`:

- `_totalDeltaBytes` vs `MaxDeltaBytes` (12 MiB, whole response)
- `_blockDeltaCount` vs `MaxDeltaCount` (20,000, per content block)

Both measure `evt.Data.Length` — a deliberately **parse-free** volume proxy.

Trace `20260707-043031-0007` (analysed for this change) shows a shape neither
budget catches: `claude-opus-4.8` locking into `court\n\ncourt\n\n…` ~32,000
times to the `max_tokens=64000` cap, delivered as **~1,010 deltas / ~500 KB**
per block across 8 cycles. Ground-truth metric on that trace:

| Measure | Runaway (4781/4830/4849) | Normal turn (4795) |
|---|---|---|
| Whole-block unique-token ratio | 0.0006–0.0019 | 0.879 |
| Trailing-500-token unique ratio | 0.0020 | 0.88 |

The separation is ~3 orders of magnitude, which is what makes a repetition signal
robust with conservative defaults.

## Goals / Non-Goals

**Goals:**
- Catch single-token / very-low-diversity repetition loops that stay under both
  volume budgets, aborting via the existing retryable path before ~500 KB of
  garbage is relayed for minutes.
- Off-by-default-safe and false-positive-safe: gated on `RepetitionWindow > 0`,
  defaults chosen from trace data so legitimate large diverse output never trips.
- Zero new wire shape, context flag, or abort machinery — reuse `Signal` /
  `overloaded_error` / `runaway=true`.

**Non-Goals:**
- Detecting semantic repetition (paraphrase loops) or cross-block repetition —
  the signal is lexical and per-block.
- Repairing or truncating the response — the guard only aborts, as today.
- Changing the byte / delta-count budgets or their defaults.
- Tokenisation quality: whitespace splitting is a proxy, not a linguistic
  tokenizer (see Decisions).

## Decisions

### D1: Sliding-window unique-token ratio (not run-length, not whole-block)

Chosen over the two alternatives considered:
- **Longest consecutive-repeat run** — simplest, but blind to `A B A B …`
  two-token alternation and to `court . court , court …` where punctuation breaks
  the run. The window ratio catches any low-diversity loop.
- **Whole-block unique ratio at `content_block_stop`** — accurate but only
  decidable *after* the block ends; for a runaway that streams to `max_tokens`
  that is exactly when it is too late (the whole payload has already been
  relayed). A trailing window trips **mid-stream**, preserving the guard's
  stop-loss purpose.

The window trips only when **full** (`count >= RepetitionWindow`) so a short
legitimate repetition (e.g. a small ASCII table, a `yes yes yes` confirmation)
cannot false-positive before enough evidence accrues.

### D2: Ring buffer of trailing tokens + incremental distinct count

Maintain a fixed-capacity `string[]` ring of the last `RepetitionWindow` tokens
plus a `Dictionary<string,int>` multiset of their counts. On each new token: if
the ring is full, evict the oldest (decrement its count, remove at zero); append
the new token (increment). `distinct = dict.Count`. This is **O(1) amortised per
token** and bounded memory (`RepetitionWindow` entries) — no re-scan, no growth
with response length. The ratio check `dict.Count < RepetitionWindow * ratio` runs
only once the ring is full.

Tokenisation is a parse-light split of the extracted delta text on ASCII
whitespace. Unlike the byte/count budgets this signal must look **inside** the
delta (the volume proxies do not), so it parses the delta JSON to pull
`text_delta.text` / `thinking_delta.thinking` — reusing the exact extraction
`ResponseLeakDetector.ExtractDeltaText` already performs. A delta that carries no
such text contributes nothing (returns null → skip), so `input_json_delta` and
control frames are ignored.

Token carry-over: delta boundaries do not align with token boundaries, so the
detector keeps a small `_tokenTail` string of the trailing partial token from the
previous delta and prepends it to the next delta's text before splitting (a token
is only counted once its terminating whitespace arrives). This keeps
`court\n\n` + `court\n\n` from being miscounted as `courtcourt`.

### D3: Config knobs and defaults

Under `Pipeline:Detectors:RunawayGuard`:
- `RepetitionWindow` (int, default **500**): trailing token window. `<= 0`
  disables the signal (parse-free short-circuit — no extraction, no allocation).
- `RepetitionMinUniqueRatio` (double, default **0.05**): trip when
  `distinct/window < ratio`. 0.05 sits ~25× above the observed runaway 0.002 and
  ~17× below a normal 0.88 — wide margin on both sides. A degenerate 2–3 token
  loop over a 500 window is ratio ≤ 0.006, still far under 0.05.

Window 500 balances latency-to-trip (≈500 tokens ≈ a few KB, a fraction of a
second of a runaway) against evidence sufficiency. Both are startup-read
(`reloadOnChange:false`), matching the sibling knobs.

### D4: Integration — a third check in the same `content_block_delta` arm

The new logic is added inside the existing `case "content_block_delta"` after the
two budget checks, and the ring is cleared in the existing
`case "content_block_start"` reset alongside `_blockDeltaCount`. Precedence among
the three signals is irrelevant to correctness (any one tripping aborts); ordering
byte → count → repetition keeps the cheapest checks first. `Begin()` initialises
the ring/dict/tail. No change to `Trip()`, `ResponseLeakError`, the DI
registration, or the detector's `Order`.

## Risks / Trade-offs

- **Whitespace tokenisation is crude for CJK** (no spaces) → a Chinese runaway
  could be one giant "token". Mitigation: the extracted-text is still split on
  whitespace, and the observed runaways were ASCII (`court`); CJK degeneration
  typically still emits repeated separators (`\n\n`, punctuation) that tokenise.
  Accepted as a known limit; the byte/count budgets remain the backstop, and the
  signal is additive (never suppresses the others).
- **A legitimate low-diversity block** (e.g. a 600-line ASCII bar chart of one
  repeated glyph) could trip. Mitigation: defaults are conservative (0.05), the
  window is per-block, and the knob disables it; such output is rare and the abort
  is a retryable `overloaded_error`, not a hard failure.
- **Per-token dictionary churn** for very long legitimate blocks → bounded to
  `RepetitionWindow` entries and O(1)/token; negligible next to the existing
  per-delta work. Off entirely when `RepetitionWindow <= 0`.
- **Token carry-over bug surface** (partial token across deltas). Mitigation:
  covered by a contract test that feeds the runaway split across delta boundaries
  exactly as the trace did (12-char fragments).

## Migration Plan

Additive and gated. Ship with defaults on (`RepetitionWindow=500`); operators who
see a false positive set `RepetitionWindow=0` and restart to fall back to the
prior byte/count-only behaviour. No data migration. Rollback = revert the commit
or set the window to 0.

## Open Questions

- Should the default ship **on** (window=500) or **off** (window=0) for the first
  release? Leaning on, given the 3-order-of-magnitude margin and that the abort is
  retryable — but this is the one operator-visible default worth confirming at
  apply time.
