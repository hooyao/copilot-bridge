## Context

`POST /cc/v1/messages` and `POST /cc/v1/messages/count_tokens` currently have
different routing semantics. The messages endpoint deserializes the Claude Code
request, runs `ModelRouterStage`, and—when a Location changes the model to a
Copilot Responses model—uses `ResponsesRequestBuilder` to produce the T2 wire
body. The count endpoint instead probes only the source model string and forwards
the original Anthropic bytes to Copilot's `/v1/messages/count_tokens` endpoint.

The production capture demonstrates why source-protocol counting is not valid
after a cross-protocol route:

| Measurement | Tokens |
| --- | ---: |
| Original Anthropic request, including 58 tools | 856,113 |
| Converted Responses history without tools | 897,009 |
| Converted Responses tool cost | 25,724 |
| Estimated full target request | 922,733 |
| Highest observed GPT-5.6 Sol admission | approximately 921,566 |

The request contained 1,681 messages, 911 tool-use/result pairs, and 58 tools.
Most inflation came from historical Anthropic-to-Responses framing, not tool
schemas. A direct probe also established that Copilot's existing
Anthropic-named count endpoint accepts a converted Responses body and returned
902,134. Copilot exposes no native `/responses/count_tokens` endpoint.

These adjacent production measurements motivate the change but do not, by
themselves, freeze a calibration constant: Claude Code's count request can omit
system, output, stream, and effort fields present on the main messages request.
The approximately 922,733 target estimate and 921,566 boundary are calibration
evidence only after an exact-body replay proves that the count baseline and
Responses usage/admission oracle evaluated the same canonical T2 input. D3 makes
that pairing mandatory.

Claude Code 2.1.221 does not use `/count_tokens` as the sole preflight gate for
every main-loop request; it also derives context pressure from prior response
usage. Therefore route-aware counting improves accounting but cannot by itself
guarantee recovery. The target's generic context 400 must also be translated into
the prompt-too-long vocabulary Claude Code recognizes.

The design must preserve Native AOT, source-generated JSON, first-match-wins
Location behavior, exact native-Anthropic count passthrough, and the rule that the
bridge never edits a client's conversation to make it fit.

## Goals / Non-Goals

**Goals:**

- Resolve count and message requests through one Location/model/profile
  implementation while evaluating the fields present on each request.
- Transform the exact input supplied to count-tokens with the same T2 rules used
  by messages, rather than count the source Anthropic framing.
- Return a conservative, probe-grounded target-equivalent input-token estimate.
- Make a confirmed target context rejection recoverable by Claude Code.
- Preserve raw upstream evidence and clearly report requested model, resolved
  model, raw count, calibration, and returned count.
- Keep native Anthropic and native Codex paths unchanged.

**Non-Goals:**

- Add or emulate a public `/responses/count_tokens` endpoint.
- Send a production generation request to `/responses` merely to discover a
  token count.
- Implement a new tokenizer dependency or tokenize serialized JSON as if JSON
  syntax equaled Responses semantic framing.
- Guarantee an exact target count; the contract is conservative admission
  accounting, not billing equivalence.
- Change Claude Code itself, silently truncate history, invoke compaction inside
  the bridge, or globally reduce every Claude model's context window.
- Add the main loop's system prompt, output budget, stream flag, effort, or other
  absent fields to an auxiliary count request merely to approximate a complete
  turn.
- Change native `/codex` error envelopes or native `/cc` Anthropic error
  passthrough.

## Decisions

### D1 — Extract one route-planning seam used by messages and count-tokens

The route decision SHALL be made from each typed Anthropic request plus its own
inbound headers/beta set. Normalization, first-match-wins Location evaluation,
`Use.Model`, `EffortMap`, header/beta match inputs, vendor dispatch, and target
profile validation SHALL have one implementation shared by both endpoints. A
count request is not required to be byte- or field-identical to a later messages
request: Claude Code also uses count-tokens for isolated file content, MCP output,
and context analysis.

The count endpoint will use an isolated per-request context and only the planning
and target-request-building portions of the request pipeline. It will not run the
upstream generation strategy or response detectors.

Alternative: duplicate the Location scan in `ClaudeCodeCountTokensEndpoint`.
Rejected because later routing features could change `/messages` without changing
counting and silently recreate the bug.

Alternative: run the entire normal pipeline and intercept it before generation.
Rejected because the runner deliberately couples request stages to a selected
upstream strategy and response stages; count-tokens needs a smaller side-effect-
free route plan.

### D2 — Native Anthropic remains raw passthrough; Responses transforms the exact count input

If planning resolves to `CopilotAnthropic`, the endpoint SHALL retain today's raw
request bytes and upstream count response bytes. This preserves fields that the
bridge's request DTO does not model and avoids changing a working protocol.

If planning resolves to `CopilotResponses`, the endpoint SHALL call the shared
T1/T2 translation with the count request's actual messages, tools, thinking,
betas, and optional metadata. The exact resulting bytes—not a separately
reconstructed approximation—are posted to Copilot's
`/v1/messages/count_tokens` endpoint. Fields absent from the count request remain
absent; in particular, the bridge does not synthesize a messages endpoint's
system blocks, `max_tokens`, stream flag, or effort. Count requests do not call
`/responses` and cannot cause model generation or tool use.

Because the native path intentionally preserves unknown JSON as raw bytes, the
Responses branch needs an AOT-safe count-request parsing contract covering every
token-bearing body field Claude Code 2.1.221 actually sends: `model`, `messages`,
`tools`, and the fixed enabled-thinking shape it adds when messages contain
thinking blocks. The SDK removes its `betas` argument from the JSON body and
sends those tokens in the `anthropic-beta` header together with
`token-counting-2024-11-01`; routing reads that real header representation. The
wire inputs are translated according to target T2 semantics, so an Anthropic-
only control need not be copied literally when Responses has no equivalent. The
implementation may reuse compatible model types, but it SHALL NOT silently
default generation-only fields or ignore an unknown/unsupported count field.
Such a cross-routed shape fails explicitly until modeled or documented as a
target T2 drop; native Anthropic passthrough remains forward-compatible and
unchanged.

The recursive-agent tool filter is part of T2 and therefore must make the same
decision for equal route context. Any future applicable T2 field/drop/coercion
automatically applies to both paths by sharing the builder. Byte equality is
asserted between the endpoint output and a direct build from the same count
input, not between two different client requests.

Alternative: change only `model` in the original Anthropic body to
`gpt-5.6-sol`. Rejected because it counts the source message/tool framing, not the
request that the target admits.

### D3 — Convert the upstream transformed-body count through a versioned conservative calibrator

The result returned by Copilot for a Responses body is a useful baseline but is
not the target-equivalent input count. A pure `ResponsesAdmissionEstimator`
SHALL apply a model-specific calibration record to the upstream count. The
record contains at least a multiplicative scale, fixed framing allowance, and
safety reserve, along with an evidence/version label. Arithmetic SHALL reject
malformed, missing, negative, and out-of-range upstream counts, remain monotonic,
and saturate safely at the response DTO's numeric limit.

The constants are backend facts, not guesses. Before implementation freezes
them, the live probe corpus SHALL cover minimal, long-history, tool-heavy, and
near-boundary requests, including the captured production request. Each data
point must be paired: the exact T2 bytes sent to count-tokens are also submitted
with equivalent semantics to a test-only Responses usage/admission oracle. A
count measured from body A must never be calibrated against usage or admission
from body B. For every guarded item, the returned estimate must be no lower than
the target-equivalent input usage or observed admission requirement for that same
input. Minimal auxiliary inputs also have a documented maximum over-count so a
fixed reserve cannot make file/MCP validation unusable.

Every supported Responses model should have an exact calibration. A newly
discovered Responses model without one SHALL use a deliberately conservative
global fallback derived from the worst observed error bound and emit a warning.
If transformed-body counting itself fails, the bridge SHALL return that failure;
it SHALL NOT silently fall back to the smaller original Anthropic count.

Alternative: trust the transformed-body count directly. Rejected by the live
902,134 versus approximately 922,733 measurement.

Alternative: tokenize the full JSON document with `o200k_base`. Rejected because
wire JSON tokens are not the backend's structured Responses framing and initial
experiments were inaccurate.

Alternative: call `/responses` with `max_output_tokens` minimized. Rejected
because that is generation, has side effects/cost, can execute a semantically
real request, and still does not provide a safe count-only contract.

### D4 — Translate only the confirmed CC-to-Responses context rejection

When all of the following hold, the buffered error path SHALL return an Anthropic
`invalid_request_error` whose message contains the exact classifier phrase
`prompt is too long`:

1. the downstream endpoint is `/cc`;
2. routing selected a `CopilotResponses` target;
3. upstream returned HTTP 400; and
4. the bounded parsed error matches the confirmed Copilot context-window
   rejection (`code=invalid_request_body` plus the known context-window message).

The production response labels this JSON body `text/plain; charset=utf-8`, so
matching SHALL be driven by the bounded body shape rather than requiring an
`application/json` upstream content type. The classifier declines bodies above
its documented inspection limit and makes no second unbounded copy or parse;
this change does not redesign the strategy's pre-existing buffered-response
transport limit.

The bridge SHALL not invent actual/limit token numbers when the upstream response
did not supply them. Claude Code recognizes the phrase without counts; the counts
only optimize how aggressively its reactive compactor prunes.

All other 400s remain unchanged. `/codex` remains Responses-native, and `/cc`
requests resolved to Anthropic retain their existing upstream error behavior.
Tracing records the original upstream status, headers, and body before the
downstream rewrite.

Alternative: rewrite every 400 from a GPT model. Rejected because tool-schema,
effort, model-access, and malformed-request failures must not trigger destructive
conversation compaction.

### D5 — The count result is advisory; the error rewrite is the recovery guarantee

Route-aware count-tokens cannot be treated as the only admission gate because
current Claude Code's main loop primarily uses prior response usage plus local
estimation. The proposal therefore does not claim proactive compaction solely
from the count endpoint. Its guaranteed outcome is:

- consumers of `/count_tokens` receive a target-aware estimate for the exact
  input they asked to count; and
- if the real request still reaches the target boundary, Claude Code receives a
  recognized prompt-too-long error and can compact/retry.

A future change may configure a client-side auto-compact window from route data,
but that global Claude Code knob would also cap native 1M Claude routes and is
outside this change.

### D6 — Preserve history and expose both source and target accounting

The bridge SHALL never remove messages, tool pairs, system blocks, tools, or
content for the purpose of fitting the target window. Only the pre-existing T2
translation/coercions may affect the count body, exactly as they affect the real
message request.

For a cross-routed count, the summary/logging model SHALL distinguish:

- requested and resolved model;
- the raw upstream transformed-body count;
- the calibration id and applied reserve; and
- the final count returned to Claude Code.

With tracing enabled, `inbound-req` retains the original Anthropic count request,
`upstream-req` records the exact T2 Responses body posted to the count endpoint,
`upstream-resp` records Copilot's raw count response, and `inbound-resp` records
the calibrated Anthropic count response. With tracing disabled, no full prompt
copy is retained solely for accounting.

### D7 — Contract tests are anchored in captured bytes and live backend facts

Tests SHALL establish the contract before implementation:

- identical route inputs resolve to the same target on message and count paths,
  while endpoint output equals a direct shared-T2 build from that endpoint's own
  request fields;
- native Anthropic count remains byte passthrough;
- captured cross-routed requests never under-count the guarded target usage;
- minimal auxiliary count requests remain within the documented over-count
  bound, and invalid upstream counts fail explicitly;
- the confirmed context 400 becomes prompt-too-long only at the Claude edge;
- unrelated errors and native client paths are unchanged; and
- trace artifacts preserve raw upstream evidence.

Mutation checks SHALL break route sharing, remove calibration, and disable the
error classifier in turn; the corresponding tests must fail.

Final acceptance SHALL drive real headless `claude.exe` through the `CcToGpt`
scenario. A deterministic boundary leg must make the client encounter the exact
context rejection, compact/retry, then complete a multi-step task with real tool
execution. The captured production request is replayed separately as an API-
contract test to prove the count and target body shapes at realistic scale.

### D8 — Exercise reactive compaction with a deterministic Debug-only upstream

The ClientBehavior case SHALL boot the real Debug bridge subprocess with
`COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL`, using the same guarded override already
employed by fault-recovery tests. A scenario-specific upstream SHALL accept a
small real count request, inject the exact confirmed Responses context 400 on a
tool-bearing `/responses` request, answer Claude Code's compaction-summary turn,
and then drive the retry through Bash and Read before returning a final canary.
Auxiliary title/summary traffic SHALL be identified without consuming main-task
phases. The scenario does not depend on a million-token body or live Copilot.

The test invocation SHALL use an isolated `CLAUDE_CONFIG_DIR` and retain session
persistence, unless the selected stream-json client version is first proven to
emit an equivalent compact event directly. PASS requires client-owned evidence:
a persisted `system/compact_boundary` with `compactMetadata.trigger=auto`, a
retry after that boundary, a post-boundary `tool_use` → `tool_result` round trip,
and the final canary. Exit code, request count, bridge HTTP 200, and bridge traces
alone are inconclusive. Traces additionally prove the raw 400 was preserved and
T3-internal markers did not cross the Claude edge.

The environment override SHALL remain compiled out of Release. Verification
must show a Release/AOT binary ignores or lacks the override; a source inspection
of `#if DEBUG` alone is insufficient release evidence.

### D9 — Coverage matrix separates pure contracts, backend facts, and client behavior

| Contract | Required test layer | Failure it must catch |
| --- | --- | --- |
| Route/profile/header parity | Pure unit tests over the shared planner, including header/beta match inputs and concurrent isolation | Count endpoint duplicates matching logic or leaks route state |
| Native Anthropic preservation | Buffered endpoint test with a recording fake Copilot client | Parse/reserialize, calibration, or status/body drift on passthrough |
| Responses count construction | Buffered endpoint test plus direct shared-T2 oracle, with all Claude Code count variants and unsupported-field failure | Wrong model/body, inferred absent fields, silently dropped token-bearing fields, extra count call, or any `/responses` generation call |
| Recursive Agent filtering | Unit tests with equal parent/sub-agent route contexts | Count and messages apply different T2 tool filters |
| Estimator arithmetic | Table/property unit tests | Under-count, non-monotonicity, overflow, invalid-value acceptance, unbounded minimal over-count, or silent unknown-model use |
| Calibration backend facts | Live `Kind=ApiContract` exact-body pairs plus sanitized production-shape replay | Formula fitted to different bodies or stale Copilot behavior |
| Error rewrite scope and trace | Buffered endpoint matrix with production `text/plain`, bounded malformed/oversized bodies, and all scope near-misses | Content-type-gated recovery, broad substring rewrite, native-path drift, lost raw evidence, or request-size gating |
| Compact/retry/tool execution | Real `claude.exe`, Debug bridge subprocess, deterministic upstream, persisted client transcript | A bridge-side/request-count false positive with no real compact or post-compact dispatch |
| Test-hook containment and AOT | Release/AOT launch against a listening fake upstream | Debug upstream override or reflection-only JSON accidentally ships |

Contract tests SHALL be mutation-checked at the seams named above. The
ClientBehavior test is intentionally small and deterministic; the separate live
and captured-byte API-contract tests own backend calibration scale and production
shape.

## Risks / Trade-offs

- **Undocumented transformed-body count behavior changes upstream** → Keep the
  live contract probe, fail explicitly rather than returning a known-low source
  count, and retain the prompt-too-long recovery path.
- **Calibration drifts as Copilot changes tokenizer/framing** → Version records,
  guard a varied captured corpus, and extend the live backend contract sweep for
  both disappearance and changed error bounds.
- **Conservative estimates compact/report early** → Bound and publish observed
  over-count; prefer a modest false high to the production false low.
- **Route matching diverges through future refactoring** → One shared planning
  seam and byte-level parity tests, not duplicated endpoint logic.
- **Error phrase matching catches an unrelated 400** → Require status, code,
  target vendor, client endpoint, and the confirmed context message together.
- **Large count requests duplicate memory** → Reuse pooled inbound reading and
  the single T2 byte array; do not hold an additional JSON DOM beyond the typed
  request lifetime. Measure the 3.7 MiB production capture.
- **Added DTO/serialization fails under Native AOT** → Use `JsonContext` entries
  and publish the native binary as an acceptance gate.

## Migration Plan

1. Add the contract and live calibration probes before product changes.
2. Extract the shared route-planning seam without changing `/messages` behavior.
3. Add the route-aware count branch and conservative estimator, retaining native
   Anthropic raw passthrough.
4. Add the narrowly scoped context-error translation and observability.
5. Run unit, captured-byte/API-contract, live boundary, Native AOT, and real
   Claude Code acceptance gates.

No configuration migration is required. Rollback is a code revert; the endpoint
shape remains Anthropic-compatible and no persisted data is introduced.

## Open Questions

- What exact scale/fixed allowance/reserve values satisfy the full calibration
  corpus for each currently supported Responses model? Task 2 freezes these only
  after the live sweep; the initial GPT-5.6 Sol evidence proves a correction is
  required but is not by itself a sufficient universal formula.
- Does Copilot's transformed-body count remain additive across every hosted tool
  type, or are separate calibration classes required for image/hosted-tool
  requests? Until probed, the conservative fallback covers unknown classes.
