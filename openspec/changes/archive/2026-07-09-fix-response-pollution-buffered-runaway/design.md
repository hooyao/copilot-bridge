## Context

The `/cc` Anthropic passthrough response pipeline runs an ordered set of
`IResponseDetector`s inside `ResponseInspectionStage`. Each detector exposes a
streaming entry (`InspectEvent(SseItem)`) and a buffered entry
(`InspectBuffered(byte[])`); the stage picks the path from `ctx.Response.Mode`, which
`CopilotMessagesPassthroughStrategy` sets from the upstream `Content-Type`
(`text/event-stream` → Streaming, else Buffered). Detector precedence is registration
order: `DoneFilter(0)`, `ModelRewrite(1)`, `ResponseLeak(2)`, `RunawayGuard(3)`,
`ToolInputValidation(4)`.

Diagnosis of real `v0.4.4-beta` traces (evidence in the gitignored `diag-captures/`)
found three pollution classes reaching the client:

1. **Runaway `court`/`count` on buffered responses.** Copilot sometimes ignores
   `stream:true` and returns a one-shot `application/json` body (correlated with a
   `tool_use` turn + the `x-github-copilot-request-te: true` experiment bucket). On
   that path `RunawayGuardDetector` — which overrides only `InspectEvent` — does
   nothing, because it inherits the base no-op `InspectBuffered`. All three runaway
   signals key off `content_block_delta`, absent from a buffered body.
2. **Short flood invisible to the window.** The repetition-density signal only
   evaluates once the trailing window is *full* (`RepetitionWindow=500`). A
   108-token body repeating one token 100× can never fill it (verified by a faithful
   port of `FeedRepetition`).
3. **`<system-reminder>` leak detected after streaming.** The signature is already
   detected on `main` (`SystemReminderMatcher`, #30), but under the default
   `PreserveStream=true` the streaming path relays each `text_delta` live, so
   detection only lands after the closing tag — the leaked bytes are already on the
   client (`ResponseInspectionStage.cs:249-250` admits this).

A detector audit confirmed `RunawayGuard` is the ONLY detector erroneously missing
`InspectBuffered`; `DoneFilter`'s absence is correct (`[DONE]` is SSE-only).

Constraints: .NET 10 Native AOT (all JSON via `JsonContext`, no reflection
serialization); detectors are per-request scoped services (no cross-request state);
config is read at startup (`reloadOnChange:false`); tests are written from the
contract, not by mirroring the implementation.

## Goals / Non-Goals

**Goals:**

- Runaway detection catches a buffered (`application/json`) runaway with the same
  retryable abort and `runaway=true` flag as the streaming path.
- A short single-token flood that never fills the window is caught by a new
  run-length signal, on both delivery paths.
- Offer an opt-in path to *suppress* (not merely flag) a scannable-block leak on the
  streaming path before its bytes reach the client, and make the default residual gap
  explicit.
- Streaming and buffered runaway scans share one per-block scanning core so they
  cannot drift.

**Non-Goals:**

- Changing the default streaming behaviour of the leak guard (block-buffering is
  opt-in; default stays relay-until-detection).
- Forcing SSE upstream or otherwise preventing Copilot from returning a buffered
  body — the fix is to handle the buffered body, not to eliminate it.
- Re-tuning the existing `RepetitionWindow`/`RepetitionMinUniqueRatio` defaults
  (the window signal is retained as-is; the short-flood case is covered by the new
  run-length signal instead).
- Deduplicating a runaway abort against a leak abort — `ResponseLeak` already has
  precedence (order 2 < 3); no ordering change is needed.

## Decisions

### D1 — RunawayGuard `InspectBuffered` reuses one per-block scan core

Extract the per-block repetition/run-length scan into a private core that consumes a
sequence of text fragments and signals a trip. `InspectEvent` feeds it each
`text_delta`/`thinking_delta`; the new `InspectBuffered` parses the Anthropic
Messages body with `JsonDocument`, and for each `text` and `thinking` block
(unconditionally — RunawayGuard has no `ScanThinking` gate, matching the streaming
path which feeds both `text_delta` and `thinking_delta`) resets per-block state and
feeds the whole block string through the
same core. This mirrors `ResponseLeakDetector`'s existing streaming/buffered split
(a shared `BuildAutomaton` fed by both paths), so the two paths cannot diverge.

- *Why not a second buffered-only algorithm?* Duplicated logic drifts; the whole bug
  class here is one path being weaker than the other.
- *Fail-open:* an unparseable body (or one with no `content` array) returns `None` —
  a scan must never turn a real response into an error on a parse hiccup, matching
  `ResponseLeakDetector.InspectBuffered`.
- *AOT:* parse with `JsonDocument` (already used by peers), no reflection-based
  deserialization; no new `JsonContext` type required for a read-only `content[]`
  walk.

### D2 — Run-length signal as a bounded counter, path-agnostic

Add a per-block "consecutive identical token" counter: track the previous token and a
count; an identical token increments, a different token resets to one; trip at
`RepetitionMaxConsecutiveRepeat`. It shares the same tokenizer/`_tokenTail`
reassembly as the window signal (so a run split across deltas — including a token
split at its terminating whitespace — is counted correctly) and is evaluated on every
pushed token in the same loop, so it costs one comparison per token and no extra
allocation. Because it does not depend on the window being full, it works on short
bodies and identically on the buffered path.

- *Why run-length, not "lower the window"?* The user's directive is that ~50
  consecutive repeats should trip while a false positive is acceptable (raise the
  knob). Run-length expresses exactly that intent and is orthogonal to window size,
  so the two signals cover different shapes (short-consecutive vs. long-diverse-looking
  low-ratio) without one weakening the other.
- *Default:* `RepetitionMaxConsecutiveRepeat = 50` (a new active check). `0`/negative
  disables it. It is deliberately low; a legitimate 50-in-a-row token run is itself
  degenerate output for this proxy, and the escape hatch is the config knob.
- *Interaction with the window signal:* both retained; first to trip wins (same abort
  either way). The previous-token comparison uses the same span-keyed reuse the ring
  already does, so no per-token string alloc on the degenerate path.

### D3 — `BufferScannableBlocks`: block-level withhold-then-relay in the stage

The current stage has two shapes: relay-live (`WrapStream`) and buffer-the-whole-
response (`BufferThenDeliverAsync`, chosen when any detector's `RequiresBuffering` is
true → real HTTP status). Airtight streaming suppression needs a *third* shape:
**per-block** withholding. When `BufferScannableBlocks` (and `PreserveStream`) is on,
`WrapStream` holds a scannable block's `content_block_delta` events in a small buffer
from `content_block_start` until `content_block_stop`; it feeds the leak detector
live as today, and at block end either (a) a leak was detected → abort with the SSE
error, having emitted none of the block's deltas, or (b) clean → flush the buffered
`content_block_start` + deltas + `content_block_stop` in order and continue.
Non-scannable blocks (`tool_use` etc.) are never withheld — their events pass through
live, preserving TTFT for content that cannot carry a text leak.

- *Why per-block, not whole-response buffering?* Whole-response buffering
  (`PreserveStream=false`) already exists and gives a real HTTP status, but it
  sacrifices streaming for *every* request. Per-block withholding pays latency only
  on scannable blocks and only until each block ends — a much smaller TTFT cost,
  opt-in for operators who want airtight suppression.
- *Scope of the buffer:* only the current scannable block's events, bounded by the
  block; not the whole response. `tool_use` argument streams (the large ones) are
  never withheld.
- *Default off:* keeps today's behaviour and TTFT; the residual "bytes may already be
  on the client" property is documented in the option/spec rather than silent.
- *Where it lives:* `ResponseInspectionStage.WrapStream` gains the block-buffer branch,
  gated by a stage-visible flag derived from the leak detector's options; the detector
  itself is unchanged beyond exposing the option. Runaway detection is unaffected by
  this mode (a runaway is volume/repetition, not a discrete closed envelope; its abort
  semantics are unchanged).

### D4 — Observability parity for runaway

`RunawayGuard.Trip` already logs one `Warning`. Extend it to carry the delivery mode
(`stream`/`buffer`) like the leak guard does, so a buffered-path trip is
distinguishable in logs, still naming the reason and never the runaway content, and
carrying the request trace id via the existing scoped logger.

## Risks / Trade-offs

- **[Run-length default of 50 causes a false positive on legitimate repetition]** →
  Documented knob `RepetitionMaxConsecutiveRepeat`; raising it (or `0` to disable) is
  the escape hatch, consistent with the user's "false positive is acceptable"
  directive. The signal only proxies degenerate output; 50 identical *tokens* in a row
  (not characters) is already well outside normal prose.
- **[Block-buffering adds TTFT latency per scannable block]** → Opt-in
  (`BufferScannableBlocks=false` by default); only scannable blocks are withheld and
  only until block end; `tool_use` streams live. Operators trade TTFT for airtight
  suppression consciously.
- **[Block-buffer holds an unbounded scannable block in memory]** → A single content
  block's deltas are bounded by the same forces the runaway guard itself caps
  (`MaxDeltaBytes`/`MaxDeltaCount` still run and abort first on a truly unbounded
  block); the buffer holds only the current block, released at block end.
- **[Buffered runaway scan double-parses the body]** → `RunawayGuard.InspectBuffered`
  parses once with `JsonDocument`; peers already parse the same body independently
  (each detector owns its scan). The cost is one extra parse of an already-in-memory
  buffer on the buffered path only, which is rare (Copilot mostly streams).
- **[AOT: new JSON handling]** → Read-only `JsonDocument` walk, no reflection
  serialization, no new serializable DTO — same pattern the leak/model-rewrite
  detectors already use under AOT.

## Migration Plan

1. Add config keys with behaviour-preserving-or-intended defaults
   (`RepetitionMaxConsecutiveRepeat=50` active; `BufferScannableBlocks=false`
   inert), with `appsettings.json` comments. Read at startup; restart required.
2. Land `RunawayGuard.InspectBuffered` + run-length signal behind the existing
   `Enabled` master switch — no new enable gate, so an operator who has the guard on
   gets the buffered fix automatically.
3. `BufferScannableBlocks` is additive and default-off; no rollback needed for the
   common case. To roll back any piece: set `RepetitionMaxConsecutiveRepeat=0`
   (disable run-length), or `RunawayGuard:Enabled=false` (disable the whole guard),
   or leave `BufferScannableBlocks=false` (no block buffering).
4. Update `docs/pipeline-design.md` §6.1 and the two option docstrings as durable
   architecture.

## Open Questions

- Should `BufferScannableBlocks` also withhold `thinking` blocks, or only `text`?
  **Resolved:** withhold both `text` and `thinking` **unconditionally** — the stage's
  `IsScannableBlock` returns true for `text`/`thinking` regardless of `ScanThinking`.
  `ScanThinking` gates only whether a withheld `thinking` block is *scanned* for a leak,
  not whether it is withheld; withholding a block the guard won't scan only adds latency
  (it flushes clean at block end, no correctness change) and keeps the stage free of
  per-detector scan-config coupling.
- Default of `RepetitionMaxConsecutiveRepeat`: 50 per the user's steer; confirm during
  apply against any known-legitimate repetitive output (e.g. a long ASCII table or a
  deliberate `"a"*N` test) — if such a case is realistic, document raising the knob
  rather than lowering the default's aggressiveness.
