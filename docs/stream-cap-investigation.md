# Does Copilot cap a token-less stream? (no)

A long deep-thinking turn that dies looks like an upstream deadline, and that reading
has been proposed twice in this repo with different numbers — once as a hard ~300 s
close of any token-less stream, once as a measured 600 s of upstream silence. Both
would change how the bridge's budgets should be set, so both are checked here against
the production log corpus and a live probe.

**Neither holds. Copilot imposes no fixed cap on a token-less stream**, and the
figures that suggested one turn out to be the bridge's own timers and ordinary
transport failure. Practical consequence: when a long turn dies, suspect a local
budget (`Pipeline:UpstreamTimeout`, a client watchdog) before suspecting upstream —
see [`timeout-chain.md`](timeout-chain.md).

## Corpus

`<install>\log` — 51 bridge logs, 2026-06-05 → 2026-08-01, **10 352** request-summary
lines (a live bridge's current log is skipped while it holds the handle). Reproduce
with `scripts/scan-stream-durations.ps1`.

### Keepalive injection does not confound this

The bridge began injecting synthetic `ping` events on the `/cc` path in
`0.4.24-beta` (deployed 2026-07-28 21:13). That splits the corpus, and the split
matters, so it is stated rather than left for the reader to discover:

| | summaries | longest clean stream | clean >300 s | `premature_eof` |
|---|---|---|---|---|
| **pre-keepalive** | 10 269 | **384.8 s** | 2 | 7 — 8.5 / 34.7 / 113.6 / 120.5 / 142.7 / 608.9 / 627.5 s |
| **post-keepalive** | 83 | 179.6 s | 0 | 2 — 10.2 / 21.6 s |

**Every figure this document rests on comes from the pre-keepalive period** — the
384.8 s completion, both runs past 300 s, all 16 runs past 250 s, and 7 of the 9
disconnects. Injection cannot have manufactured them, because it did not exist yet.
The post-keepalive sample is only 83 requests and is too small to conclude anything
from on its own; its two disconnects (10.2 s, 21.6 s) are merely consistent.

Two further reasons injection cannot explain the result: it is a **downstream** event
the bridge sends to its client, never something it receives from Copilot, so it
cannot extend an upstream stream; and by design it never resets the bridge's own
stream-idle budget (see `timeout-chain.md`). The live probe below bypasses the bridge
process entirely and so is unaffected either way.

## What the corpus shows

### 1. Streams run past 300 s and complete normally

Of 9 428 clean streaming runs (`error=(none) streaming=true`):

| p50 | p95 | p99 | max |
|---|---|---|---|
| 11.2 s | 61.3 s | 141.0 s | **384.8 s** |

Two exceeded 300 s. The longest:

```
duration_ms=384809  effort=max  status=200  streaming=true
usage={in:2 out:2307 cache_read:0 cache_creation:122776}  error=(none)
```

A 384.8 s stream that finished and returned 2 307 tokens **cannot coexist with a
300 s server-side cap.** This single line falsifies the claim.

### 2. Actual upstream disconnects do not cluster at 300 s

`premature_eof` — the error a server-side close produces — occurs **9 times in
10 352 requests (0.09 %)**, at these durations:

```
8.5s  10.2s  21.6s  34.7s  113.6s  120.5s  142.7s  608.9s  627.5s
```

**Not one lands near 300 s.** A fixed cap would pile them up there; this is scatter,
consistent with ordinary transport failure.

### 3. The long tail was mostly the bridge's own timers, not Copilot

| cause | count |
|---|---|
| clean | 10 047 |
| cancelled by client | 150 |
| bridge `first_byte` timeout | 53 |
| bridge `stream_idle` timeout | 52 |
| transport error (DNS / TLS / connect) | 29 |
| other | 12 |
| `premature_eof` (upstream closed) | 9 |

The bridge's own budgets ended **11× more** requests than upstream did, and ordinary
transport failures — DNS, TLS, refused connections — outnumber upstream closes **3:1**.
That ratio is itself an argument: if `premature_eof` were a deliberate server policy
it should be common and regular, and instead it is rarer than the network simply
breaking. Every `stream_idle` line names a bridge budget (`idle=120s`, `idle=180s`),
not a server decision.

### 4. The "nothing has ever come close" premise is wrong

The cap claim was supported by the observation that none of 9 608 responses ran past
250 s — presenting ~300 s as untouched territory that nothing had ever reached. On
this corpus **16 runs exceed 250 s**, 8 of them clean completions. The premise does
not hold, and with it goes the reason the ceiling had supposedly stayed invisible.

## Restating the 600 s figure

[`timeout-chain.md`](timeout-chain.md) said a `claude-opus-5` turn at `effort=xhigh`
"put nothing on the wire for 600 s". The corpus does contain 600 s runs, but they are
**not** upstream silence:

```
600005 ms   600005 ms   600014 ms
```

Millisecond-tight against a round 600 s is the signature of a **local fixed timer** —
the bridge's own `HttpClient.Timeout`, which was 600 s at the time and has since been
removed (bound ⑦ in `timeout-chain.md`). All three are `status=500 streaming=false
error=cancelled by client`.

The neighbouring 608.9 s and 627.5 s `premature_eof` runs are also not a cap: all
five cluster inside a **34-second window** (11:40:49 → 11:41:23) despite different
start times and durations — concurrent requests dying together, i.e. one transport
interruption. A per-request server cap would end each one ~300 s after *its own*
start, scattering the wall-clock times.

Both also report `out:1` — a token was produced, so they are not the token-less shape
the cap claim describes.

The load-bearing point is untouched: Copilot sends no keepalive, so extended thinking
is genuinely byte-free. Only the specific figure was wrong, so `timeout-chain.md` now
cites the longest *verified* clean run (384.8 s) instead.

## What survives

- **Copilot sends no SSE `ping`.** Independently re-confirmed by live probe: a
  `claude-opus-5` / `effort=xhigh` turn produced 34 events, **0 pings**. This is the
  fact the bridge's keepalive injection rests on, and it is unaffected.
- **Extended thinking produces long byte-free gaps.** The same probe showed a 17.6 s
  gap on a *trivial* turn; production shows far longer. The phenomenon is real — only
  the specific 600 s and 300 s numbers were unsupported.

## Live probe

`tests/CopilotBridge.Playground/ApiContract/StreamCapProbe.cs` measures this
directly and is written to be admissible. It talks to
`<endpoints.api>/v1/messages` through `AuthService` + `CopilotHeaderFactory`,
**bypassing the bridge process entirely** — so no bridge budget, and no injected
keepalive, can shape what it observes. It classifies the end of a stream by
**cause**, not elapsed time — a zero-length read means the peer closed, an
`OperationCanceledException` means the probe gave up — and its own budget is 900 s,
three times the disputed value, so a local timer cannot be mistaken for a server cap.
A run that ends on the probe's own deadline is reported INCONCLUSIVE rather than as a
measurement.

It deliberately asserts nothing about the bound's value; pinning a number would
freeze whichever claim happens to be current. The only assertion is that the server,
not the probe, decided the ending.

### Run 1 — trivial prompt, `claude-opus-5` / `xhigh`

Completed in 32.9 s: 34 events, **0 pings**, first text token at 21.4 s, longest
byte-free gap 17.6 s. Too easy to reach any ceiling, but it independently confirms
the no-keepalive fact.

### Run 2 — hard prompt, `claude-opus-5` / `xhigh`

Polled repeatedly while in flight and found the stream **still open at 383 s, 518 s,
595 s and 663 s** — more than double the proposed ~305 s ceiling, and past the 600 s
figure as well. The run was then killed to unblock a rebuild, and because xUnit
buffers `ITestOutputHelper` in-process, **the event timeline and final ending were
lost**. That is a real gap in this record: the probe shows the stream *survives* far
past both proposed bounds, but does not characterise how it eventually ends.

It does not weaken the conclusion. A stream cannot be hard-closed at ~305 s and still
be transmitting at 663 s. Re-run the probe to completion if the ending itself matters.
