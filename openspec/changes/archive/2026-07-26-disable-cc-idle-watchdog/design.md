## Context

A `claude-opus-5` turn at `xhigh` effort failed deterministically through the
bridge. The captured trace (`20260726-134917-0534`) shows the upstream emitting
`message_start` and an empty `thinking` `content_block_start`, then nothing. The
bridge reported `stream_idle` at 120s.

Investigation separated three distinct facts that had been conflated into the
single symptom "the bridge times out".

### What was measured

**The upstream cap.** Replaying the captured request against a bridge instance with
a 900s idle budget produced not a resumed stream but `premature_eof` at 304.4s. To
rule out the bridge's own stack, the same request was then replayed
**direct to `api.enterprise.githubcopilot.com`** from raw Python `HTTPSConnection`
with a 900s socket timeout — no bridge, no .NET `HttpClient`, no ASP.NET. Result:
305.2s, clean EOF, zero tokens. An unrelated synthetic prompt (an exhaustive graph
enumeration chosen only for being expensive to think about) produced 303.0s on the
same direct path.

| Replay | Stack | Result |
|---|---|---|
| Captured itinerary request | Direct, no bridge | 305.2s, server closed |
| Unrelated synthetic prompt | Direct, no bridge | 303.0s, server closed |
| Captured request, `high` | Bridge, 900s budget | 305.8s, server closed |
| Captured request, `xhigh` | Bridge, 900s budget | 304.4s `premature_eof` |

Four measurements, 303–305s, across two unrelated prompts and two independent HTTP
stacks. The termination is a clean EOF — no `error` event, no `message_stop` — which
is a server closing a connection, not a local timer firing (a client-side abort
surfaces as a cancellation/timeout exception, which the probe distinguished
explicitly).

`effort=medium` on the same request **completed** in 276.4s, with a 207s silent gap
during thinking. So the cap is real but the workload sits right at its edge: medium
finishes just inside it, high and xhigh do not.

**No keepalive.** Zero occurrences of `event: ping` across 290 captured upstream
bodies. Copilot does not send SSE keepalives, so an extended-thinking gap is
genuinely byte-free on the wire.

**The cap was previously invisible.** Across 9,608 captured responses, none ran past
250s. Nothing in the corpus had ever touched the 300s ceiling, which is why it was
not already known.

### What was found in the repo

Two defects, discovered while explaining the above:

1. **`API_FORCE_IDLE_TIMEOUT` is left armed.** Claude Code aborts a byte-free
   streaming response after 5 minutes; per its documentation the timeout is inactive
   on direct Anthropic/AWS connections and active on every other provider — so it is
   on for every bridge user by default. Combined with the absence of pings, it will
   abort a legitimately long think. The bridge already force-writes three other
   Claude Code env keys, so the mechanism to fix this exists and is unused here.

2. **The stream-idle default's stated rationale is false.** `StreamIdleTimeoutSeconds
   = 60` is justified in three places as "below Claude Code's own watchdog
   `CLAUDE_STREAM_IDLE_TIMEOUT_MS`, default 90s". That variable does not appear in
   current Claude Code documentation. The real mechanism is `API_FORCE_IDLE_TIMEOUT`
   at 5 minutes — different name, different value, different trigger condition.

## Goals / Non-Goals

**Goals**
- Stop the client from aborting turns the bridge intends to keep alive.
- Make `config status` surface the still-armed watchdog as drift, so existing users
  learn they need to re-run the config command.
- Replace the false rationale with the real mechanism, in all three places.
- Record the upstream cap and its reproduction so the next person to hit a stalled
  `message_start` does not repeat this investigation.

**Non-Goals**
- **Changing `StreamIdleTimeoutSeconds`.** Its justification was wrong, but that does
  not tell us the value is wrong. See D3.
- **Working around the 300s cap.** It is server-side and unconditional. No bridge or
  client setting extends it; claiming otherwise would be a false promise.
- **Special-casing the cap in bridge behavior** (e.g. a distinct error message when a
  stream dies at ~300s with no token). Plausible, but it is a separate change with its
  own detection question, and this one is already touching two specs.

## Decisions

### D1 — Force-write `API_FORCE_IDLE_TIMEOUT = "0"`, exactly like the 1M-context pair

Same mechanism as `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`: a managed constant, an
unconditional write in `MergeInto`, a read plus detail line in `Read`, and a
`ConfigState` Expected/Current pair that feeds `Drifted`.

Force-write rather than fill-if-absent. A user who has explicitly set `"1"` has
turned the watchdog on for all providers, which on this backend means arming a timer
that cannot distinguish thinking from death. Preserving that value would preserve the
bug. This matches how the 1M pair already behaves, and the overwrite is disclosed in
the `--dry-run` summary line.

- **Alternative considered — fill only if absent:** respects an explicit user choice,
  but silently leaves the defect in place for exactly the users who touched the knob.
  Rejected; the value is bridge-operational, not a user preference.

### D2 — Existing users are reported as DRIFTED, deliberately

Adding a managed key means every previously-configured user's `config status` flips
to DRIFTED until they re-run `config claude-code`. That is a real, if mild, papercut.

It is also correct: DRIFTED is precisely true — their config no longer matches what
the bridge would write, and the difference is a live defect. Suppressing the drift
signal for this key would hide it from the only population that still has the problem.

- **Alternative considered — treat a missing key as acceptable:** avoids the churn but
  makes `config status` say "fine" to a config that will abort long turns. Rejected.

### D3 — Correct the rationale, do not touch the value

`StreamIdleTimeoutSeconds` stays at 60. The discovered error is in the *stated reason*,
not necessarily the number.

It is tempting to raise it now — 60s is well under the ~207s of silence a successful
`effort=medium` request needed, so the bridge would abort a turn Copilot was still
working on. That is a genuine finding and probably means the default is too low. But
choosing a new value needs its own evidence: how often real turns exceed 60s of
silence, what the recovery action costs when it fires spuriously, and whether the
right ceiling is now the 300s upstream cap. Folding a behavior change into a
documentation fix would ship an unmeasured number under cover of a correction.

Recorded here as the open question so it is not lost. The corrected comments state
the real mechanism and stop claiming a coordination that never existed.

### D4 — Document the cap as an upstream fact, in its own file

`docs/copilot-stream-cap.md`, not a paragraph inside `pipeline-design.md`. It is a
property of the backend, not of the pipeline, and it is the kind of fact someone
arrives at from a symptom ("my turn died after five minutes with no output") rather
than from reading the pipeline design top to bottom. It includes the reproduction so
the measurement can be re-run when Copilot changes.

## Risks / Trade-offs

**The cap may not be exactly 300s, or may vary.** Four samples clustered at 303–305s
support "roughly 300 seconds", not a precise constant, and the doc says so. It may
also differ by account type (measured on an enterprise endpoint) or change without
notice. Mitigated by documenting the reproduction rather than only the number.

**Disabling the client watchdog removes a safety net for non-Copilot failure modes.**
If the bridge's own idle budget were misconfigured to 0 (disabled), a truly dead
stream would now hang until the 10-minute `HttpClient.Timeout`. Accepted: the bridge's
budget is on by default, and one clearly-owned bound beats two uncoordinated ones.

**`ConfigState` grows to a fourth Expected/Current pair.** The record is becoming a
list of parallel key pairs and will eventually want a dictionary. Not refactored here
— it would enlarge the diff of a fix that should stay reviewable, and the pattern is
still legible at four.

## Migration Plan

No data migration. Users re-run `copilot-bridge config claude-code`; `config status`
tells them to by reporting drift. No bridge restart is required for the client-side
change (Claude Code reads its settings at launch, so the user restarts Claude Code).

## Open Questions

- **Is `StreamIdleTimeoutSeconds = 60` too low?** A successful `effort=medium` request
  was silent for 207s mid-thinking, far beyond 60s — meaning the bridge would have
  aborted a working turn had that silence landed between two of its events rather than
  before the first. Deferred per D3; wants a measurement of silence-gap distribution
  across real turns before a new default is chosen.
- **Does the ~300s cap vary by account type or model?** Measured only on an enterprise
  endpoint with `claude-opus-5`.
