## Why

GitHub Copilot's `/responses` stream emits a different opaque item id on successive lifecycle events for one logical assistant message. Codex 0.153.x treats that id as the stable UI item key, so the streamed `item/started` text and the full `item/completed` snapshot are retained as two identical visible messages even though the bridge did not duplicate any text delta and the next request context contains only one completed item.

## What Changes

- Normalize the client-facing id of each streaming `message` output item on the native `/codex/responses` route so `response.output_item.added`, all content/text delta and done events, `response.output_item.done`, and the terminal response output refer to one stable id.
- Treat the id on `response.output_item.added` as the canonical client identity and rewrite later lifecycle copies to it. A conforming stable `msg_...` stream therefore remains value-identical; only a missing added id uses a deterministic non-`msg` fallback that the existing request-side correction removes if echoed.
- Preserve the upstream event count, order, content, phase, usage, unknown siblings, and every non-message item identity. The existing `custom_tool_call` `ctc` correction remains unchanged; reasoning, function/tool, web-search, image, and unknown item ids are out of scope.
- Amend native response fidelity to authorize this narrow protocol-corrective mutation instead of restoring Copilot's rolling message ids byte-for-byte.
- Add contract tests from the captured failing stream, mutation-check the stable-id invariant, and verify a real current Codex client through a bridge subprocess using the client's own dispatch evidence.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-response-fidelity`: Allow and require a stable client-facing id across every SSE lifecycle event for one native Codex message item while retaining full value fidelity for unrelated fields and item types.

## Impact

- Affected code: native Responses event restoration in `IrToResponsesOutboundAdapter`, with request-side message-id stripping retained as the upstream isolation boundary.
- Affected tests: native response fidelity/corpus replay expectations, new captured message-id stability regressions, and real-client behavior verification.
- Affected documentation: `docs/pipeline-design.md` response-fidelity contract and the main `codex-response-fidelity` specification after archival/sync.
- No new dependency, configuration key, endpoint, or change to `/cc`; only client-facing message-id values on streaming native `/codex` responses change.
