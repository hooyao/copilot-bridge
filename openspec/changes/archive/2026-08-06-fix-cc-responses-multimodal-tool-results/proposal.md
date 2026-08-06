## Why

Claude Code can return images inside an Anthropic `tool_result`, but the CC→Responses translator currently flattens that content into JSON/base64 text. A live `gpt-5.6-sol` probe accepted the official structured `function_call_output.output` content-item array and correctly identified a generated image, so the current behavior destroys a modality the target can consume.

## What Changes

- Translate ordered Anthropic text/image tool-result blocks into ordered Responses `input_text`/`input_image` function-output content items for model profiles proven to support that wire shape.
- Preserve image media type, source bytes or URL, tool-call identity, and text order, and set the request's vision signal so the upstream request carries `Copilot-Vision-Request: true`.
- Keep the existing string/verbatim output behavior for scalar tool results, native Codex round trips, unsupported block variants, and model profiles that reject structured multimodal function output.
- Keep the decision inside the IR: a source that needs its tool output left uninterpreted marks that on the block (Codex T1 sets `opaque_tool_output`), and the request translator reads that mark instead of taking a client-identity parameter.
- Ground every positive model capability in direct live probes rather than extrapolating from model family names.
- Add contract-derived unit coverage, mutation proof, captured Claude Code byte replay, content-conservation checks, and a real Claude Code CC→gpt image-tool behavior run judged from the client transcript and exact trace.

## Capabilities

### New Capabilities

- `cc-responses-multimodal-tool-results`: Defines modality-preserving translation of Claude Code tool-result text/images to a proven-capable Responses backend, capability-safe fallback, vision signaling, and real-client acceptance.

### Modified Capabilities

- `codex-response-fidelity`: Clarifies that the existing flattened-output contract remains authoritative for native Codex round trips and content that cannot be represented as supported Responses function-output content items; it does not require flattening a recognized Claude Code image for a proven-capable target.

## Impact

- Request translation and model profiles: `ResponsesRequestBuilder`, `CodexModelProfile`, and `CodexModelProfileCatalog`.
- Upstream request headers: the existing `Vision` return value and `Copilot-Vision-Request` path become active for images nested in tool results.
- Tests: Codex image invariants, content conservation, captured-byte ApiContract coverage, live per-model probes, and Claude Code ClientBehavior coverage.
- Durable protocol contract: a new CC→Responses multimodal capability plus a scoped clarification to `codex-response-fidelity`.
- No endpoint, configuration, dependency, or persisted-data change; native Anthropic routing remains unchanged.
