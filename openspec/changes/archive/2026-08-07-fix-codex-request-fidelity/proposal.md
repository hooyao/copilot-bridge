## Why

Production audit of 233 real Codex Desktop 0.147.0-alpha.1.2 turns to `gpt-5.6-sol` found that T1→IR→T2 silently changes request semantics: every request loses `reasoning.context: "all_turns"`, modeled message/tool-result metadata is discarded, developer messages are moved out of conversation order, and structured native tool results are flattened. Paired live replays proved Copilot accepts the unmodified complex Codex body and each affected field/shape, while Codex defines omission of reasoning context as a change from `all_turns` to the `current_turn` default.

## What Changes

- Preserve native Codex Responses request fields, input-item order, and JSON values through T1→IR→T2 whenever routing and destination policy authorize them unchanged, including future fields on already-modeled shapes.
- Carry source-owned Responses data into provider-scoped IR extensions and let the Responses destination pull it back out; neither edge will branch on the identity of the other edge.
- Preserve `reasoning.context`, in-place developer/system messages, message phase and valid identity metadata, function-output identity/status, reasoning state, and native `function_call_output.output` arrays.
- Restrict departures from native request fidelity to explicit destination-owned mutations grounded in live backend evidence, such as model/effort routing, rejected tool/field filters, and removal of a message id whose wire form Copilot rejects. Each mutation will be narrow and observable.
- Keep Claude Code→Responses translation behavior isolated: Claude-originated content continues to be translated from the Anthropic IR without receiving native-Codex carrier data.
- Replace the request corpus oracle that compares against an already-lossy captured upstream body with contract-driven inbound→outbound conservation checks, mutation checks, paired live captured-body probes, and real-client verification.

## Capabilities

### New Capabilities

- `codex-request-fidelity`: Defines lossless native Responses request carriage through the shared IR, authorized destination mutations, protocol isolation, observability, and contract/live-client acceptance.

### Modified Capabilities

- `codex-response-fidelity`: Removes native request transformations from the response-fidelity contract, delegates them to `codex-request-fidelity`, and keeps Claude→Responses request translation explicitly isolated from native Codex restoration.

## Impact

- Affected request path: `ResponsesRequest` DTO/converters, `ResponsesToIrInboundAdapter`, provider extensions on request/message content, `ResponsesRequestBuilder`, routing/profile coercions, and request audit summaries.
- Affected contracts/tests: Codex round-trip invariants, production-corpus replay, captured-byte ApiContract probes, and the real `Codex_XhighReasoningAndCustomExec_PreservesNativeResponse_ForVerdict` ClientBehavior case.
- Affected documentation: `docs/pipeline-design.md`, `docs/codex-implementation-design.md`, and `docs/codex-protocol-research.md`.
- No endpoint, configuration, dependency, or public client setup change. Native request wire bytes may gain fields previously lost and retain structured values previously flattened.
