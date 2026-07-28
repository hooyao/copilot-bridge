## 1. Configuration surface

- [x] 1.1 Add `KeepAliveIntervalSeconds` (default 15) to `UpstreamTimeoutOptions`, with XML docs stating it is silence-triggered, that `<= 0` disables it and arms no timer, and that it must be shorter than the client's tightest watchdog (CC byte-level 180 s under `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`) to be useful.
- [x] 1.2 Document the knob in `src/CopilotBridge.Cli/appsettings.json` alongside the existing `Pipeline:UpstreamTimeout` entries, matching the surrounding `_Comment` style.

## 2. Reader: two deadlines over one read

- [x] 2.1 Change `StreamIdleReader.MoveNextAsync` to return a three-state outcome (`Event` / `EndOfStream` / `KeepAliveDue`) instead of `bool`, taking the keepalive interval and the caller-owned idle deadline.
- [x] 2.2 Race the pending read against `min(keepalive due, idle deadline)`; on a keepalive win return `KeepAliveDue` **without** recomputing the idle deadline and **without** disturbing the pending read, so the next call resumes the same read.
- [x] 2.3 Preserve the existing structure exactly where it matters: independent `Task.Delay` racers (never `CancelAfter` on the reused CTS — the nanosecond poison race), the synchronous fast path for an already-buffered event, client-cancel-wins-over-deadline, and cancel-then-await of the pending read on a genuine idle timeout.
- [x] 2.4 Keep `KeepAliveIntervalSeconds <= 0` and `>= StreamIdleTimeoutSeconds` on the no-keepalive path (idle deadline always wins first) with no keepalive timer allocated.

## 3. `/cc` injection

- [x] 3.1 In `CopilotMessagesPassthroughStrategy.StreamEventsAsync`, yield a synthesized `SseItem<string>("{\"type\":\"ping\"}", "ping")` on each `KeepAliveDue`, and only after at least one upstream event has been relayed (never before the stream's first event).
- [x] 3.2 Thread `KeepAliveIntervalSeconds` from `UpstreamTimeoutOptions` into the iterator alongside the existing `StreamIdleTimeoutSeconds`.
- [x] 3.3 Confirm the injected item is teed *above* `TeeReadStream`, so the raw upstream capture structurally cannot contain it; add a code comment pinning that ordering as load-bearing.
- [x] 3.4 Leave the Codex path (`CopilotResponsesStrategy`) unchanged; if `StreamIdleReader`'s signature changed, update its call site to the no-keepalive overload/argument only.

## 4. Trace annotation

- [x] 4.1 Add an `Injected` flag to `CapturedSseEvent` (parallel to `Filtered`, not overloading it) and mark bridge-synthesized pings with it in the `/cc` endpoint relay loop.
- [x] 4.2 Verify the inbound-resp trace artifact renders the flag; keep it absent/false for every upstream-originated event.

## 5. Tests — contract-first

- [x] 5.1 Reader: upstream silent past the keepalive interval yields one `KeepAliveDue` per elapsed interval, and the *same* pending read completes normally when upstream finally emits (no event lost, no read restarted).
- [x] 5.2 Reader: **the load-bearing test** — with keepalive shorter than the idle budget and upstream silent forever, the idle timeout fires at approximately `StreamIdleTimeoutSeconds` measured from the last upstream event, independent of how many keepalives were emitted.
- [x] 5.3 Reader: keepalive `<= 0`, and keepalive `>=` idle budget, both produce zero `KeepAliveDue` outcomes.
- [x] 5.4 Strategy: upstream emitting steadily ⇒ downstream event sequence byte-identical to the no-keepalive path, and no keepalive timer allocated.
- [x] 5.5 Strategy: no ping is emitted before the first upstream event.
- [x] 5.6 Endpoint: a relay containing injected pings reports usage identical to the same upstream stream without injection (`UsageProbe` untouched by pings).
- [x] 5.7 Trace: raw upstream capture contains no injected ping; inbound-resp capture marks each injected ping and no upstream event.
- [x] 5.8 Mutation-check every new test per the repo's testing directive — break the product code, confirm each goes red; a test that stays green asserts nothing.

## 6. Real-client verification (not optional)

- [x] 6.1 Build a `Kind=ClientBehavior` scenario driving real `claude.exe` through a `ServeProcess` bridge against an upstream that goes silent **longer than the client's 180 s byte watchdog** but shorter than the bridge's stream-idle budget, then resumes and completes.
- [x] 6.2 Run it with the client's timeout env keys **unset** (factory defaults) — that is the configuration this change is supposed to rescue; with them raised, a pass would prove nothing about the pings.
- [x] 6.3 Verdict from the CLIENT's own evidence per the `real-client-verify` skill: the turn completed and tool calls executed. A bridge-side 200 is INCONCLUSIVE, not a pass.
- [x] 6.4 Second scenario: upstream silent forever ⇒ confirm the bridge (not the client) ends the turn at its stream-idle budget, and the client sees the retryable error rather than an endless ping stream.

## 7. Documentation

- [x] 7.1 Update `docs/timeout-chain.md` §"Why the silence happens at all": the "bridge deliberately does not synthesize keepalives" decision is reversed — record the new decision, why, and how the observability cost is paid (marked pings).
- [x] 7.2 Note in the timeout-chain doc that the client env keys remain a second line of defence, and why both mechanisms coexist.
- [x] 7.3 Mention keepalive injection in the startup timeout report so an operator is not surprised by pings in a trace (leave the existing bounds and undercut warning unchanged).
- [x] 7.4 Re-check `CLAUDE.md` / `AGENTS.md` for anything the change invalidates; mirror any edit across both. (Checked: neither mentions keepalives or the no-ping decision — nothing invalidated, no edit needed.)
