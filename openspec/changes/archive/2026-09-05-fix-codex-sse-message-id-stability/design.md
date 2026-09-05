## Context

The native `/codex/responses` path translates each Copilot Responses event into the shared Anthropic-shaped semantic stream for response inspection, then uses `NativeResponsesEventLedger` to restore the original event when inspection leaves its semantics unchanged. The only current identity correction is for `custom_tool_call` ids.

A real Codex 0.153.3 + bridge 0.5.14 trace demonstrated that one logical message used different opaque ids in `response.output_item.added`, successive `response.output_text.delta` events, `response.output_text.done`, `response.content_part.done`, `response.output_item.done`, and `response.completed.output`. Codex streams the started item under the added id, then completes the item under the done id. The Desktop reducer replaces an existing item only when ids match, so it retains both the delta-built text and the identical completed snapshot. Codex persists only the completed response item, which is why the next request context is not duplicated.

The bridge must adapt this Copilot wire quirk at the Codex client boundary without weakening semantic inspection or changing identities that are replay-significant for reasoning and tool calls.

## Goals / Non-Goals

**Goals:**

- Give every streaming native Codex `message` output item one stable client-facing id across its complete SSE lifecycle.
- Preserve event count, order, timing, text, phase, annotations, usage, unknown siblings, terminal metadata, and all non-message item identities.
- Preserve a valid canonical `msg_...` id for official replay, while ensuring an opaque/non-`msg` canonical or synthesized fallback remains subject to the existing invalid-message-id correction.
- Prove the fix from a captured rolling-id stream and from a real current Codex tool trajectory using client-owned evidence.

**Non-Goals:**

- Normalizing reasoning, function/custom tool, web-search, image-generation, or unknown-item ids.
- Changing the existing `custom_tool_call` `ctc` prefix/echo correction.
- Disabling native event restoration, reconstructing the whole Responses stream from the IR, or changing `/cc` behavior.
- Suppressing or deduplicating model text after generation.

## Decisions

### 1. Normalize only after semantic inspection authorizes native restoration

The correction belongs in the native T4 restoration function that currently applies the custom-tool id correction. T3 and every response detector continue to see the same semantic events. A dropped, rewritten, or aborted semantic group still fails closed and cannot be resurrected by identity normalization.

Alternative: normalize before T3. Rejected because the raw upstream audit would stop representing Copilot truth and the mutation would become entangled with detector semantics.

### 2. Establish message identity from `output_item.added` and key it by `output_index`

When an authorized `response.output_item.added` contains `item.type == "message"`, T4 records its non-empty `item.id` as the canonical identity for that output index. Every later lifecycle reference is rewritten to that value. An official stream that already uses one stable `msg_...` id is therefore not mutated at all. If the added item has no usable id, T4 derives a deterministic `item_` fallback from the response domain and output index using SHA-256.

For that mapped output index, T4 rewrites:

- `item.id` on `response.output_item.added` and `response.output_item.done`;
- top-level `item_id` on content-part, output-text, refusal, annotation, and future message-scoped events;
- the corresponding message item's `id` in `response.completed` or `response.incomplete` output arrays.

The mapping is activated only by an added item explicitly typed as `message`; an unrelated event with the same-shaped fields is not changed. Multiple commentary/final messages in one response remain distinct because each has its own output index. Per-request state is discarded with the adapter.

Alternative: always synthesize a non-`msg` bridge id. Rejected because it would unnecessarily mutate a conforming official stream and discard a legitimate replayable `msg_...` identity.

Alternative: use the final done id. Rejected because it is unavailable while deltas are being streamed and would require buffering visible output.

### 3. Keep the normalized id out of the next Copilot request

`ResponsesRequestBuilder` already removes assistant message ids that do not begin with `msg`. Therefore a canonical opaque Copilot id (or the synthesized `item_...` fallback) remains client-only and is absent upstream, while a legitimate canonical `msg_...` id continues to replay unchanged. Second-turn contract tests cover both cases while proving phase, status, content, and future siblings survive.

No cross-request lookup table is introduced. The bridge remains stateless and prompt caching sees the same upstream message-id behavior as before.

### 4. Preserve non-id JSON values and declare the correction in fidelity tests

The existing native fidelity corpus compares every event JSON value and currently recognizes only custom-tool identity correction. Tests will normalize only the declared message-id paths when comparing expected upstream and downstream values, then independently assert that every lifecycle reference for one message is identical. This prevents the compatibility rewrite from becoming a general license to mutate events.

A de-identified fixture derived from the real failing stream will contain different ids at every message lifecycle event. The new test must fail against the pre-fix implementation before the product change is applied.

### 5. Verify the real client on a message-only terminal as well as a tool loop

The real-client case uses Codex 0.153.3 or later with reasoning disabled for the target turn, so a remaining `item completed without a recorded start timestamp` warning cannot be attributed to a separate reasoning item. It performs multiple real tool calls, consumes their outputs, and ends with a visible canary message. PASS requires matching call/output pairs in the per-run bridge trace, the canary exactly once, no execution abort/router fatal, and stable ids across the final message lifecycle.

## Risks / Trade-offs

- **[Risk] A message-scoped event type is added later and retains Copilot's rolling id.** → Rewrite any top-level `item_id` only after its `output_index` is registered as a message, while keeping explicit tests for all currently known event families.
- **[Risk] A synthesized fallback id collides across messages.** → Hash the response domain and output index, retain the full SHA-256 value, and scope state per request.
- **[Risk] An opaque canonical id leaks upstream and changes replay or cache bytes.** → Assert the existing request-side correction removes non-`msg` ids, while separately preserving legitimate `msg_...` ids.
- **[Risk] Fidelity tests hide unrelated mutations by over-normalizing.** → Permit differences only at enumerated message-id JSON paths and assert all other values and event positions exactly.
- **[Risk] Codex changes its client behavior again.** → Keep the wire contract independently correct: one logical item has one id. Pin a current real-client behavior case in addition to reducer-specific evidence.
- **[Trade-off] Downstream event JSON is no longer byte/value-identical to raw Copilot at message-id paths.** → Raw `upstream-resp` remains untouched, `inbound-resp` records the corrected truth, and the spec names the exception explicitly.

## Migration Plan

1. Update the architectural fidelity contract before product code.
2. Add the captured contract test and confirm it fails on rolling ids.
3. Implement the request-scoped message-id mapping in native T4 restoration.
4. Run focused unit/corpus tests, the solution non-integration suite, and Native AOT build checks appropriate to the touched code.
5. Run the real current Codex case through a non-8765 bridge subprocess and inspect its trace, stdout/stderr, and `logs_2.sqlite` window.

Rollback is a single code-path reversal to raw message-id restoration. It would restore the duplicate-rendering defect but does not require data or configuration migration.

## Open Questions

None. Reasoning-item normalization remains a separate future investigation because its opaque replay contract differs from visible messages.
