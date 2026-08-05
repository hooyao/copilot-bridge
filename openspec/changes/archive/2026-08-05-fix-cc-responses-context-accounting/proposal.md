## Why

Claude Code sizes a `claude-opus-5` request in Anthropic Messages token space, but
the bridge may route that same request to `gpt-5.6-sol` and expand it into OpenAI
Responses token space. The captured failure counted as 856,113 Anthropic tokens
but approximately 922,733 Responses tokens; Copilot rejected the latter at an
observed admission boundary near 921,566 while the bridge's route-blind
`/cc/v1/messages/count_tokens` endpoint still reported that the source request
fit.

The rejection is then relayed with Copilot's generic Responses wording, which
Claude Code 2.1.221 does not classify as prompt-too-long and therefore does not
recover from by compacting the conversation.

## What Changes

- Make `/cc/v1/messages/count_tokens` resolve `Routing.Locations` through the
  same route-planning and target-profile rules as `/cc/v1/messages`, using only
  the model, headers, betas, effort, and content actually present on that count
  request. The endpoint does not infer fields from a previous or hypothetical
  main-loop request.
- Preserve the current byte-level Copilot Anthropic count passthrough when the
  resolved route remains Anthropic-native.
- When routing selects a Responses backend, transform the count request's actual
  input into canonical T2 Responses form with the shared T2 rules, submit those
  exact bytes to Copilot's existing `/v1/messages/count_tokens` endpoint, and
  convert its result into a conservative target-equivalent input-token estimate.
  The estimate includes a versioned, model-specific calibration and safety
  reserve because the Anthropic count endpoint under-counted the captured
  Responses body and Copilot exposes no native `/responses/count_tokens`
  endpoint.
- Keep the calibration grounded in captured real-client requests and live
  boundary probes. Do not use a live generation request to `/responses` as the
  production counting mechanism.
- On `/cc` requests resolved to a Responses backend only, translate Copilot's
  confirmed context-window 400 into a Claude-Code-recognized Anthropic
  prompt-too-long error so Claude Code can compact and retry if proactive
  accounting still misses. Preserve the raw upstream error in tracing.
- Keep native `/cc` Anthropic behavior and native `/codex` Responses behavior
  unchanged. The bridge does not truncate, drop, or summarize client history.
- Add contract, captured-byte, live-Copilot boundary, and real headless Claude
  Code coverage for route parity, conservative accounting, compact/retry, and
  successful post-compaction tool execution. The client recovery case uses a
  deterministic Debug-only upstream to inject the confirmed context 400 into a
  small request, then proves auto-compaction from Claude Code's own persisted
  `compact_boundary` event; it does not manufacture a one-million-token prompt.

## Capabilities

### New Capabilities

- `cc-responses-context-accounting`: Route-aware Claude Code token accounting,
  conservative Responses admission estimation, and Claude-native recovery from
  Responses context-window rejection.

### Modified Capabilities

None.

## Impact

- Affects the Claude Code count-tokens endpoint, routing/T2 reuse seams, the
  Copilot Responses buffered-error path, model-specific backend facts, request
  summaries, and trace assertions.
- Adds no public endpoint and no third-party runtime dependency. Any new JSON
  DTOs must remain source-generated and Native-AOT safe.
- Counting a cross-routed request performs one Copilot count request, not a
  Responses generation request. Native Anthropic count requests remain raw
  passthrough.
- The estimate is intentionally conservative, but it describes the exact count
  input rather than adding a hypothetical whole-turn system prompt or output
  reservation. Bounded-overcount tests protect auxiliary callers such as file
  and MCP-output validation from gross inflation.
