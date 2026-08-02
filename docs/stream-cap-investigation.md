# Does Copilot close a long stream at a fixed ~300 s? (no)

A long deep-thinking turn that dies looks like an upstream deadline, and that reading
has been proposed twice in this repo with different numbers — once as a hard ~300 s
close of any token-less stream, once as a measured 600 s of upstream silence. Both
would change how the bridge's budgets should be set, so both are checked here against
the production log corpus and a live probe.

**Neither holds**, and the figures that suggested them turn out to be the bridge's own
timers and ordinary transport failure. Practical consequence: when a long turn dies,
suspect a local budget (`Pipeline:UpstreamTimeout`, a client watchdog) before
suspecting upstream — see [`timeout-chain.md`](timeout-chain.md).

**What this does and does not establish.** A live probe held a **genuinely token-less
stream open for 752.6 s** — no content delta of any kind, verified from the wire, at
2.5× the proposed ceiling — which settles the token-less form of the claim directly.
The corpus agrees independently: disconnects scatter from 8.5 s to 627.5 s with none
in 280–330 s. It does **not** establish any upper bound in place of the one it
removes: one observation at 752.6 s fixes a floor, not a ceiling. Nor does it say who
ends these streams — a response body that stops early looks identical whether Copilot,
a proxy, or the network dropped it, so this document deliberately attributes none of
them.

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

**How far this goes, and no further.** It rules out a cap on *total stream duration*:
a 384.8 s stream finished and returned 2 307 tokens, so no deadline ends a stream at
300 s unconditionally. It does **not** by itself rule out a cap that applies only
while a stream remains token-less — summary lines record no first-token time, so this
run may have started emitting well before 300 s and simply continued. The
token-less form of the claim is answered by §2 and §5, not here.

### 2. Actual upstream disconnects do not cluster at 300 s

`premature_eof` — the error a server-side close produces — occurs **9 times in
10 352 requests (0.09 %)**, at these durations:

```
8.5s  10.2s  21.6s  34.7s  113.6s  120.5s  142.7s  608.9s  627.5s
```

This is the observation that discriminates, and it needs no first-token timing:
*whatever* triggers a fixed cap, the disconnects it produces have to
cluster at the cap value. **None lands within 280–330 s.** Scatter across two orders
of magnitude is not a deadline.

The two longest are the strongest single data points against the *token-less* form
of the claim, because they are almost token-less themselves:

```
608.9s  out:1  CopilotAnthropic:/v1/messages  premature_eof
627.5s  out:1  CopilotAnthropic:/v1/messages  premature_eof
```

Both are on the Anthropic endpoint, both produced **one** output token across ten
minutes, and both ran **more than twice** the proposed ~305 s ceiling. Strictly, one
token means they are not literally token-less, so a narrow reading of the cap could
exempt them — but a rule that closes non-producing streams at ~305 s has to call a
stream with `out:1` at 627 s "producing" in order to survive. (Their ending is
separately explained in §"Restating the 600 s figure" — they died together in one
34-second window, i.e. a transport interruption.)

### 3. The long tail was mostly the bridge's own timers, not Copilot

| cause | count |
|---|---|
| clean | 10 047 |
| cancelled by client | 150 |
| bridge `first_byte` timeout | 53 |
| bridge `stream_idle` timeout | 52 |
| transport error (DNS / TLS / connect) | 29 |
| other | 12 |
| `premature_eof` (connection cut mid-body) | 9 |

The bridge's own budgets ended **11× more** requests than upstream did, and ordinary
transport failures — DNS, TLS, refused connections — outnumber mid-body cuts **3:1**.
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

Both report `out:1` — a single output token across ten minutes. That is not literally
token-less, and it is the reason §2 cites them as *near*-token-less rather than as a
clean instance of the shape: a strict reading of the cap could exempt them. But one
token in 627 s is not a stream that is producing, and a rule that closes silent
streams at ~305 s would have to treat it as one to survive.

**The 600 s claim's direction was right; only its evidence was wrong.** Silence really
can run that long — the live probe measured **749 s** of it. What the corpus could not
support was reading that figure off `duration_ms` values produced by a local timer.
`timeout-chain.md` now cites the measured gap instead, so the same conclusion rests on
an observation of upstream rather than of ourselves.

The load-bearing point is untouched: Copilot sends no keepalive, so extended thinking
is genuinely byte-free. Only the specific figure was wrong, so `timeout-chain.md` no
longer states a verified silence duration at all.

## What survives

- **Copilot sends no SSE `ping`.** Independently re-confirmed by live probe: zero
  pings across a 32.9 s turn (34 events) and zero across 752.6 s of silence. This is
  the fact the bridge's keepalive injection rests on, and it is unaffected.
- **Extended thinking produces long byte-free gaps.** Now quantified: **749 s** with
  nothing on the wire, measured. The phenomenon was always real — only the 600 s and
  300 s figures were derived from the wrong observations.

## Live probe

`tests/CopilotBridge.Playground/ApiContract/StreamCapProbe.cs` measures this
directly and is written to be admissible. It talks to
`<endpoints.api>/v1/messages` through `AuthService` + `CopilotHeaderFactory`,
**bypassing the bridge process entirely** — so no bridge budget, and no injected
keepalive, can shape what it observes.

Two independent conditions must hold before a run counts as evidence, and each fails
loudly rather than passing as a green measurement:

1. **The ending must not be the probe's own deadline.** The probe measures how long a
   stream stayed alive while producing nothing — a *lower bound*, established by the
   bytes that kept arriving, not by whatever ended the stream afterwards. Only its own
   900 s budget firing destroys that reading, because the stream might have been closed
   a millisecond later and we would never know; the number would measure our patience.
   A transport fault is different — the stream demonstrably survived until it happened.
   **Attribution is deliberately withheld:** a response body ending early is
   indistinguishable here from a reset or a proxy dropping the connection (the repo's
   own `TransientUpstreamError` classes `HttpIOException` as "upstream *or* network"
   for exactly that reason), so no ending is reported as "Copilot closed it".
2. **The stream was actually token-less across the threshold.** A prompt instruction
   cannot *guarantee* wire silence — the model may narrate immediately — so this is
   verified from the wire: the probe records when the first content delta of any kind
   arrives (`text_delta`, `thinking_delta`, `signature_delta`, `input_json_delta`, not
   just visible text) and marks a run INAPPLICABLE if content started before the
   threshold. Without this check the probe silently degrades into a total-lifetime
   measurement, which cannot test a token-less cap at all — the same conflation this
   document had to correct elsewhere.

It deliberately asserts nothing about the bound's *value*; pinning a number would
freeze whichever claim happens to be current.

### Run 1 — trivial prompt, `claude-opus-5` / `xhigh`

Completed in 32.9 s: 34 events, **0 pings**, first content at 21.4 s, longest
byte-free gap 17.6 s. **Inapplicable to the cap** — it neither reached the threshold
nor stayed token-less — but it is the source of the directly-measured 17.6 s silence
and the independent confirmation that Copilot sends no `ping`.

### Run 2 — hard prompt, `claude-opus-5` / `xhigh` (killed, no timeline)

Polled while in flight and found the stream still open at 383 / 518 / 595 / 663 s,
then killed to unblock a rebuild. xUnit buffers `ITestOutputHelper` in-process, so the
event timeline was lost — and without it there is no record of when content first
arrived, so this run **cannot** be claimed as a token-less stream. Superseded by Run 3,
which instruments exactly that.

### Run 3 — hard prompt, `claude-opus-5` / `xhigh` — the decisive measurement

```
elapsed        : 752.6 s
first content  : never          ← genuinely token-less, verified from the wire
events         : 2 (pings: 0)   ← message_start @2.0s, content_block_start @3.5s
tests 305s cap : YES            ← applicable
ending         : TransportError (response body ended prematurely — actor unknown)
```

**A stream that produced no content at all stayed open for 752.6 s — 2.5× the
proposed ~305 s ceiling.** The wire shape is exactly the one the cap claim describes:
`message_start`, a `content_block_start` opening a thinking block, then nothing for
749 seconds. If Copilot closed token-less streams at ~305 s, this run could not exist.

Two details worth keeping:

- **The ending is not attributed.** The response body ended before its terminating
  chunk, which is what a peer close looks like — and also what a reset or a failed
  intermediary looks like. The probe reports it as a transport fault with the actor
  marked unknown. That costs nothing here: the claim under test is that the stream is
  *closed at ~305 s*, and this stream was demonstrably still alive at 305 s. Survival
  is established by the 749 s of connection that preceded the ending, whoever caused it.
- **Zero pings across 752 s of silence**, confirming again that the keepalive the
  bridge injects has no upstream counterpart.

The first attempt at this run failed its assertion, which is how the classification
above got settled. .NET surfaces a mid-body cut on a chunked response as
`HttpIOException(ResponseEnded)` rather than a zero-length read, and the probe's
original rule ("only a peer-decided ending counts") rejected it. The rule was wrong,
but not in the direction it first appeared: `ResponseEnded` does **not** identify the
peer as the actor, so the fix was not to promote it to a server close — it was to stop
requiring attribution at all. Survival to a threshold is proved by the connection that
preceded it; only the probe's own timeout can undermine that.
