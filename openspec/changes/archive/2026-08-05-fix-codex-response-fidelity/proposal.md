## Why

A two-day audit of 3,227 real Codex-to-gpt-5.6 turns found that the request path is semantically lossless, but the same-shape Responses return path is not: T3/T4 drops every reasoning item and summary, replaces response/item identities, removes message phases and cache-write usage, and discards all unmodeled event and terminal fields. One cancelled custom-tool stream also reached Codex without a terminal event, so an upstream HTTP 200 and complete tool deltas still do not prove that the downstream client received a complete turn.

## What Changes

- Preserve each successful or incomplete Copilot Responses SSE event, including unknown event and field extensions, across the native `/codex/responses` client edge when the response pipeline does not intentionally rewrite or abort it; retain the existing bounded security surface for `response.failed`.
- Keep the Anthropic-shaped semantic inspection stream used by response leak, runaway, model-rewrite, and tool-input-validation detectors, while associating it with the original Responses events so a clean Codex route restores the original wire rather than synthesizing a lossy approximation.
- Preserve reasoning summaries and encrypted reasoning items, message `phase`, response/item ids, complete usage details such as `cache_write_tokens`, delta metadata, terminal response metadata, and future unmodeled fields on the native Responses route.
- Define detector rewrites and aborts as the only authorized departures from original-event fidelity; bridge-internal transport markers must never cross either client edge.
- Ensure an upstream stream that ends or faults without a Responses terminal produces exactly one Codex-native `response.failed` terminal unless the downstream client has already cancelled the response.
- Retain the current translated behavior for Claude Code requests routed to a Responses backend; raw Responses events and bridge-private carrier data must not leak to `/cc`.
- Preserve tool-result content without injecting JSON wrappers: `text`, `input_text`, and `output_text` content blocks flatten to their text values, while genuinely non-text blocks retain their compact JSON representation.
- Preserve every present field of a modeled encrypted reasoning input item (`id`, `encrypted_content`, `summary`, and `content`) through T1/T2 so a detailed-reasoning tool result can be echoed on turn two without Copilot rejecting the missing summary.
- Add capture-backed contract tests, mutation checks, a corpus-level trace verifier, and a real headless Codex multi-tool behavior run whose verdict includes Codex's own dispatch log.

## Capabilities

### New Capabilities

- `codex-response-fidelity`: Defines lossless same-protocol Responses event carriage, authorized response-stage mutations, terminal integrity, cross-client isolation, and real-client acceptance.

### Modified Capabilities

None.

## Impact

- Affected response pipeline: `CopilotResponsesStrategy`, `ResponsesToAnthropicStream`, `ResponseInspectionStage`, `IrToResponsesOutboundAdapter`, and request-scoped response state.
- Affected tests: Codex stream round-trip/content-conservation tests, endpoint fault tests, capture-backed integration tests, and `Kind=ClientBehavior` verification.
- Affected documentation: `docs/pipeline-design.md` and the durable OpenSpec response-fidelity/upstream-timeout contracts.
- No endpoint, configuration, or dependency change. The response wire change retains fields previously discarded and gives faulted streams the required terminal; the request-side correction removes accidental JSON wrappers around Responses text blocks inside tool-result output.
