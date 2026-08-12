## Context

The bridge already solves downstream-idle watchdogs for Claude Code with `StreamIdleReader`: one pending upstream `MoveNextAsync` is raced against two absolute deadlines. The upstream-idle deadline is measured only from genuine upstream events; the downstream-keepalive deadline is measured from the last downstream activity. When the keepalive deadline wins, the same read remains pending and the upstream deadline is not recomputed.

Native Codex currently disables that second deadline. Codex 0.144.1 and current `openai/codex` main both default `stream_idle_timeout_ms` to 300,000 and wrap each parsed `eventsource().next()` in that timeout. The parser discards SSE comment lines before yielding, but a complete data event with an unknown `type` yields from `next()` and is then ignored by the Responses event dispatcher. A local bridge trace already captured the resulting failure shape: a native Responses stream was cancelled by the client after roughly five minutes while the bridge recorded no upstream timeout.

The native Codex path also preserves every upstream Responses event through a private T3/T4 ledger. A synthetic keepalive must not enter the semantic sequence associated with an upstream event, or the fidelity comparison will correctly treat it as an unauthorized mutation and fail closed.

## Goals / Non-Goals

**Goals:**

- Keep native Codex streams alive across healthy Copilot silence with the same timeout calculation, pending-read ownership, and cancellation semantics used for Claude Code.
- Make the downstream heartbeat an SSE-valid complete data event that Codex counts as activity and otherwise ignores.
- Preserve the bridge's upstream-idle authority, native Responses event fidelity, terminal guarantees, usage, and trace provenance.
- Keep one configuration interval for both downstream protocols and retain the zero-overhead disabled path.
- Prove the behavior with contract-derived unit tests and a real Codex run whose client idle timeout is shortened deterministically.

**Non-Goals:**

- Changing Codex's configured `stream_idle_timeout_ms` or disabling its watchdog.
- Treating a keepalive as upstream progress or extending the bridge's stream-idle budget.
- Sending a keepalive before the first upstream event, on buffered responses, or after a terminal.
- Defining a new official OpenAI Responses lifecycle event or changing Copilot's upstream behavior.

## Decisions

### Reuse the single pending-read race

`CopilotResponsesStrategy` will pass `KeepAliveIntervalSeconds` for both `/cc` and `/codex` downstream routes. It will continue to use the existing `StreamIdleReader`; no Codex-specific periodic task, cancellation source, clock, or deadline will be introduced.

This preserves the load-bearing mechanics already mutation-tested for Claude Code: a synchronous buffered event takes the allocation-free fast path; a waiting read is created once and carried across keepalive ticks; both deadlines use one `Stopwatch` sample; ties go to the upstream idle budget; a keepalive updates only downstream activity; and client cancellation wins over timeout classification.

Alternative considered: add an endpoint timer that writes pings independently. Rejected because it would duplicate deadline calculation, race concurrent writes against T4, and make it easy for downstream activity to accidentally reset upstream inactivity.

### Carry one identity-marked ping through IR and render it before native fidelity accounting

The existing `StreamKeepAlive.Ping()` event (`event: ping`, `data: {"type":"ping"}`) is a complete SSE data event. Both clients tolerate it: Claude Code explicitly discards `ping`; Codex yields it from its SSE parser, resets the per-event timeout, deserializes the arbitrary string `type`, and ignores the unknown kind.

T4 will recognize the bridge-owned ping by reference identity before adding ordinary IR events to the native semantic accumulator. It will yield that same event immediately and leave the ledger ordinal and accumulated semantic sequence untouched. Keeping the same payload instance also lets the Codex endpoint mark the final event `injected: true` without payload sniffing.

Alternatives considered:

- SSE comment `: ping`: rejected because `eventsource-stream` discards comments and empty-data frames, so Codex's timed `stream.next()` never completes.
- A fabricated `response.in_progress`: rejected because it would pretend to carry model lifecycle state and require synthetic response fields.
- Adding the ping to each native ledger group: rejected because a bridge event is not an upstream event and would corrupt the fidelity comparison.

### Preserve provenance at both capture layers

The raw upstream tee remains below injection and therefore cannot contain a synthetic ping. The Codex endpoint will set `CapturedSseEvent.Injected` using the same identity check as the Claude endpoint. Response inspection continues to relay keepalives live even while a scannable block is withheld.

### Share the existing configuration knob

`Pipeline:UpstreamTimeout:KeepAliveIntervalSeconds` applies to both streaming client protocols. A non-positive value disables the second deadline and injection everywhere; a value greater than or equal to the upstream idle budget remains coherent but ineffective because the idle budget wins first. No migration or new configuration key is required.

### Report Codex's end-to-end idle chain at startup

The startup report will read the active global Codex provider from `config.toml`. A provider counts as bridge-pointed only when its name, `/codex` base URL suffix, and Responses wire mode all match. It will report an integer `stream_idle_timeout_ms`, or the source-confirmed 300,000 ms Codex default only when the key is absent; wrong-type, negative, missing, malformed, or non-bridge configuration remains non-fatal and is shown as unknown.

Claude Code and Codex receive separate idle-gap rows because their configuration sources differ. Each row shows the bridge upstream-idle budget, the client watchdog, and the actual termination authority. Protection is computed per client: the interval must be positive, reach a live stream, and be strictly shorter than both the bridge budget and that client's watchdog. Only then is the client refreshed and the bridge budget authoritative; otherwise the shorter numeric bound wins, while an unknown client keeps the winner unknown. This makes the table an end-to-end calculation rather than a raw-value dump.

Alternative considered: print only `300s` as a note. Rejected because it would not show an explicit user override, could not identify which side actually ends the gap, and would repeat the misleading `min(client, bridge)` calculation while keepalives are active.

### Verify the actual Codex watchdog path without a five-minute test

The behavior harness will point a real Codex app-server at a deterministic Responses upstream, set that isolated provider's `stream_idle_timeout_ms` to a short test value, and make the upstream open a response, remain byte-silent beyond that value, then issue a real tool call and complete the next turn. The bridge keepalive interval is shorter and its upstream-idle budget longer. The run counts only if the trace contains injected pings spanning the silence, the tool call/output round-trip is present, the turn completes, and the client's own SQLite log contains no idle-timeout or router fatal.

## Risks / Trade-offs

- **Future Codex rejects unknown event types** -> The real-client behavior case pins parser acceptance; a failure blocks shipping. The event is isolated behind one renderer for replacement if the client contract changes.
- **Keepalive contaminates native semantic fidelity** -> T4 handles it before the native accumulator, and unit tests require every surrounding upstream event to remain value-identical.
- **Keepalives hide a dead upstream** -> The shared reader never updates the upstream timestamp on a keepalive; tests require `response.failed` at the original bridge deadline.
- **A buffering layer coalesces heartbeat bytes** -> Existing client timeout configuration remains a fallback where applicable, and the trace identifies whether pings were actually flushed.
- **More wakeups on long silent streams** -> The default is one small event per 15 seconds only after the stream starts and only while upstream is silent; disabling remains allocation-free.
- **Codex config is absent or belongs to another provider** -> Startup remains non-fatal and reports the Codex client contribution as unknown instead of guessing that the bridge provider is active.

## Migration Plan

The change is enabled by the existing default interval after upgrading and restarting the bridge. Rollback is either restoring the prior binary or setting `KeepAliveIntervalSeconds` to `0`, which returns native Codex streaming to the previous no-injection behavior.

## Open Questions

None. The wire behavior and timeout semantics are grounded in the installed Codex source and will be rechecked through the real client.
