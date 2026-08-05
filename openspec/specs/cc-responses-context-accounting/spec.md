# cc-responses-context-accounting Specification

## Purpose

Ensure Claude Code requests routed from Anthropic Messages models to Copilot
Responses models are counted in the target protocol's token space and can
recover when the target still rejects an oversized prompt. This specification
also preserves native Anthropic count passthrough, raw trace evidence, and
client-owned conversation history while requiring real-client compact/retry
proof.

## Requirements
### Requirement: Count-tokens resolves the same route as messages

For a Claude Code request body and inbound header set, the bridge SHALL apply the
same model normalization, first-match-wins `Routing.Locations` evaluation,
effort mapping, target-profile validation, and backend-vendor selection to
`POST /cc/v1/messages/count_tokens` as it applies to
`POST /cc/v1/messages`. The count path SHALL NOT maintain an independent routing
implementation. Each endpoint SHALL evaluate only the model, headers, betas,
effort, and content present on that request; the count endpoint SHALL NOT copy
fields from a prior or hypothetical messages request.

#### Scenario: Model Location selects a Responses target

- **WHEN** a count-tokens request names `claude-opus-5` and the first matching
  Location rewrites that model to `gpt-5.6-sol`
- **THEN** count-tokens resolves `gpt-5.6-sol` on the Copilot Responses backend
- **AND** the equivalent messages request resolves the same target.

#### Scenario: Header and effort conditions have parity

- **WHEN** a Location match depends on an inbound header, Anthropic beta token,
  or request effort and the same body and headers are supplied to count-tokens
  and messages
- **THEN** either both paths select that Location or neither path selects it
- **AND** any configured effort mapping is identical on both paths.

#### Scenario: An absent optional route input stays absent

- **WHEN** a count-tokens request omits effort or another optional route input
- **THEN** routing evaluates that input as absent
- **AND** does not borrow the value from a prior messages request or configured
  main-loop assumption.

#### Scenario: First matching Location still wins

- **WHEN** more than one Location could match a count-tokens request
- **THEN** only the first Location is applied
- **AND** no route chaining or fall-through occurs.

#### Scenario: Concurrent requests do not share route state

- **WHEN** count and messages requests with different matching headers execute
  concurrently
- **THEN** each resolves solely from its own body and headers
- **AND** neither request observes the other's target, effort, or profile.

### Requirement: Native Anthropic counting remains byte passthrough

When route planning resolves a count-tokens request to a Copilot Anthropic
backend, the bridge SHALL post the original inbound body bytes to Copilot's
`/v1/messages/count_tokens` endpoint and SHALL relay the upstream status and body
without calibration or protocol translation.

#### Scenario: Unmatched native Claude request

- **WHEN** a count-tokens request resolves to its native Claude model without a
  cross-protocol Location
- **THEN** the upstream request body is byte-for-byte equal to the inbound body
- **AND** a successful upstream count response is byte-for-byte equal to the
  downstream response.

### Requirement: Responses targets transform the exact count input with shared T2 rules

When route planning resolves a Claude Code count-tokens request to a Copilot
Responses backend, the bridge SHALL transform the fields actually present on
that count request through the same applicable T1/T2 rules as the messages path
and SHALL submit the resulting exact bytes to Copilot's
`/v1/messages/count_tokens` endpoint. It SHALL NOT infer system blocks, output
limits, stream settings, effort, or other fields absent from the count input.
The count path SHALL NOT call `/responses` and SHALL NOT initiate model
generation. Its AOT-safe parsing contract SHALL cover every token-bearing count
body field emitted by the supported Claude Code version; the cross-routed branch
SHALL NOT silently drop a present recognized field or default generation-only
fields through an incompatible request shape.

#### Scenario: Cross-routed body uses Responses framing

- **WHEN** `claude-opus-5` is routed to `gpt-5.6-sol`
- **THEN** the upstream count body names `gpt-5.6-sol`
- **AND** its input items, tools, thinking configuration, and applicable T2
  drops/coercions equal a direct shared-T2 build from that same count input.

#### Scenario: Count-only shape is not expanded into a whole main-loop turn

- **WHEN** Claude Code asks to count isolated messages or tools and the request
  omits system, output limit, stream, or effort fields
- **THEN** the transformed count body omits those absent fields
- **AND** the returned value estimates that isolated input rather than a
  hypothetical complete messages turn.

#### Scenario: Claude Code count variants preserve their token-bearing fields

- **WHEN** Claude Code counts messages alone, tools with its dummy message,
  beta-enabled input, or messages containing thinking blocks
- **THEN** the transformed body accounts for the supplied messages, tools, and
  fixed count-time thinking configuration according to documented target T2
  semantics
- **AND** beta routing reads the SDK's actual `anthropic-beta` header, including
  the token-counting beta, rather than expecting `betas` in the JSON body
- **AND** no variant is silently reduced to only model and messages.

#### Scenario: Unsupported cross-routed count field fails visibly

- **WHEN** a Responses-routed count request contains a token-bearing field the
  typed count/T1 translator neither models nor explicitly documents as a target
  T2 drop
- **THEN** the bridge returns an explicit unsupported-count-shape failure
- **AND** does not ignore the field and return a known-low successful count
- **AND** the same field remains byte-preserved on native Anthropic passthrough.

#### Scenario: T2 changes automatically affect counting

- **WHEN** the shared T2 builder changes a field, coercion, or tool filter for a
  Responses message request
- **THEN** the count body receives the same change without a second endpoint-
  specific implementation.

#### Scenario: Recursive Agent filtering has route-context parity

- **WHEN** the same recursive-agent route context and tool set are translated by
  count-tokens and messages
- **THEN** both apply the same Agent-tool inclusion or omission rule
- **AND** neither mutates the source request's tool collection.

#### Scenario: Counting cannot execute the prompt

- **WHEN** a cross-routed count-tokens request succeeds
- **THEN** exactly one count request is sent to Copilot's count endpoint
- **AND** no request is sent to `/responses`, no response stream is opened, and
  no client tool can be invoked.

### Requirement: Cross-routed counts conservatively represent the exact target input

The bridge SHALL convert the raw count returned for a transformed Responses body
through a versioned model-specific admission calibration. The calibrated result
SHALL be monotonic and SHALL be no lower than target-equivalent input usage for
every request in the guarded calibration corpus. Every calibration datum SHALL
pair the exact T2 bytes sent to count-tokens with equivalent Responses usage or
admission evidence for that same input. An unknown Responses model SHALL use a
documented conservative fallback and emit a warning; it SHALL NOT use an
uncalibrated raw count as if it were exact.

#### Scenario: Production capture does not under-count

- **WHEN** the captured 1,681-message, 911 tool-pair, 58-tool Claude Code request
  is routed to `gpt-5.6-sol` and counted
- **THEN** the final returned count is no lower than the target admission usage
  established for those exact transformed bytes by the paired captured-byte and
  boundary probes
- **AND** it is greater than the raw transformed-body count when that raw count
  under-reports target usage.

#### Scenario: Calibration corpus covers distinct request shapes

- **WHEN** minimal, long-history, tool-heavy, and near-boundary guarded requests
  are evaluated
- **THEN** no calibrated estimate is lower than its established target usage
- **AND** the observed over-count for each case is recorded for review.

#### Scenario: Calibration never compares different bodies

- **WHEN** a live or replay calibration case is recorded
- **THEN** the count baseline and target usage/admission observation identify the
  same canonical T2 bytes and semantic input
- **AND** evidence derived from a differently shaped body is rejected as a
  calibration datum.

#### Scenario: Minimal auxiliary count has bounded over-count

- **WHEN** Claude Code counts a minimal file fragment, MCP result, isolated
  message, or isolated tool set
- **THEN** the result is no lower than the paired target-equivalent input usage
- **AND** it does not exceed the documented minimal-input over-count bound
- **AND** it does not include a synthetic main-loop system prompt or output
  reservation.

#### Scenario: Newly discovered Responses model uses conservative fallback

- **WHEN** routing selects a live Responses model with no exact calibration
  record
- **THEN** the bridge applies the global conservative fallback
- **AND** logs the missing calibration and resolved model without logging prompt
  or tool content.

#### Scenario: Calibration arithmetic is monotonic and bounded

- **WHEN** two valid raw counts for one calibration satisfy `a <= b`
- **THEN** the calibrated results satisfy `estimate(a) <= estimate(b)`
- **AND** checked arithmetic cannot wrap to a smaller or negative result
- **AND** values above the response numeric range saturate at its documented
  maximum.

#### Scenario: Invalid upstream count is not calibrated

- **WHEN** the count response is malformed, omits `input_tokens`, or supplies a
  negative, fractional, non-numeric, or out-of-range value
- **THEN** the bridge returns an explicit count failure
- **AND** does not treat the value as zero, saturate an invalid signed value, or
  fall back to source-protocol counting.

#### Scenario: Transformed-body count fails

- **WHEN** Copilot rejects or fails the transformed-body count request
- **THEN** the bridge returns an explicit count failure
- **AND** does not retry with the original lower Anthropic count or fabricate a
  successful count.

### Requirement: Claude Code receives a recoverable target context error

For a `/cc` messages request routed to a Copilot Responses backend, the bridge
SHALL translate the confirmed Copilot context-window rejection into an Anthropic
HTTP 400 `invalid_request_error` whose message contains `prompt is too long`.
The bridge SHALL preserve the raw upstream error for tracing and SHALL NOT invent
token numbers absent from the upstream response.

#### Scenario: Confirmed GPT context rejection is translated

- **WHEN** a `/cc` request resolved to a Responses target receives HTTP 400 with
  `code=invalid_request_body` and Copilot's confirmed context-window message
- **AND** the upstream body carries the observed `text/plain; charset=utf-8`
  content type despite containing JSON
- **THEN** Claude Code receives HTTP 400 with an Anthropic error envelope
- **AND** its message contains `prompt is too long`
- **AND** the original Responses error envelope does not cross the Claude edge.

#### Scenario: Exact scope guards are all required

- **WHEN** any one of HTTP status 400, `code=invalid_request_body`, the confirmed
  context-window message, `/cc`, or a resolved Responses target is absent
- **THEN** the bridge does not label the response prompt-too-long.

#### Scenario: Error text in an unrelated field does not match

- **WHEN** the confirmed sentence appears only in an unrelated JSON field while
  `error.message` or `error.code` does not match
- **THEN** the bridge does not label the response prompt-too-long.

#### Scenario: Malformed or oversized error bodies remain bounded

- **WHEN** the upstream error body is malformed JSON or exceeds the bounded
  classifier parse limit
- **THEN** classification does not perform an unbounded second copy or parse
- **AND** the response is not labeled prompt-too-long merely by a raw substring
  search.

#### Scenario: Classification does not depend on request size

- **WHEN** a small `/cc` request routed to Responses receives the exact confirmed
  context-window rejection
- **THEN** it is translated identically to a large request
- **AND** no local request-body-size heuristic suppresses recovery.

#### Scenario: Native client paths are unchanged

- **WHEN** the same upstream error occurs on native `/codex`, or a `/cc` request
  remains on a Copilot Anthropic backend
- **THEN** this cross-protocol error translation is not applied.

#### Scenario: Exact 400 preserves downstream protocol metadata

- **WHEN** the confirmed context rejection is translated
- **THEN** the downstream status remains HTTP 400
- **AND** the response is Anthropic JSON with the existing Claude endpoint's
  error content type and envelope conventions
- **AND** unrelated upstream headers are not used to mislabel the body as a
  Responses-native envelope.

### Requirement: Context accounting never mutates conversation history

The bridge SHALL NOT drop, truncate, summarize, or reorder conversation content
to satisfy a target context limit. The count body may differ from the inbound
body only through the same route and T2 transformation used for the real target
request.

#### Scenario: Oversized history remains client-owned

- **WHEN** a cross-routed request exceeds the target context window
- **THEN** the bridge returns target-aware accounting or the recoverable prompt-
  too-long error
- **AND** leaves compaction and retry to Claude Code
- **AND** does not send a shortened hidden request upstream.

### Requirement: Cross-routed accounting is observable without hiding raw evidence

For a cross-routed count, summaries SHALL identify requested model, resolved
model, raw upstream count, calibration identity, and returned count. With tracing
enabled, the four trace boundaries SHALL distinguish the original Anthropic
request, exact transformed upstream body, raw Copilot response, and calibrated
downstream response. With tracing disabled, the feature SHALL NOT retain an
additional full prompt copy after the request completes.

#### Scenario: Count trace records both protocol shapes

- **WHEN** tracing is enabled and a Claude count request is routed to Responses
- **THEN** `inbound-req` contains the original Anthropic body
- **AND** `upstream-req` contains the exact T2 Responses body posted to the count
  endpoint
- **AND** `upstream-resp` contains Copilot's unmodified count response
- **AND** `inbound-resp` contains the calibrated Anthropic count response.

#### Scenario: Context error trace keeps upstream wording

- **WHEN** a confirmed context 400 is translated for Claude Code with tracing
  enabled
- **THEN** `upstream-resp` retains the original Copilot status, headers, and body
- **AND** `inbound-resp` records the Anthropic prompt-too-long envelope.

#### Scenario: Sensitive content is not added to ordinary logs

- **WHEN** route-aware counting or calibration is logged while tracing is off
- **THEN** ordinary logs contain only bounded model/count/calibration metadata
- **AND** contain no message text, tool arguments, or tool results.

### Requirement: Real Claude Code proves compact and post-compact recovery

The acceptance suite SHALL drive real headless `claude.exe` through a real Debug
bridge subprocess and a deterministic test upstream. The upstream SHALL inject
the exact confirmed context 400 into a small tool-bearing CC-to-Responses turn,
answer the client's compact-summary request, and drive the post-compact retry
through real tool execution. Acceptance SHALL rely on Claude Code's own compact
and tool-result evidence, not solely on bridge status, trace, request count,
process exit code, or final text.

#### Scenario: Injected context rejection triggers automatic compact

- **WHEN** the deterministic upstream returns the exact confirmed context 400 on
  the first qualifying tool-bearing `/responses` request
- **THEN** the bridge returns the scoped Anthropic prompt-too-long envelope
- **AND** the isolated Claude session transcript records a
  `system/compact_boundary` whose `compactMetadata.trigger` is `auto`
- **AND** the deterministic upstream records a compaction-summary request
  between the rejected request and the post-compact retry
- **AND** no near-million-token request is required to reach this state.

#### Scenario: Work resumes after the compact boundary

- **WHEN** Claude Code has recorded the automatic compact boundary
- **THEN** it retries after that boundary
- **AND** completes a post-boundary `tool_use` to `tool_result` round trip using
  Bash and Read
- **AND** reports the deterministic final canary
- **AND** no `bridge_tool_namespace` or `bridge_input_is_grammar_text` marker is
  visible in the Claude transcript or downstream response.

#### Scenario: The behavior test preserves client-owned evidence

- **WHEN** the compact-recovery ClientBehavior case runs
- **THEN** it uses an isolated `CLAUDE_CONFIG_DIR` and retains the relevant
  session transcript, unless equivalent compact-boundary evidence is proven to
  be emitted directly by that Claude Code version
- **AND** a missing compact boundary fails the semantic verdict even if the
  client exits zero and the final canary appears.

#### Scenario: Test upstream override cannot ship

- **WHEN** a Release or Native-AOT bridge is launched with
  `COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL` set
- **THEN** it cannot route authentication or model traffic to the supplied test
  upstream
- **AND** deterministic injection remains available only to the Debug test
  binary.
