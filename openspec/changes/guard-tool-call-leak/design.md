## Context

GitHub-Copilot-served Claude models (observed: `claude-opus-4-8`) intermittently
emit a tool call as **literal XML inside a text or thinking content block**
instead of a proper `tool_use` block. Claude Code renders it as text and executes
nothing; worse, the leaked text commits to the transcript and Claude Code replays
the whole transcript each turn, so the model imitates its own bad output and the
failure self-reinforces (a poisoned session).

The bridge is byte-level passthrough for `/cc/v1/messages` today
(`CopilotMessagesPassthroughStrategy` → lazy per-event SSE relay in
`ClaudeCodeMessagesEndpoint`). It is the only vantage point that sees every SSE
event AND can influence Claude Code's retry before the dirty bytes commit.

### Trace evidence (the empirical basis for the detection signature)

A 3-hour session captured in `request-traces/` gives the ground truth this design
is built on:

- **Rate**: 5 genuine leaks / 229 assistant responses ≈ **2.2%**, clustered in a
  ~12-minute window (17:23–17:35) — the poisoning signature.
- **Prefix token drifts**: the degraded token preceding the leak was `court` in
  all 5; a prior investigation (memory) recorded `call`. It is a tokenizer
  degradation artifact and **must not be matched on**.
- **`stop_reason` varies**: 2 of 5 were `tool_use`, 3 were `end_turn`. The leak
  can coexist with *valid* `tool_use` blocks in the same response
  (seq 0090 had a text block with both prose and leaked XML). **Cannot filter on
  `stop_reason`.**
- **Closure is the discriminator**: all 5 genuine leaks were fully closed and
  balanced — `<invoke>`=1 `</invoke>`=1, `<parameter>`=3 `</parameter>`=3, each
  ending in `</parameter>\n</invoke>`. By contrast, prose that merely *quotes*
  the syntax (the assistant's own analysis messages in the same trace, seq
  0244/0260) was **unbalanced** — `<invoke>`=3 `</invoke>`=1. Balanced closure is
  what separates a real leak from a quotation.

### Claude Code retry contract (verified, not assumed)

Read from decompiled claude-code 2.1.88 (`src/services/api/withRetry.ts`,
`errors.ts`):

- Once the bridge writes the first SSE byte, the HTTP 200 is committed
  (`HttpResponse.HasStarted == true`) and the status can no longer change. A
  stream-preserving fix therefore can only **inject an SSE `error` event**, not
  change the status.
- Mid-stream, the one signal Claude Code reliably retries is
  `overloaded_error`: `is529Error` matches the literal substring
  `"type":"overloaded_error"` in the streamed error (the SDK "sometimes fails to
  pass the 529 status during streaming", so CC checks the message text).
- **Anti-poisoning property**: on retry, Claude Code discards the *entire* current
  attempt — including any text already streamed this turn — and re-sends from the
  prior clean history. So even though the dirty bytes were already put on the
  wire before we detect the closing `</invoke>`, they never commit to the
  transcript. This is why "detect on close" is safe.
- After 3 consecutive `overloaded_error`s on a non-custom Opus model, CC falls
  back opus→Sonnet (or, for external users without a fallback model, surfaces
  "Repeated 529"). For a genuinely poisoned session this is the *correct*
  backstop — Sonnet does not exhibit the leak, and the manual remedy was always
  `/clear` or model switch.

## Goals / Non-Goals

**Goals:**
- Detect the leak in the `/cc/v1/messages` response with a structural signature
  that survives prefix-token drift and `stop_reason` variance.
- Force Claude Code to retry the turn cleanly, breaking the self-reinforcement
  loop by preventing the dirty text from committing to the transcript.
- Keep the default path streaming (no TTFT regression) and zero-cost when
  disabled.
- Make delivery and signal independently configurable via a dedicated config
  section.

**Non-Goals:**
- Repairing the leaked call into a valid `tool_use` block (fragile; the leak
  coexists with real `tool_use` blocks and varied `stop_reason`). Out of scope.
- Touching the `feature/tool-call-repair` branch — its JSON-repair stage fixes
  malformed JSON *inside real `tool_use` blocks*, a different failure.
- Detecting leaks on non-Copilot-Anthropic routes or the Codex `/responses` path.
- Guaranteeing zero false positives. The design drives the rate toward
  negligible; the default delivery makes a false positive cheap (one retry).

## Decisions

### D1. Detection signature = three structural conditions, ALL required

A block is a leak only if **all three** hold:

1. **Closed, balanced call block** in a single `text` or `thinking` block:
   `<invoke name="X">` … one or more closed `<parameter …>…</parameter>` …
   `</invoke>`. Balanced closure (matching open/close counts) is the primary
   discriminator, per the trace evidence.
2. **`X` ∈ the request's `tools[].name`.** Excludes teaching examples that use a
   fictitious tool name (`FooTool`, `get_weather`). The stage reads
   `ctx.Request.Body.Tools` (available on `IResponseStage<MessagesRequest>`).
3. **Not inside a markdown code fence** (` ``` `). Excludes the case a real
   Claude Code session hits: the assistant *explaining* tool-call syntax using a
   real, currently-active tool name (e.g. writing `<invoke name="Read">…` inside
   a fenced example). Genuine leaks are bare degraded XML and are never fenced
   (trace: 5/5 unfenced). Thinking blocks have no fence concept and are treated
   as always-unfenced.

*Rationale for stacking all three*: condition 1 alone is very low false-positive
but not zero — an assistant can write a fully-closed example. Condition 2 filters
fictitious names but is weak in this repo (real tools are discussed constantly).
Condition 3 is the one that actually closes the "teaching with a real tool name"
gap the user raised. A false positive now requires: a closed balanced block, a
real active tool name, *outside* any fence, in a text/thinking block —
vanishingly unlikely for legitimate prose.

*Explicitly NOT matched*: the prefix token (`court`/`call`, drifts),
`stop_reason` (both values observed), raw substring `<invoke` without closure
(that is what prose quotation looks like).

### D2. Two orthogonal knobs: `PreserveStream` × `Signal`

Delivery (how the error reaches the client) and signal (which error) are
independent. The 2×2 is fully valid; detection and error-object construction are
**shared**, only the emit channel differs.

| `PreserveStream` | `Signal` | On detection | CC reaction |
|---|---|---|---|
| `true` (default) | `OverloadedError` (default) | inject SSE `event: error` with `overloaded_error`, end stream (status stays 200) | retries; 3 consecutive → opus→Sonnet |
| `true` | `ApiError` | inject SSE `event: error` with `api_error`, end stream | mid-stream classification less certain |
| `false` | `OverloadedError` | buffer whole response; dirty → HTTP **529** + `overloaded_error` body | retries + fallback |
| `false` | `ApiError` | buffer whole response; dirty → HTTP **500** + `api_error` body | retried ~10×, no fallback |

`Signal` maps to both the Anthropic `error.type` string and (buffer mode) the
HTTP status: `OverloadedError`→529, `ApiError`→500. The final enum is exactly
`{OverloadedError, ApiError}` — `Refusal` is excluded (CC shows-not-retries it),
`RateLimit`/429 excluded (redundant with overloaded).

The default row (`true` + `OverloadedError`) also happens to avoid the one
uncertain cell (`true` + `ApiError`, whose mid-stream retryability is not
guaranteed by the CC contract).

### D3. Algorithm: single-pass streaming automaton (O(1) state, no content retention)

Detection is a finite-state automaton over a fixed token set — no accumulation
buffer, no regex re-scan. Each character of streamed text is examined exactly
once; the automaton node IS the cross-delta memory (a signature split as `<inv`
| `oke name="` across two deltas is carried by the node, not a carry buffer).

**Must be a true automaton with failure transitions (Aho-Corasick / KMP), NOT a
naive per-token "position counter that resets to 0 on mismatch".** A naive
counter drops a valid restart: on input `<<invoke name="`, the second `<`
mismatches the expected `i` and a naive counter resets to 0, missing that the
second `<` is itself a fresh start of the token — the following `invoke name="`
then never matches. The failure function must fall back to the longest proper
suffix that is still a prefix (here: node for a single `<`), so overlapping /
mid-token restarts are handled and nothing is missed at delta boundaries.

Fixed token set: `` ``` `` · `<invoke name="` · `">` · `<parameter` ·
`</parameter>` · `</invoke>`.

Per-block O(1) state, reset on `content_block_start`:
- `inFence` (bool) — toggled by `` ``` ``;
- `invokeOpen` (bool), captured `name` (short, bounded), `paramOpen`/`paramClose`
  (ints);
- current automaton node.

On `</invoke>` the block is a leak iff `invokeOpen && paramClose ≥ 1 &&
paramOpen == paramClose && name ∈ tools[] && !inFence`; otherwise reset
`invokeOpen`. This makes the three D1 conditions fall out of the single pass.

Consequences:
- **Time O(total bytes), memory O(1).** A 5810-char leaked block (trace seq
  0090) is handled in one pass; the opening `<invoke` can never "scroll out of a
  window" because no window exists — only state is retained.
- **Correctness does not depend on any buffer size.** `MaxScanChars` is therefore
  irrelevant to the tool-leak automaton; it applies only to *buffering* detectors
  (see D6) that must retain content (e.g. a future JSON-repair detector holding a
  `tool_use` block's `input_json`). Fail-open: `name` capture past a small bound
  → "not a valid invoke" → pass through.
- No two-phase "pending / waiting-for-close" state — the counters and the node
  are the whole machine.

### D6. Extensible detection framework: one inspection pass, many detectors

The guard is built as the first `IResponseDetector` behind a single
`ResponseInspectionStage`, not as a bespoke one-off stage. The stage parses the
SSE sequence once, tracks block index/type and fence state, and fans the
per-block text out to an ordered list of detectors. Adding a detector is
implement-interface + DI-register; the framework is untouched.

This is not a new capability shape — it **generalizes what the codebase already
does ad hoc**:
- `DoneFilterStage` drops the `[DONE]` event → a *filter/abort-of-one-event*
  action.
- `ResponseModelRewriteStage` rewrites `message_start`'s model → a *rewrite*
  action.

So the detector action kinds this change implements are all proven in-tree:
- `Abort(signal)` — terminate: stop forwarding, inject the error (stream) or flip
  status (buffer). **ToolLeakDetector uses this.**
- `DropEvent` — swallow one event. **DoneFilterDetector** (the `[DONE]` filter)
  uses this.
- `RewriteEvent(...)` — transform an event's payload in place.
  **ModelRewriteDetector** (restore the client model id in `message_start`) uses
  this.
- `RewriteBlock(...)` — transform a whole block's outgoing content at
  `content_block_stop`. Defined as the extension point a future JSON-repair
  detector (the `feature/tool-call-repair` branch's concern — malformed JSON
  inside *real* `tool_use` blocks) would use; **not implemented here**, and that
  branch is not touched.

**Scope for this change (decided: consolidate now)**: implement the framework and
migrate the two existing response stages into it as detectors, so the framework
is exercised by three real action kinds (`Abort`, `DropEvent`, `RewriteEvent`) on
day one rather than shipping paper extension points. `DoneFilterStage` and
`ResponseModelRewriteStage` become `DoneFilterDetector` and `ModelRewriteDetector`
and are removed as standalone stages; the response pipeline then wraps the event
stream **once** (one `ResponseInspectionStage`) instead of three times.

Hard constraint on the migration (per the CLAUDE.md contract-test directive): the
observable behavior of `[DONE]` filtering and model rewriting MUST stay
byte-identical. Two properties make this non-trivial and MUST be preserved:
- `ModelRewriteDetector` operates on **both** delivery modes — it rewrites the
  streaming `message_start` event AND the buffered response body's top-level
  `model`. So the framework's detector contract MUST cover the buffered path, not
  only streaming. `DoneFilterDetector` is streaming-only (a `DropEvent` on the
  `message`/`[DONE]` event).
- The existing ordering — DONE-filter runs before model-rewrite so the rewriter
  only sees events that will actually reach the client — MUST be reproduced by
  the detector order.

The existing `ResponseModelRewriteStageTests` (and any DoneFilter coverage) are
the migration's regression gate: they MUST pass unchanged against the migrated
detectors, or be re-expressed against the framework with the same assertions.

### D7. Data flow: detectors return actions, the framework owns the stream

Detectors never touch the event stream directly — they inspect and return an
*intent*; the framework (one place) renders it. This is what lets three action
kinds share one traversal.

Streaming render (`PreserveStream=true`), one wrapping iterator:

```
foreach event in upstream:
    ctx = framework.parse(event)          # block index/type, fence, feed tool-leak automaton
    action = None
    foreach d in detectors (in order):    # order reproduces prior stage order
        a = d.InspectEvent(event, ctx)
        if a != None: action = a; if a is Abort: break
    switch action:
        None             -> yield event                       # pass through
        DropEvent        -> record DroppedEvents (no yield)    # DoneFilter
        RewriteEvent(e2) -> yield e2                           # ModelRewrite
        Abort(signal)    -> yield synthetic error event; return  # ToolLeak (ends stream)
```

| Action | Stream expression | User |
|---|---|---|
| `None` | `yield event` unchanged | default |
| `DropEvent` | no yield, push to `DroppedEvents` | DoneFilter |
| `RewriteEvent(e2)` | yield the replacement event | ModelRewrite |
| `Abort(signal)` | yield one synthetic `error` event, then stop enumerating | ToolLeak |

- `ModelRewriteDetector.InspectEvent` returns `RewriteEvent` only for the first
  `message_start`, `None` otherwise. `DoneFilterDetector` returns `DropEvent` for
  `message`/`[DONE]`, `None` otherwise. `ToolLeakDetector` feeds each text/thinking
  delta to its automaton and returns `Abort` on the closing `</invoke>` match.

**Framing guarantee (why the two detectors have different granularity).**
Detectors receive fully-framed `SseItem`s from `SseParser`, never raw token
fragments — SSE frames on the blank line (`\n\n`), so the parser reassembles a
partial event (e.g. network bytes `data: [DON` then `E]\n\n`) before the
framework sees it. Consequences:
- `DoneFilterDetector` is a whole-event string compare (`evt.Data == "[DONE]"`);
  `[DONE]` is one event's complete data and is never split, so no state is
  needed. This matches the current `DoneFilterStage` behavior byte-for-byte.
- `ToolLeakDetector` operates one level deeper: the leak text lives *inside*
  `content_block_delta` events as `text_delta`, and a signature like
  `<invoke name="Read">` legitimately spans several such events
  (`…<inv` | `oke name="Read">…`). The automaton consumes the `text_delta`
  characters across events; the automaton node is the cross-event memory. This is
  exactly why it needs a state machine and per-request lifetime while
  `DoneFilterDetector` does not.

### D8. Detector lifecycle: new instance per request (no shared streaming state)

`ToolLeakDetector` carries cross-delta automaton state; per the CLAUDE.md rule
that a streaming state machine's state MUST NOT be shared across requests, a
detector CANNOT be a singleton holding that state. **Decision: detectors are
instantiated per request.** `ResponseInspectionStage.ApplyAsync` builds a fresh
detector set for each request (via a registered factory / transient resolution),
so each request's automaton, `message_start`-seen flag, etc. are isolated.
Stateless dependencies (`ILogger<T>`, bound options) still come from DI; only the
detector instances are per-request.

This is a change from the current `AddSingleton<DoneFilterStage>()` /
`AddSingleton<ResponseModelRewriteStage>()` registration — the migrated detectors
register as a per-request factory instead. `ModelRewriteDetector`'s only state (a
`bool` "already rewrote message_start") becomes naturally correct per request
rather than relying on the field being reset.

### D9. Detector covers both delivery modes

Each detector exposes both a streaming entry (`InspectEvent`) and a buffered
entry (`InspectBuffered(body)`), because `ModelRewriteDetector` legitimately acts
on both — it rewrites the streaming `message_start` AND the buffered body's
top-level `model`. The framework calls the entry matching `ctx.Response.Mode`:

- Streaming: the wrapping iterator above.
- Buffered: after the body is available, each detector's `InspectBuffered` may
  return a `RewriteBlock`-equivalent (whole-body rewrite → new `BufferedBody`) or
  `Abort` (→ set `Status`/error body). `ToolLeakDetector`'s buffered entry runs
  the automaton over reconstructed block text; `DoneFilterDetector`'s is a no-op
  (there is no `[DONE]` in a buffered body).

### D4. Stream delivery = pure wrapper; buffer delivery = endpoint change

Both delivery modes are properties of `ResponseInspectionStage` (D6); a detector
returns an action and the stage renders it per the delivery mode.

- `PreserveStream=true`: the stage wraps `ctx.Response.EventStream` with a
  transforming iterator (exactly like the existing `DoneFilterStage` /
  `ResponseModelRewriteStage`). Each event is yielded through unchanged; on an
  `Abort` action the wrapper yields a synthetic `SseItem` with event type `error`
  and the Anthropic error JSON, then stops enumerating. **No endpoint change.**
- `PreserveStream=false`: the stage must drain the upstream `EventStream` into
  memory before the endpoint writes any byte, decide clean/dirty, and — on dirty
  — set `ctx.Response.Status` (529/500) and `ctx.Response.BufferedBody` and flip
  `Mode` to `Buffered`; on clean, replay the buffered events as a stream. This
  **touches `ClaudeCodeMessagesEndpoint`**, which currently branches on
  `Mode == Streaming`. No existing "buffer whole stream then return" helper
  exists to reuse (passthrough is lazy-yield; `RawResponseCapture` only tees raw
  bytes for trace; the buffered branch runs only when upstream is already
  non-streaming), so this is net-new code.

### D5. Config: dedicated `Pipeline:ToolLeakGuard` section

Sibling of `Pipeline:UpstreamRetry` / `Pipeline:ResponseModelRewrite`. Bound to a
new `ToolLeakGuardOptions` in `Hosting/Options/`, registered like the others.
Per-knob `_Xxx` comments (config is intentionally verbose here for operator
clarity):

```jsonc
"ToolLeakGuard": {
  "_comment": "Detects a Copilot-served Claude model leaking a tool call as literal <invoke name=\"X\"><parameter…></parameter></invoke> XML inside a text/thinking block (not a real tool_use block), then forces the client to retry the turn cleanly. Detection is STRUCTURAL and requires ALL of: (1) a single block with a CLOSED, balanced <invoke>…</invoke> containing >=1 closed <parameter>; (2) the tool name is in the request's tools[]; (3) it is NOT inside a markdown ``` code fence. It does NOT key off the drifting prefix token ('court'/'call'), stop_reason (both tool_use and end_turn seen), or a bare unbalanced <invoke. Grounded in trace evidence: 5/229 (~2.2%) in a poisoned 3h session, all 5 closed and unfenced.",
  "Enabled": true,
  "_PreserveStream": "true (default): keep streaming, inject the error mid-stream on detection (TTFT preserved; only dirty turns pay). false: buffer the whole response and emit a real HTTP status on a dirty turn — sacrifices streaming for ALL requests, not just dirty ones.",
  "PreserveStream": true,
  "_Signal": "OverloadedError (default) => Anthropic overloaded_error; buffer-mode HTTP 529; Claude Code retries it and 3 consecutive trigger opus->Sonnet fallback. ApiError => generic api_error; buffer-mode HTTP 500; retried ~10x with no model fallback (mid-stream retryability less certain than overloaded_error).",
  "Signal": "OverloadedError",
  "_ScanThinking": "Also scan thinking blocks (default true). Trace leaks were all in text blocks, but memory notes thinking blocks can leak too.",
  "ScanThinking": true,
  "_MaxScanChars": "Content-retention cap for BUFFERING detectors only (0 = unbounded). The tool-leak detector is a single-pass automaton that retains no content, so this does NOT affect it; it bounds a future JSON-repair-style detector that must hold a block's bytes. Exceeding it fails open (pass-through).",
  "MaxScanChars": 10000
}
```

If the injected error body needs source-generated serialization, add the error
DTO to `Models/JsonContext.cs` (AOT: no reflection-based `Serialize`). The
existing `ErrorResponse`/`ErrorBody` types (already in `JsonContext`) are reused
where possible.

## Risks / Trade-offs

- **False positive: assistant writes a closed, real-tool-named, unfenced
  `<invoke>` example** → still possible but requires defeating all three
  conditions at once. Mitigation: default delivery (`PreserveStream=true` +
  `OverloadedError`) makes it cheap — one retry, and the model rarely regenerates
  the identical unfenced example, so it self-heals; the 3-strike fallback only
  triggers on genuine repetition.
- **False negative: mixed leak where the `<parameter>` values stream as a real
  `tool_use` block and only `<invoke name="X">` leaks as text** → may not present
  a closed block in the text, so not detected. Accepted: rarer, and detecting it
  reliably would require heuristics that reintroduce false positives.
- **`PreserveStream=false` regresses TTFT for 100% of requests**, not just the
  2.2% dirty ones (must buffer every response to be able to change the status).
  Mitigation: it is opt-in; the default preserves streaming. Documented in the
  `_PreserveStream` comment.
- **`PreserveStream=false` touches the hot-path endpoint** → more review surface,
  risk of altering the clean-response path. Mitigation: gate strictly on the
  option; when `PreserveStream=true` (default) the endpoint is untouched. Buffer
  mode may be deferred to a follow-up if the endpoint change proves invasive
  (the enum value and options still ship; the stage returns pass-through with a
  warning if buffer mode is selected but not yet wired).
- **`overloaded_error` masquerade could confuse operators** reading traces ("why
  a 529 with no upstream 529?"). Mitigation: the stage logs a distinct
  Warning/Debug line naming the leak detection and the tool; the summary log
  records it.
- **Detect-on-close means dirty text is already on the wire in stream mode.**
  Accepted by design — the CC anti-poisoning property (retry discards the whole
  attempt) means it never commits. Buffer mode avoids even the transient
  appearance at the cost of TTFT.

## Migration Plan

- Additive and default-on, but inert unless a leak is detected on a
  Copilot-Anthropic `/cc/v1/messages` response. No request-wire change, no schema
  change to existing DTOs.
- Rollback: set `Pipeline:ToolLeakGuard:Enabled = false` (no rebuild) — the stage
  becomes a no-op with zero allocation. Removing the stage from the pipeline is
  the code-level rollback.
- Ship stream delivery first (pure wrapper, no endpoint risk); buffer delivery
  can land in the same change or a follow-up per the risk note above.

## Open Questions

- Should a detected leak be surfaced in the per-request summary log as a
  first-class field (e.g. `toolLeakDetected=true`) for observability? Leaning
  yes; cheap and useful for measuring real-world rate.
- Buffer mode (`PreserveStream=false`): implement in this change or defer to a
  follow-up? Design supports both; decision can be made at task time based on how
  invasive the endpoint change is.
