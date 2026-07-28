## Context

Copilot's native Anthropic endpoint never sends `ping`. Verified: zero `ping`
events across 137 captured response traces, and a measured `claude-opus-5` turn at
`effort=xhigh` emitted `message_start` + a thinking-block `content_block_start` and
then nothing for **600 s** — confirmed against the raw upstream capture, so it is
upstream behaviour, not a bridge stall. The real Anthropic API pings through that
silence; Claude Code's watchdogs are calibrated for that world.

The prior change (`bridge-owns-timeouts`) addressed this by *raising the client's*
bounds via env keys, and `docs/timeout-chain.md` already records the two residual
gaps that approach cannot close: the env values need a client restart, and they
protect only clients the bridge configured. That doc also records the decision
*not* to synthesize keepalives, on the grounds that a silent upstream should stay
observably silent. **This change reverses that decision** and pays the
observability cost explicitly (see Decision 5).

### What Claude Code actually does with `ping` — verified, not assumed

Extracted from the running `claude.exe` 2.1.220 bundle (the decompiled
`claude-code-sourcemap` checkout is stale; the bytes below come from the shipped
binary):

```js
// event-level watchdog ②
for await (let pi of f1_(Le, mr)) {
  if (As(), P_r(pi)) { yield {type:"stream_event", event:pi}; continue }
  ...
}
function P_r(e){ return e.type === "ping" }
As = function(){ fi(); vs(); if(!St) return;
  sn = setTimeout(→ warn, bn);            // bn = Jt/2
  dt = setTimeout(→ Bo=!0; abort, Jt) };  // Jt = m7i() >= 300000
```

Three facts follow, and all three are load-bearing:

1. **`As()` runs before the type check**, so a `ping` resets watchdog ② — it
   re-arms `dt` for a fresh `Jt`.
2. **`ping` then `continue`s**, skipping every business branch: it cannot touch a
   content block, `Me`/`cr`, usage accumulation, or stall statistics.
3. The **byte-level watchdog ①** (`Axg`) wraps the raw body stream and re-arms in
   `pull()` on *any* chunk, so the ping's bytes feed it too. The UI layer
   (`JIs`) does `if (P_r(e.event)) return` — pings are silently dropped, no
   flicker.

So a synthesized `ping` is exactly the right instrument: it resets both client
watchdogs and is guaranteed inert everywhere else.

## Goals / Non-Goals

**Goals:**

- A healthy `/cc` turn is never ended by the *client's* idle watchdogs, regardless
  of how long Copilot thinks, and regardless of whether the bridge ever configured
  that client.
- The bridge's stream-idle budget remains the single authority that ends a stalled
  turn, and keeps measuring the true upstream gap.
- Zero cost when upstream is healthy: no timer, no allocation, byte-identical
  relay.
- An operator can still tell, from a trace, that upstream went silent.

**Non-Goals:**

- The Codex path (`/codex/responses`). A Responses-protocol keepalive is a
  different question — what `codex.exe` accepts as progress has not been probed —
  and mixing it in would double the verification surface. Codex keeps its existing
  `response.failed` terminal.
- Removing or relaxing the client env keys the bridge writes. They stay as a
  second line of defence (spec: *Client-side timeout configuration remains a
  second line of defence*).
- Changing what a fired stream-idle budget looks like to the client. Still
  `overloaded_error` by default.
- Bounding a *buffered* (non-streaming) response. There is nothing to inject into;
  that gap is unchanged and still unowned.

## Decisions

### Decision 1: Silence-triggered, not periodic

Inject only after the upstream has been quiet for `KeepAliveIntervalSeconds`, then
once per elapsed interval until it speaks again.

*Alternative considered — fixed heartbeat from stream start:* simpler (one timer,
armed once), and it is what the real Anthropic API appears to do. Rejected for two
reasons. It arms a timer on every stream including the 99% that never go silent,
violating the "zero overhead when healthy" property the existing timeout work
established; and it destroys the diagnostic — with pings everywhere, their
presence tells an operator nothing, whereas with silence-triggering a ping in the
trace *is* the record that upstream stalled.

### Decision 2: The keepalive tick lives in `StreamIdleReader`, not a background timer

`StreamIdleReader.MoveNextAsync` already races the pending upstream read against
an independent `Task.Delay` — it is precisely the place that knows "upstream has
been silent for X". Extend it to accept a keepalive interval and return a
three-state outcome (`Event` / `EndOfStream` / `KeepAliveDue`) instead of `bool`,
looping internally so the caller sees one `KeepAliveDue` per elapsed interval
without the read being disturbed.

*Alternative considered — a separate timer on the endpoint that writes pings
concurrently with the relay loop:* rejected. It introduces concurrent writes to
`HttpResponse.Body` from two tasks, which is not safe and would need a write lock
on the hot path; and it would need its own cancellation plumbing to stop at
stream end. The reader already owns the silence knowledge and the relay is
single-threaded through it — keep it that way.

The existing race-an-independent-delay structure is preserved verbatim, including
its stated reason (an arm/disarm `CancelAfter` on a reused CTS has a nanosecond
poison window). The keepalive delay is a second racer, not a replacement.

### Decision 3: Two independent deadlines, one read

The idle budget is measured from the last **upstream** event; the keepalive
deadline is measured from the last **injected ping or upstream event**. Both are
computed against the same pending read. Concretely: race the read against
`min(next keepalive due, idle deadline)`; on a keepalive win, emit the tick and
re-race with the *same* idle deadline (not a fresh one). This is what makes the
spec's load-bearing constraint — pings don't extend the budget — fall out of the
structure rather than depend on a discipline someone can later break.

If `KeepAliveIntervalSeconds >= StreamIdleTimeoutSeconds`, the idle deadline
always wins first and no ping is ever due. That is a coherent (if useless)
configuration and needs no special-casing.

### Decision 4: Injected as an in-band `SseItem`, flushed by the existing writer

The strategy's `StreamEventsAsync` yields a synthesized
`SseItem<string>("{\"type\":\"ping\"}", "ping")` on a `KeepAliveDue` tick. It then
flows through the normal path — outbound adapter, endpoint loop,
`WriteSseEventAsync` — which already flushes after every event. No new write path,
no new flush semantics.

Two guards this implies, both spec'd:

- **No ping before the first upstream event.** A stream that has not started must
  stay governed by the first-byte budget; injecting into it would make an unstarted
  stream look started to the client.
- **`UsageProbe` must not see it.** It keys off `message_start`/`message_delta`, so
  a `ping` is already inert — but the relay loop calls it unconditionally, so this
  needs a test asserting usage is unchanged, not an assumption.

Marker scrubbing in `ClaudeCodeOutboundAdapter` is a no-op for a ping (no markers
in the payload), so the adapter needs no change.

### Decision 5: Paying the observability cost explicitly

The current design deliberately preserves "silent upstream ⇒ observably silent."
Injection breaks that *for the client*, and would break it for the operator too if
pings landed in the trace unmarked. Two-part mitigation:

- The **raw upstream capture** (`RawResponseCapture` via `TeeReadStream`) tees the
  network stream *below* the injection point, so it is structurally incapable of
  containing an injected ping. No code change needed — but a test must pin it,
  because a future refactor moving injection lower would silently corrupt the
  meaning of that artifact.
- The **inbound-resp capture** records what the client received. `CapturedSseEvent`
  already carries a bridge-annotation flag (`Filtered`); add a parallel
  `Injected` flag rather than overloading it, so an operator reading the trace can
  see both "the bridge dropped this" and "the bridge invented this."

The operator's silence signal therefore *improves*: today silence is an absence
(you infer it from timestamps); after this change it is a positive record.

### Decision 6: Default interval

Default `KeepAliveIntervalSeconds = 15`. It must be comfortably under the
tightest client bound the bridge can encounter — Claude Code's byte-level
watchdog at **180 s** when `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` is set
(which the bridge's own 1M-context config sets, per `docs/timeout-chain.md`) — with
enough margin that a few dropped/buffered pings do not cost a turn. 15 s over a
600 s silence is 40 events of ~20 bytes; negligible. It also matches the order of
magnitude of the real Anthropic API's cadence, which is what CC's bounds were
tuned against.

## Risks / Trade-offs

- **An intermediary buffers the SSE stream and defeats the ping** (corporate proxy,
  compression layer) → the client env keys stay in place as the second line of
  defence; this is exactly why Decision/spec keeps them rather than removing them.
- **A future refactor lets an injected ping reset the idle budget**, silently making
  a hung upstream stream pings forever → the structural mitigation is Decision 3
  (one read, two deadlines, idle deadline never recomputed on a ping win), plus a
  from-contract test that a silence longer than the budget still fires the budget
  at the configured time regardless of how many pings were sent.
- **Injection masks a genuine bridge-side hang** — the bridge itself wedged, but the
  client sees a live stream → bounded by the same stream-idle budget, which is
  driven by the reader and unaffected by injection. Additionally the summary line
  still records `upstream_timeout=stream_idle` when it fires.
- **Non-Claude-Code Anthropic clients that mishandle `ping`** → `ping` is part of the
  documented Anthropic streaming protocol and defined as ignorable; a client that
  breaks on it is already broken against the real API. The knob can be set to 0 to
  disable for such a deployment.
- **Loss of the "silence is visible as a gap in the trace" property** → accepted and
  mitigated per Decision 5; a marked ping is a stronger signal than an inferred gap.

## Migration Plan

Additive and default-on with a conservative interval; no wire change when upstream
is healthy. Rollback is `Pipeline:UpstreamTimeout:KeepAliveIntervalSeconds = 0`,
which restores byte-identical prior behavior with no timer allocated (asserted by
a test). The startup timeout report should mention keepalive injection is active
so the operator is not surprised by pings in a trace, but the report's existing
bounds and undercut warning are unchanged.

## Open Questions

- Should the startup timeout report *soften* the client-undercut warning when
  keepalive injection is enabled (the undercut is now mitigated at runtime)? Leaning
  no — the warning describes a static misconfiguration that still bites if injection
  is later disabled, and softening it would encode "injection always works," which
  is precisely the assumption the second-line-of-defence spec refuses to make.
- Whether to expose an injected-ping counter on the request summary line. Cheap and
  useful for measuring real-world silence, but it is a new summary field with its
  own contract; deferrable to a follow-up unless it falls out for free.
