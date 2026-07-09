## Context

The `/cc/v1/messages` forward path (`CopilotMessagesPassthroughStrategy`) calls
`CopilotClient.PostMessagesAsync`, which does
`http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)` inside an
idempotent transient-retry loop, then either buffers the body or hands an
`IAsyncEnumerable<SseItem<string>>` (`StreamEventsAsync`) back to the endpoint,
which relays each event to the client.

Two facts define the problem:

1. Under `ResponseHeadersRead`, `HttpClient.Timeout` (currently 10 min, set in
   `BridgeServiceCollectionExtensions`) bounds **only** the wait for response
   headers. It does not apply to the SSE body reads that happen after
   `SendAsync` returns.
2. `StreamEventsAsync` reads with `SseParser.EnumerateAsync(ct)` where `ct` is
   `httpCtx.RequestAborted` — so the only thing that can interrupt a mid-stream
   stall is the client cancelling or the socket dying.

Observed incident (traces in repo, seq 0918/0919/0920): three near-full-`[1m]`
opus-4.8 requests blocked in `SendAsync` waiting for a first byte for 4.4 / 5.0 /
0.8 min, then Claude Code cancelled at ~5 min — before the 10-min
`HttpClient.Timeout` fired. The sibling that eventually succeeded (0921) shows
`cache_creation_input_tokens=499300`, i.e. a legitimately expensive first byte.
The reference implementation forwards with a bare `fetch` and no timeout, so
there is no prior art to copy a default from.

Constraints: Native-AOT (no reflection, no non-trim-friendly deps); the
byte-identical `/cc` passthrough hot path must not regress; disabled budgets must
add zero timer/allocation overhead.

## Goals / Non-Goals

**Goals:**
- Bound the first-byte wait with an inactivity budget applied per send attempt,
  so a stalled upstream yields a clean `504` instead of hanging until the client
  or the coarse 10-min timeout gives up.
- Bound mid-stream gaps with an inactivity budget reset on every upstream event,
  so a stream that stops emitting is aborted within the budget.
- Make both budgets independently tunable and disable-able (`<= 0`), with the
  disabled path allocating no timer and staying byte-identical.
- Report a fired budget as an upstream timeout — distinct from client
  cancellation — on both the summary line and the operator log.

**Non-Goals:**
- A total-duration cap. This is strictly an *inactivity* bound; a request that
  keeps making progress is never aborted, however long it runs.
- Changing the Codex/Responses translation path is now **in scope** (both budgets),
  via a shared first-byte helper and the Codex strategy's existing fault channel —
  see D8. What stays out of scope: the buffered (non-streaming) read on either path
  and any total-duration cap.
- Re-sending from the bridge on timeout. The bridge never re-issues the upstream
  call itself (re-sending a slow upstream just times out again). Driving a *client*
  retry is in scope for the mid-stream case — see D6: a mid-stream timeout injects
  the same retryable `overloaded_error` the guards use, which Claude Code re-attempts.
- Removing the 10-min `HttpClient.Timeout`. It stays as the coarse outer
  backstop.

## Decisions

### D1 — Inactivity (idle) timeout, not total duration

Reset-on-progress is the only bound that doesn't punish the legitimate
near-full-context request (0921) while still catching a true stall. A total cap
would have to be set above the slowest legitimate request, which is exactly the
minutes-scale first byte we cannot distinguish from a hang by duration alone —
but *can* distinguish by lack of progress.

### D2 — Two separate budgets (first-byte vs. stream-idle)

A legitimate time-to-first-byte is minutes (cache creation over ~500k tokens); a
legitimate mid-stream gap is sub-second (token deltas, including during extended
thinking, which still emits deltas). One knob cannot serve both without either
letting a mid-stream hang run for minutes or killing a slow first byte. So:
`FirstByteTimeoutSeconds` (generous) and `StreamIdleTimeoutSeconds` (tight).

- **Alternative considered — single knob:** rejected; forces the tight bound to
  the loose one's value or vice-versa.

### D3 — First-byte budget is per-attempt, inside `PostMessagesAsync`

Thread the budget into `PostMessagesAsync`; inside the retry loop, wrap each
`http.SendAsync` in a `CancellationTokenSource.CreateLinkedTokenSource(ct)` with
`CancelAfter(budget)`. A fired timer throws a dedicated `UpstreamTimeoutException`
(first-byte phase), classified so it is **not** retried by the transient loop.

Rationale: arming per iteration means each fresh send gets the full budget and
inter-attempt backoff never counts against it — the cleanest realization of the
spec's "each fresh send is granted the full budget." A slow first byte throws no
exception (it is just a slow await), so in the dominant incident case there is
exactly one `SendAsync` and one timer; the retry loop only ever engages on
fast-failing connection exceptions, where this placement is still correct.

- **Alternative considered — one `CancelAfter` around the whole
  `PostMessagesAsync` call in the strategy (no signature change):** simpler, and
  behaviorally identical in the no-exception incident case, but its budget spans
  all retries + backoff, so it cannot honor "full budget per fresh send." Since
  the timer belongs conceptually next to the `SendAsync` it bounds, per-attempt
  wins despite the one-parameter signature growth.

### D4 — Stream-idle budget: arm before each upstream read, disarm before yield

In `StreamEventsAsync`, enumerate the parser manually: `CancelAfter(budget)`
immediately before each `MoveNextAsync` (the wait on upstream), and
`CancelAfter(Timeout.InfiniteTimeSpan)` after an event arrives and before
`yield return`. This measures only time spent *waiting on upstream*, not the time
the downstream consumer spends writing/flushing the event — so a slow client
never trips the upstream budget. A fired timer throws
`UpstreamTimeoutException` (stream-idle phase). The `yield return` sits outside
the `try/catch` (C# forbids yielding inside a `try` with a `catch`), which the
arm/read/disarm/yield shape satisfies naturally.

### D5 — Distinguish timeout from client cancel by inspecting the outer token

Both fire as `OperationCanceledException` on the *linked* token. The throw sites
convert to `UpstreamTimeoutException` only under
`when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)`. If the
client's `ct` is also cancelled, the original OCE propagates unchanged and is
caught by the endpoint's existing `catch (OperationCanceledException) when
(ct.IsCancellationRequested)` branch — client cancel wins the race, preserving
today's semantics. `UpstreamTimeoutException` is a plain `Exception` (not a
`TaskCanceledException`), so `TransientUpstreamError.Is` does not classify it as
transient and the generic catch will not mislabel it.

### D6 — Endpoint mapping: 504 before headers, retryable error mid-stream

Add one `catch (UpstreamTimeoutException ex)` to `ClaudeCodeMessagesEndpoint`
ahead of the transient/generic catches:
- `!httpCtx.Response.HasStarted` (first-byte): status `504 Gateway Timeout`,
  `summary.Error` names the phase + elapsed idle, one `WARN` (no stack).
- else (mid-stream, headers already sent): status cannot be rewritten. The
  natural surface is **not** a bare truncation but the same retryable
  `overloaded_error` SSE event that `RunawayGuard`/`ResponseLeakGuard` inject
  (`ResponseInspectionStage`'s abort path) — a mid-stream stall is
  functionally the same situation those guards handle (headers already `200`, a
  bad/absent continuation). Verified against decompiled Claude Code
  (`claude.ts:2404` catch): a mid-stream error is re-attempted — under
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1` (which the bridge's own `config`
  command writes when a `PreserveStream=true` detector is active) it propagates
  to `query.ts`'s retry path as a **whole-turn retry** (and, after 3 consecutive
  529s, the opus→Sonnet fallback); with the flag unset it drives a non-streaming
  fallback re-request. Either way the stalled turn is re-attempted, not left as a
  silent partial. `summary.Error` names the phase + elapsed; `WARN` (no stack).

Because the mechanism is exactly the guards' abort *wire shape*, the mid-stream
error JSON is built with the same `ResponseDetectionError.JsonWithMessage(signal,
message)` the guards call, and written as an SSE `error` event followed by end of
stream — byte-identical to a RunawayGuard trip. Note it cannot be routed *through*
`ResponseInspectionStage`'s `DetectionActionKind.Abort`: that stage is pull-based
(it inspects events that arrive) and an idle gap is the absence of an event, so
the detector traversal never sees it. The reuse is the shared error builder, not
the detector path.

- **Alternative considered — plain truncation (cut the stream, no error
  event):** rejected as the default because it leaves a silent partial that the
  self-reinforcing-poisoning concern (`docs/pipeline-design.md`) warns against,
  and it forgoes the retry the flag already enables. Offered instead as a config
  option (a `Signal`/action knob mirroring the guards) for operators who
  explicitly prefer a truncation over a retry.

Add a machine-greppable token to the summary line (e.g. `upstream_timeout=first_byte|stream_idle`, absent when none) alongside the existing `response_leak=` / `runaway=` fields.

### D7 — Defaults grounded in the incident, both disable-able

- `FirstByteTimeoutSeconds = 240` (4 min): under the 10-min `HttpClient.Timeout`,
  above a realistic cache-creation first byte, and gives a clean `504` before the
  client's ~5-min opaque cancel. Tunable; an operator seeing legitimate >4-min
  first bytes raises it.
- `StreamIdleTimeoutSeconds = 60`: far above any legitimate inter-event gap
  (deltas are sub-second; extended thinking still emits deltas), so only a true
  stall trips it.
- `<= 0` disables either budget (no linked CTS, no `CancelAfter`).

These are documented as tunable, not load-bearing constants — see Risks.

### D8 — Codex path: shared first-byte helper + reuse the existing fault channel

Both budgets apply to the Codex/Responses path too, but the *mechanism* differs
from `/cc` because the Codex strategy translates the stream and already has a
fault path:

- **First-byte:** `PostMessagesAsync` and `PostResponsesAsync` have identical
  `SendAsync(ResponseHeadersRead)` + transient-retry loops. Extract the arm/
  disarm/throw logic into one private helper on `CopilotClient`
  (`SendWithFirstByteBudgetAsync`) and call it from both. Codex first-byte timeout
  then throws `UpstreamTimeoutException(FirstByte)` exactly like `/cc`; it reaches
  `CodexResponsesEndpoint` before headers and maps to a 504 (the existing generic
  catch already returns 502 for pre-header exceptions — add the dedicated
  `UpstreamTimeoutException` catch there, mirroring `/cc`, to make it a 504 + the
  summary field).
- **Stream-idle:** `CopilotResponsesStrategy.TranslateStreamAsync` already iterates
  the parser manually and **catches** a mid-stream fault into `fault`, then flushes
  a `response.failed` terminal (`sm.FlushTerminal(failed: true)`) and latches
  `ctx.Response.UpstreamStreamFault`. So the idle timer slots directly into that
  loop: arm before each `MoveNextAsync`, disarm after; on our timer firing set
  `fault = new UpstreamTimeoutException(StreamIdle, …)` and `break` — the existing
  code then flushes `response.failed` and surfaces the fault. The endpoint's
  existing `UpstreamStreamFault` handling (CodexResponsesEndpoint.cs:206) already
  folds it into the audit error; extend it to set
  `summary.UpstreamTimeout = "stream_idle"` when that fault is an
  `UpstreamTimeoutException`.

Rationale: the Codex client speaks Responses, so injecting an Anthropic
`overloaded_error` (D6's `/cc` surface) would be a protocol mismatch. Reusing the
already-tested `response.failed` fault channel keeps the Codex client seeing a
well-formed terminated stream and avoids a second, parallel error path.

- **Alternative considered — a `/cc`-style raw throw out of the Codex stream:**
  rejected. It would bypass `FlushTerminal`, leaving the Codex client with a
  headless (unterminated) stream — the exact failure the existing catch-and-flush
  was built to prevent.

## Risks / Trade-offs

- **A default first-byte budget kills a legitimate slow request** → 240s is under
  the client's own ~5-min cancel and above the observed cost, so the realistic
  outcome is a clean `504` slightly before the client would have cancelled
  opaquely anyway; the exact 0921 TTFB is unknown, so the value is a documented
  tunable, not a hard guarantee. Operators can disable (`0`) to restore the pure
  10-min backstop.
- **Manual parser enumeration diverges from the `await foreach` passthrough** →
  when the budget is disabled the code takes the original no-wrapper path
  (`SseParser.EnumerateAsync(ct)` directly), so the byte-identical hot path is
  literally unchanged; the manual loop is used only when a budget is armed. A
  round-trip test asserts event-sequence identity with the budget enabled but
  upstream responsive.
- **Timer/cancel race mislabels a client cancel as a timeout** → the
  `!ct.IsCancellationRequested` guard at the throw site makes client-cancel win
  ties (D5); a contract test drives both-cancelled and asserts "client
  cancellation," not "timeout."
- **Threading a param into `PostMessagesAsync` touches the retry loop** →
  minimal: one linked CTS per iteration, first-byte timeout classified terminal
  so the loop's transient predicate is unaffected; existing
  `CopilotClientRetryTests` guard the retry contract and must stay green.
- **Disarm-before-yield lets a truly frozen consumer mask an upstream stall** →
  acceptable and correct by design: while the consumer holds the event, the
  client is by definition still connected; if the client goes away, `ct`
  (`RequestAborted`) fires and the existing cancel path handles it.

## Migration Plan

Additive and behind grounded defaults. Deploy: ship the new options bound from
`Pipeline:UpstreamTimeout` (present in `appsettings.json` with a verbose
`_comment`, read at startup like the sibling detector/retry options). Rollback:
set both values to `0` to fully disable, restoring today's behavior (only the
10-min `HttpClient.Timeout`). No data migration, no wire-format change.

## Open Questions

- **First-byte default (240s):** confirm against a measured near-full-context
  TTFB if one becomes available; adjust the documented default rather than the
  mechanism.
- **Coordinate the stream-idle default with Claude Code's own watchdog.** Claude
  Code has a client-side stream idle watchdog: `CLAUDE_ENABLE_STREAM_WATCHDOG` +
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS` (default 90s, `claude.ts:1874-1927`), whose
  abort feeds the same `catch (streamingError)` → retry/fallback path. So a
  mid-stream stall may already be caught client-side *if the user enabled the
  watchdog*. The bridge's stream-idle budget is still worth having (it does not
  depend on an opt-in client env var, and it can inject the retryable error
  directly), but its default should sit at or below 90s so the bridge is the
  earlier, deterministic actor rather than racing an optional client timer —
  confirm the chosen `StreamIdleTimeoutSeconds` (proposed 60s) against this.
- **Mid-stream surface: settled — endpoint synthesizes using the shared wire
  builder.** The detector framework (`ResponseInspectionStage`) is **pull-based**:
  `InspectEvent` runs only on events that *arrive*, so it structurally cannot
  fire on the *absence* of an event (an idle gap). The idle timeout therefore
  fires at the read site (`StreamEventsAsync` for stream-idle, `PostMessagesAsync`
  for first-byte), throws `UpstreamTimeoutException`, and the endpoint's single
  `catch` maps it. For a mid-stream trip the endpoint writes the retryable error
  event using the **same wire-shape builder the guards use**
  (`ResponseDetectionError.JsonWithMessage`), so the bytes are identical to a
  RunawayGuard trip even though the trigger (a gap) is invisible to the detectors.
  This is reuse of the shared error shape, not of the detector traversal.
- **Codex/Responses parity:** now in scope — see D8. Both budgets apply; the Codex
  mid-stream surface is a `response.failed` terminal via the existing fault channel,
  not the `/cc` `overloaded_error` injection.
- **Summary field shape:** whether `upstream_timeout=` is a single phase token or
  two booleans — settle during apply to match the existing summary-logger idiom.
