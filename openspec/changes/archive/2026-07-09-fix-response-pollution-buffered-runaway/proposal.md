## Why

Three classes of response pollution reach the `/cc` client because the response-detector
framework has gaps against how Copilot actually replies. All three are grounded in real
`v0.4.4-beta` traces:

- **Runaway repetition leaks through on BUFFERED responses.** Copilot sometimes ignores
  `stream:true` and returns a one-shot `application/json` body (observed with `tool_use`
  turns). `RunawayGuardDetector` implements only the streaming `InspectEvent` path and
  inherits the base no-op for `InspectBuffered`, so on a buffered response **all three**
  runaway signals are dead code. Two captured responses (`count`×100 in a 108-token body;
  `court`×60 / 1320-total in a 1405-token body) degenerated into a single-token flood and
  were relayed to the client verbatim.
- **Short degenerate outputs are invisible to the repetition window.** The repetition
  signal only evaluates once the trailing-token window is **full** (`RepetitionWindow=500`).
  A 108-token body repeating one token 100× can never fill a 500-token window, so it is
  immune by construction — verified with a faithful port of the detector's tokenizer.
- **A control-envelope leak in a STREAMED response is detected too late to suppress.**
  Under the default `PreserveStream=true` the guard relays each `text_delta` as it arrives
  and can only inject an error after the closing tag is scanned — the leaked bytes are
  already on the client. A real trace streamed a `<system-reminder>…</system-reminder>`
  scaffolding leak to completion. (The signature itself is already detected on `main`;
  the residual is the streaming *suppression* gap, which is silent today.)

## What Changes

- **RunawayGuard runs on the buffered path.** `RunawayGuardDetector` gains an
  `InspectBuffered` implementation that parses the Anthropic body and scans each `text`
  (and, per `ScanThinking`, `thinking`) block through the same repetition/run-length logic,
  so a runaway in a one-shot `application/json` reply is caught with the same retryable
  abort as the streaming path.
- **New run-length runaway signal.** Add a per-block **consecutive-identical-token** signal
  that trips at `RepetitionMaxConsecutiveRepeat` identical tokens in a row (default ~50),
  independent of total length and of the sliding window — catching short floods the
  window-full gate misses. It runs on **both** the streaming and buffered paths, keyed off
  the same `text_delta`/`thinking_delta` (streaming) or text-block (buffered) content. The
  existing window signal is retained. A value of `0` (or less) disables it; false positives
  are resolved by raising the value in config.
- **Opt-in airtight streaming suppression for scannable leaks.** Add a
  `BufferScannableBlocks` mode to the response-leak guard: when enabled it holds back a
  scannable `text`/`thinking` block's deltas until the block completes, scans the assembled
  block, and relays it only if clean (aborting before any leaked byte is written) — while
  non-scannable blocks (e.g. `tool_use`) still stream live, preserving most TTFT. When
  disabled (default), behaviour is unchanged and the residual "detected-after-streamed" gap
  is documented rather than silent.
- **Observability covers runaway detection on both delivery modes.** A runaway trip SHALL
  emit exactly one `Warning` naming the tripped signal, the block, and the delivery mode
  (`stream` vs `buffer`), with no leaked content — mirroring the response-leak logging
  contract.
- Docs (`docs/pipeline-design.md` §6.1) and `appsettings.json` comments updated for the new
  signal and mode.

## Capabilities

### New Capabilities
<!-- none — all affected behaviour lives in existing capabilities -->

### Modified Capabilities

- `runaway-guard`: detection SHALL apply on the **buffered** (`application/json`) delivery
  path as well as streaming; a new **consecutive-identical-token** run-length signal
  (`RepetitionMaxConsecutiveRepeat`) trips independently of the sliding window and of total
  length, on both paths.
- `response-leak-guard`: a new opt-in `BufferScannableBlocks` delivery mode buffers a
  scannable block's deltas until block end so a detected leak is suppressed before any byte
  reaches the client, while non-scannable blocks stream live; default-off preserves current
  behaviour.
- `observability`: a runaway detection SHALL be logged at the detection point with exactly
  one `Warning` naming the tripped signal, block, and delivery mode, and no leaked content.

## Impact

- **Code**: `Pipeline/Response/Detection/RunawayGuardDetector.cs` (add `InspectBuffered`,
  refactor the per-block scan so streaming and buffered share it; add the run-length
  counter), `Hosting/Options/RunawayGuardOptions.cs` (new `RepetitionMaxConsecutiveRepeat`),
  `Pipeline/Response/Detection/ResponseLeakDetector.cs` + `ResponseInspectionStage.cs`
  (block-level buffering mode), `Hosting/Options/ResponseLeakGuardOptions.cs` (new
  `BufferScannableBlocks`), `src/CopilotBridge.Cli/appsettings.json` (new keys + comments).
- **Config surface**: two new keys, both default to preserve current behaviour
  (`RepetitionMaxConsecutiveRepeat` default ~50 = a new active check;
  `BufferScannableBlocks` default false = no behaviour change). Read at startup; restart
  required.
- **Wire behaviour**: buffered runaways now abort with the configured retryable signal
  (previously relayed); a new short-flood shape now trips. No change to a clean response's
  bytes. AOT-safe (all new JSON parsing via `JsonContext`; no reflection serialization).
  Detectors remain per-request scoped (no cross-request state).
- **Specs/docs**: `runaway-guard`, `response-leak-guard`, `observability` spec deltas;
  `docs/pipeline-design.md` §6.1.
