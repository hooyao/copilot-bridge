# Diagnosis: Claude Code goes haywire when routed to gpt-5.5

Investigation of the 2026-07-05 session where Claude Code, routed through
copilot-bridge `/cc` → Copilot `/responses` (gpt-5.5), produced nonsense actions
and one 6.5-minute / 17 MB runaway response.

Evidence: `log/bridge-20260705-091206.log`, traces under `request-traces/`,
primarily seq `0102` (`20260705-093651-0102-*`). The session id in the traces
(`97547e82-...`) is the same interactive session, so the "weird operations" the
user saw *were* gpt-5.5 driving the Claude Code harness.

## TL;DR

The bridge translated faithfully. The erratic behavior is **gpt-5.5 itself
malfunctioning as a Claude Code driver**, made much worse by a **context poisoned
with 50 error tool-results** from earlier failed calls in the same session. Three
bridge-side issues amplified or obscured it, none of which is the root cause but
all worth fixing:

1. No output circuit-breaker → the bridge relayed a degenerate 27,643-delta /
   17 MB tool call for 6.5 min until the user killed it.
2. `output_config.effort=max` is silently down-clamped to `medium` on gpt-5.5
   (should be `xhigh`) — a real translation bug.
3. The summary log prints the **inbound** effort (`max`), not what was actually
   sent upstream (`medium`) — misleading, explains "effort 有时 max 有时 null".

## The runaway (seq 0102) — what actually happened

- Claude Code inbound-req: `model=claude-opus-4-8`, `output_config={"effort":"max"}`,
  `thinking=adaptive`, `max_tokens=64000`, `99 tools`, `stream=true`. Routed to
  `gpt-5.5` via the `gpt-5.5-1m → gpt-5.5` location.
- Upstream-req to Copilot: `model=gpt-5.5`, `reasoning={"effort":"medium"}` (note:
  **medium**, not max — see bug #2), `max_output_tokens=64000`, 99 tools, 278
  input items.
- Upstream-resp (17.13 MB, `text/event-stream`): a **single `Write` function
  call** that never finished. Event counts:
  - `response.function_call_arguments.delta`: **27,643**
  - `response.output_item.added`: 2 (one reasoning item, one `Write` call)
  - `response.created` / `response.in_progress` / `response.output_item.done`: 1 each
  - **no** `response.completed`, **no** `function_call_arguments.done`
- Reconstructed `Write` arguments = only **99 KB** of text, but degenerate:
  after a plausible start (a `recursive-spinning-catmull.md` plan), the `content`
  collapses into an endless loop of whitespace and literal
  `"               \t\t:" + "\t\t," +` repeats. gpt-5.5 got stuck in a
  degenerate-generation loop.
- Why 17 MB for 99 KB of text: **19,907 of 27,643 deltas carried ≤2 payload
  chars** (avg 3.59), each wrapped in a large encrypted Responses envelope
  (`id`/`call_id` blobs). The bloat is Copilot's per-delta framing, faithfully
  relayed.
- inbound-resp to Claude Code: correctly translated to Anthropic SSE
  (`message_start` → `content_block_start` tool_use `Write` → 27,643
  `input_json_delta` → `content_block_stop`), `duration_ms=393817` (6.5 min),
  `error="cancelled by client"`. Translation was correct; there was just nothing
  good to translate.

## Root cause #1 — poisoned input context (the real trigger)

The history sent to gpt-5.5 in seq 0102 contained **137 tool_result blocks, 50 of
which were `API Error: 400 The requested model is not supported`**, plus several
giant truncated tool dumps (`Output too large (42.1KB)...`).

Those 50 errors came from *this same session*: earlier parallel `Agent` / Task
launches with model overrides that Copilot rejected (the `debug`/model-override
calls that returned `400 The requested model is not supported`). Claude Code kept
that failure debris in the transcript, and it was replayed to gpt-5.5 as context.

A frontier model usually shrugs this off; gpt-5.5, already a weaker fit for the
Claude Code tool protocol, was pushed into incoherent output (spurious tool
calls, then the degenerate `Write` loop). **Garbage-in → garbage-out.** This is
model behavior, not a translation defect.

**Bridge mitigation (gap #5).** The bridge cannot fix this — it can neither strip
the blocks (dropping a `tool_result` without its paired `tool_use` 400s upstream)
nor un-poison the client's transcript; only the user compacting the session can.
So the bridge does the one useful thing it can: a request-side
`PoisonedContextScanStage` detects the **structural** signature — *one tool name
accumulating many failed `tool_result`s in a single request* (here `Agent` alone
failed 50×). It is deliberately **not** a match on the error phrase: a
`tool_result` counts as failed when `is_error` is set OR its content begins with an
API-failure marker (`API Error:` / `Error:`), so the detector survives whatever
wording Copilot returns next (a region 400, a quota 429, a timeout, "model is not
supported", …) rather than chasing phrases. It records the total failure count on
the summary line (`poisoned_tool_results=`) and, once one tool crosses a threshold
(default 5), logs a WARNING naming that tool and telling the user to `/compact`.
Because Claude Code replays the whole transcript each turn, that warning repeats
every turn until the user compacts — the intended nudge. The bad *output* such a
context induces is caught independently by the runaway guard (gap #4).

## Root cause #2 — gpt-5.5 is a poor Claude Code harness driver

Even on clean turns (seq 0101, 0004–0011) the gpt-5.5 responses are tiny
(`out:47`, `out:141`, `out:314`) and the model repeatedly mis-drives the harness.
Claude Code's system prompt, tool schemas, and thinking/effort contract are tuned
for Claude; gpt-5.5 through the Responses shape is a lossy fit. This is inherent,
not fixable in the bridge.

## Bridge bug #2 — effort `max` silently clamped to `medium`

`src/CopilotBridge.Cli/Pipeline/Strategies/Codex/ResponsesRequestBuilder.cs`
`CoerceEffort()` (≈L571-590) has **no `"max"` case**:

```csharp
return effort.ToLowerInvariant() switch
{
    "minimal" => ... ? "low" : null,
    "xhigh"   => ... ? "high" : null,
    "none"    => null,
    _         => profile.AcceptedEfforts.Contains("medium", ...) ? "medium" : null,
};
```

The gpt-5.5 Codex profile is
`AcceptedEfforts = ["none","low","medium","high","xhigh"]`
(`CodexModelProfileCatalog.cs` ≈L112). `max` is not in it, so it hits the `_ =>`
default and becomes **`medium`** — a two-tier silent downgrade. The routing note
for gpt-5.5 even says *"gpt-5.5 natively accepts xhigh"*, so the intended clamp is
`max → xhigh` (parallel to the existing `xhigh → high` neighbor rule). Fix: add a
`"max" => AcceptedEfforts.Contains("xhigh") ? "xhigh" : (Contains("high") ? "high" : ...)`
arm. Verify against a probe before shipping.

## Bridge issue #3 — misleading effort in the summary log ("max vs null")

The per-request summary logs the **inbound** effort, not the coerced outbound
value. So seq 0102 logs `effort=max` while the wire actually carried
`reasoning.effort=medium`. That is the "非常不正常" the user noticed.

The `max` vs `(none)` flip-flop across lines is *mostly* normal, though:
- Foreground CC main-loop requests carry `effort=max` (79× gpt-5.5, 4× opus, 3× sonnet).
- Background utility requests (titles, classifiers) carry **no** effort — that's
  Claude Code, not the bridge (57× haiku `(none)`, 6× opus `(none)`).

So the null lines are expected; the genuinely wrong part is **logging inbound-max
while sending medium**. Recommend logging the *outbound* (coerced) effort, or both
(`effort_in=max effort_out=medium`).

## Bridge gap #4 — no runaway circuit-breaker

The bridge streamed 27,643 deltas / 17 MB over 6.5 min with no guard. A safety
valve on the Responses→Anthropic stream (cap on delta count, cumulative bytes, or
a stall/duration budget) would abort a degenerate loop and surface a clean error
instead of hanging the client. Related but distinct from the `ResponseLeakGuard`
(that scans for leaked control markup, not size/loops).

## Secondary noise (not the cause)

Several background requests 502'd / 400'd transiently
(`net_http_client_execution_error`, and a few `claude-sonnet-4.6` /
`claude-haiku-4.5` `400`s at seq 0007-0009, 0012-0014). These are Copilot-side
transient failures on Claude Code's background/utility calls, unrelated to the
gpt-5.5 runaway.

## Recommendations

1. **Don't route the Claude Code main loop to gpt-5.5** for real work — it's a
   lossy harness fit and prone to this failure. Keep gpt-5.5 for Codex, route CC
   to a Claude model.
2. Fix `CoerceEffort` `max → xhigh` for the large profile (bug #2).
3. Log the outbound/coerced effort, or both in/out (issue #3).
4. Add a stream circuit-breaker (delta-count / byte / stall budget) on the
   Responses path (gap #4).
5. Independently, the poisoned-context trigger (#1) is a Claude-Code-side artifact
   of failed `Agent` model-override calls leaving `400` debris in the transcript.
   The bridge can't clean it, but it now **detects and warns**: a request-side
   `PoisonedContextScanStage` detects the structural signature (one tool name with
   many failed `tool_result`s — is_error or an `API Error:`/`Error:` prefix, not a
   fixed phrase), records the total on the summary line (`poisoned_tool_results=`)
   and, past a per-tool threshold, logs a WARNING naming the tool and telling the
   user to `/compact` — the only real cure.
