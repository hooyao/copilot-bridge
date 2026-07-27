# Timeouts along the Claude Code → copilot-bridge → Copilot chain

Reference for the "a deep-thinking turn times out" failure: what bounds a turn,
which knob moves what, and how to diagnose a recurrence. Everything here is
**measured** — from the running `claude.exe` (2.1.220), the bridge's own logs and
request traces, and a controlled lab that drives the real client against an
upstream that goes silent on demand.

## The shipped model

**The bridge owns the timeouts.** Its two inactivity budgets are the only bound
that should ever fire; the client is configured to outlast them.

1. **Two bridge budgets** (`Pipeline:UpstreamTimeout`) bound *inactivity*, not
   total duration — a slow-but-progressing turn is never aborted:
   - `FirstByteTimeoutSeconds` — waiting for response headers. Also bounds the
     **buffered** (non-streaming) path end-to-end.
   - `StreamIdleTimeoutSeconds` — the gap between consecutive SSE events.
2. **No coarse HTTP cap.** The shared upstream `HttpClient` uses
   `Timeout.InfiniteTimeSpan`. Setting **both** budgets `<= 0` therefore leaves
   upstream calls genuinely unbounded — there is no backstop behind them.
3. **The client's idle bound is derived from the bridge.** `config claude-code`
   force-writes `CLAUDE_STREAM_IDLE_TIMEOUT_MS` as `budget + 5 min` (clamped to
   what the client actually honors). Change a budget → re-run `config claude-code`;
   until you do, `config status` reports drift.
   `API_TIMEOUT_MS` is written as a fixed generous maximum instead, **not**
   derived — see [the one bound the bridge cannot own](#the-one-bound-the-bridge-cannot-own).
4. **Startup says what is really in force**, and warns when the client would win:

```
Timeouts:  bridge first-byte 900s, stream-idle 600s (Pipeline:UpstreamTimeout — idle budgets, not total caps)
Timeouts:  Claude Code stream-idle 15m, request 60m (global client env — applies on Claude Code's next start)
Timeouts:  effective end-to-end bound 10m (bridge stream-idle)
```

A `WARNING` naming a `CLAUDE_*` key means the client aborts first and the bridge's
budget never applies. It fires for a **missing** key too — absence is not benign
(see below).

### The one bound the bridge cannot own

`API_TIMEOUT_MS` is a **wall-clock** cap on the whole request, while the bridge's
budgets bound **inactivity**. Those are different quantities, and no value of the
first can be guaranteed to outlast the second:

- A healthy turn that keeps emitting has **no total duration** — the stream-idle
  timer resets on every event, so the turn can legitimately run for hours.
- Even a stalled turn can spend the first-byte budget *and then* one or more
  stream-idle gaps before any bridge timer fires. With first-byte 900 s and
  stream-idle 600 s the bridge may legitimately take ~1500 s, so a derived
  `max(900,600)+300 = 1200 s` would have the **client** abort first.

So the bridge writes a fixed generous maximum (60 min) rather than deriving one,
and the startup report calls it what it is — a residual bound the bridge cannot
out-wait:

```
Timeouts:  API_TIMEOUT_MS = 60m is a wall-clock cap on the whole request, not an
           inactivity budget — the bridge cannot out-wait it, so a turn running
           longer than this ends at the client regardless of the budgets above
```

It is still worth raising from the client's own default, because it *also* bounds
each attempt of Claude Code's non-streaming recovery request (default 300 s) —
that path is a single bounded response, so a higher ceiling genuinely helps there.
Deriving it would only have moved the threshold while implying a guarantee that
cannot exist.

### Why the silence happens at all

**Copilot sends no keepalive while a model is thinking** — verified: zero `ping`
events across 137 captured response traces. Extended reasoning puts nothing on
the wire for minutes: a measured `claude-opus-5` turn at `effort=xhigh` emitted
only `message_start` + a `content_block_start` opening a thinking block, then
**nothing for 600 s** (confirmed from the raw upstream capture, so it is upstream
behaviour, not a bridge stall). Every bound below is really a bet on how long that
silence may legitimately last — which is why the stream-idle default is 240 s and
why deep workloads need more.

The bridge deliberately does **not** synthesize keepalives today: a silent
upstream stays observably silent, so a genuine hang is distinguishable from deep
thinking. Injecting `ping` (what the real Anthropic API does) is a separate
change; it would remove both remaining gaps — the client-restart requirement and
clients the bridge never configured.

## The eight bounds

| Layer | Bound | Default | Knob |
|---|---|---|---|
| CC ① byte-level idle watchdog | 180 s silence | on for bridge users | `CLAUDE_STREAM_IDLE_TIMEOUT_MS` |
| CC ② event-level idle watchdog | 300 s silence (floor) | on | `CLAUDE_STREAM_IDLE_TIMEOUT_MS` |
| CC ③ SDK whole-request | 600 s | on | `API_TIMEOUT_MS` |
| CC ④ non-streaming fallback | 300 s per attempt | on | `API_TIMEOUT_MS` |
| Bridge ⑤ first-byte idle | 240 s | on | `FirstByteTimeoutSeconds` |
| Bridge ⑥ stream-idle | 240 s | on | `StreamIdleTimeoutSeconds` |
| Bridge ⑦ `HttpClient.Timeout` | **removed** (was 600 s) | — | — |
| Bridge ⑧ Kestrel keep-alive | 900 s | hard-coded | — |

### The 180 s trap: a side effect of the bridge's own 1M-context flag

Watchdog ① is armed only when the request is considered *first-party*:

```js
function QQc(e){ return e==="firstParty" && Kd() || ... }
function Kd(){ if (Z._CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL) return !0; return fWr() }
function h7i(e){ let t=m7i(), r = e==="firstParty" ? vxg : t, ... }   // vxg = 180000
```

`config claude-code` writes `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` to unlock
the native 1M context window (see [`context-window.md`](context-window.md)). Its
side effect is making `Kd()` true, which swaps the idle budget from 300 s down to
180 s. **Enabling 1M context tightens the timeout by 120 s** — which is why a
missing client key is treated as known-bad rather than unknown: the bridge itself
created the condition that selects the shorter bound.

### Only one knob lifts both client watchdogs

```js
function m7i(){ return Math.max(fq(process.env.CLAUDE_STREAM_IDLE_TIMEOUT_MS)||0, 300000) }
function h7i(e){
  let t=m7i(), r = e==="firstParty" ? vxg : t, n=t,
      o=fq(process.env.CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS),
      i=fq(process.env.CLAUDE_STREAM_IDLE_TIMEOUT_MS)>0;
  if (Number.isFinite(o)&&o>0) n=o;
  else if (!i) { n=r; ... }              // <-- skipped once CLAUDE_STREAM_IDLE_TIMEOUT_MS is set
  return Math.min(Math.max(n,1), 1800000)
}
```

`CLAUDE_STREAM_IDLE_TIMEOUT_MS` raises **both**: it lifts ② directly, and because
the `else if(!i)` branch is skipped, ① inherits the raised value instead of the
180 s first-party default. `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS` lifts only ①,
leaving ② pinned at its 300 s floor. The 30-minute ceiling (`Exg`) is why the
bridge clamps what it writes — a larger value is silently reduced by the client.

## Evidence

### Lab: real `claude.exe` against an upstream that goes silent

| silence | env | outcome |
|---|---|---|
| 240 s | (none) | **abort @ 180.013 s**, `exit 1` |
| 240 s | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS=900000` | survived, `exit 0` |
| 240 s | `CLAUDE_STREAM_IDLE_TIMEOUT_MS=900000` | survived, `exit 0` |
| 340 s | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS=900000` | **abort @ 300.006 s**, `exit 1` |
| 340 s | `CLAUDE_STREAM_IDLE_TIMEOUT_MS=900000` | survived, `exit 0` |

The aborts land on `vxg` and the `m7i()` floor to the millisecond. User-visible
error either way: `API Error: Stream idle timeout - no chunks received`.

### Production: the cascade this change fixes

Captured while reproducing the failure with a three-prompt itinerary sequence
(bridge log `bridge-20260723-021002.log`, traces `20260727-0226*`):

```
10:26:51  seq 0022  stream=true   142 404 bytes forwarded
10:26:53  message_start + content_block_start (a THINKING block opens)   ← 1.75 s in
          ... silence: the model is thinking, Copilot sends no ping ...
10:29:54  seq 0022 ends: duration_ms=182816  error=cancelled by client   ← CC watchdog ① @180 s
10:29:54  seq 0026  stream=FALSE  142 390 bytes    ← CC's non-streaming fallback
10:33:54  seq 0026: upstream_timeout=first_byte  status=504             ← bridge ⑤ @240 s
10:33:54  seq 0034  stream=false  (CC retries)
10:37:54  seq 0034: upstream_timeout=first_byte  status=504             ← bridge ⑤ again
10:37:56  seq 0039  stream=false
10:41:53  seq 0039: status=200  duration_ms=236693  out=16795 tokens    ← finally succeeded
```

Two failures compounded: the client killed a healthy stream at 180 s, and its
non-streaming recovery then hit the bridge's 240 s first-byte budget — a
non-streaming request produces *no bytes at all* until the model has finished, so
a 4-minute answer was guaranteed to blow it. The successful attempt took 236.7 s,
clearing the budget by 3 s.

## Diagnosing a recurrence

- Read the **startup report** first — it names the effective bound and its source.
- Bridge summary line: `upstream_timeout=first_byte|stream_idle` names the phase
  that fired; `error=cancelled by client` means the *client* gave up first.
- A `streaming=false` request immediately after a `cancelled by client` streaming
  request is the signature of Claude Code's non-streaming fallback — the turn
  already failed once.
- The client-facing trace (`*-inbound-resp.json`) event count tells you where it
  stopped: 2 events (`message_start`, `content_block_start`) = killed during
  thinking.
- `config status` reports the client as drifted when a budget was raised without
  re-running `config claude-code`.

## Method

Client-side constants come from the **running** `claude.exe`, not a decompile:
the 2.1.88 source map in `claude-code-sourcemap` predates all three watchdogs and
disagrees. Extract the bundled JS from the binary and search for
`CLAUDE_STREAM_IDLE_TIMEOUT_MS` / `cli_streaming_idle_warning`. Behavior is then
confirmed by driving the real client against a local HTTP server that opens an
SSE stream, stays silent for N seconds, and completes — varying only the env under
test.
