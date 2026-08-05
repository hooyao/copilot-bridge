## Context

The shared pipeline uses an Anthropic Messages-shaped IR. A native Codex request is Responses-shaped on both sides, but its response currently takes the path `Responses SSE → T3 Anthropic events → detectors → T4 Responses SSE`. T3 intentionally ignores event types without an Anthropic equivalent and T4 reconstructs a minimum Responses envelope from the modeled subset.

The production trace audit proves this reconstruction is lossy at scale: 1,517 responses lost 2,331 reasoning items and 124,380 reasoning-summary events; all 3,219 completed streams lost `cache_write_tokens` and terminal metadata; 835 message items lost `phase`; every response/item id was replaced. The content needed for ordinary text and tool dispatch happened to survive, but the same architecture will drop every future Responses extension by default.

The response detectors must remain on the shared semantic stream. Bypassing T3/T4 entirely for native Codex would preserve bytes but would also bypass leak, runaway, model rewrite, and tool-input validation, which is unacceptable.

## Goals / Non-Goals

**Goals:**

- Preserve every clean native Responses event and field without teaching the Anthropic IR every Responses extension.
- Keep all response detectors authoritative and preserve their current semantic inputs.
- Produce an honest Codex terminal on non-cancelled faults without synthesizing successful partial tool completion.
- Keep `/cc→Responses` translation and both native request paths isolated and unchanged.
- Stay Native AOT-safe and avoid reflection serialization or an unbounded second copy of the stream.

**Non-Goals:**

- Byte-identical HTTP chunking, whitespace, or JSON escaping.
- Exposing raw Responses reasoning to Claude Code.
- Changing request T1/T2 transformations, routing, model profiles, or detector policies.
- Preserving Copilot-only HTTP headers that the existing endpoint policy intentionally does not forward.

## Decisions

### 1. Carry original Responses events beside semantic IR events

T3 will emit a private constant carrier token for every parsed upstream Responses event. The request-scoped FIFO ledger entry paired with that token contains the original event name/value, its semantic IR expansion, and a monotonically increasing source ordinal. The carrier follows the semantic IR event(s) derived from that source event. Events that have no Anthropic semantic representation still receive a carrier, so reasoning and future unknown events are no longer erased without allocating a new payload string per source delta.

The carrier representation will use source-generated/AOT-safe DTOs or explicit `Utf8JsonWriter` and bounded strings. It is private pipeline data, not client protocol. ClaudeCodeOutboundAdapter will scrub it unconditionally.

Alternative considered: store all originals in a request-scoped side list and let T4 read it. Rejected because response stages can buffer, drop, rewrite, and terminate the stream; a side list is not transformed with the inspected stream and can accidentally restore content a detector suppressed.

Alternative considered: model every missing Responses field in the IR. Rejected because the Responses union is open-ended and this is the failure mode the change must remove, not perpetuate.

### 2. Treat the carrier as delivery authority only after semantic inspection

ResponseInspectionStage will recognize carrier events but not feed their opaque payload to content detectors. It will associate carriers with the semantic events generated from the same source ordinal. A source group is released only after all its semantic events receive `None`; a drop/rewrite/abort marks that source group as mutated and prevents blind restoration.

For a clean native Codex route, T4 consumes the approved carrier and emits the original Responses event. The proven model-only rewrite patches only model identity; every other mutated group fails closed through `response.failed`. This keeps detector decisions authoritative while making the common path lossless.

Grouping, rather than one carrier per IR event, is necessary because one Responses event can generate zero, one, or multiple IR events. A source-end marker (or an equivalent group boundary encoded in the carrier) makes release deterministic without buffering the entire response.

Alternative considered: let T4 prefer original events whenever a carrier exists. Rejected because it would reintroduce text after leak/runaway detectors dropped or aborted it.

### 3. Native Codex and Claude Code select different carrier consumers

The request path is the protocol discriminator. On `/codex`, IrToResponsesOutboundAdapter restores approved original events. On `/cc`, ClaudeCodeOutboundAdapter discards every private carrier and continues emitting the translated Anthropic events. T3 remains shared because it supplies semantic inspection for both client protocols.

The model rewrite detector may intentionally alter a semantic model field. The carrier approval layer must patch only that configured model identity into the original event rather than replace the whole original envelope, preserving unrelated fields.

### 4. Fault completion belongs to the Codex edge

T3 must never close an open semantic block as a successful tool/text completion merely because upstream ended. Premature EOF or an exception is propagated after the safe inspected prefix. T4 owns the downstream protocol and emits exactly one `response.failed` on a connected Codex response, then rethrows for endpoint accounting. A real upstream failed terminal remains a typed fault so its generated message cannot cross either client edge; its normalized bounded code is retained in the Codex failed envelope and endpoint accounting. Client cancellation remains distinct: once the request token is cancelled, no terminal write is promised.

This extends the existing stream-idle behavior to every incomplete upstream termination and fixes the audited case where a custom-tool `.done` was synthesized from a cancelled partial stream.

### 5. Tests compare full values, not selected fields

The primary fixture test will parse every original and downstream SSE event and compare ordered `(event name, JSON value)` pairs. It will include reasoning summaries, encrypted content, `phase`, non-empty cache-write usage, original ids, unknown event/field extensions, and tool deltas. Separate tests will mutation-check carrier loss, detector bypass, marker leakage, and duplicate/missing terminals.

The existing generated-shape tests remain useful for `/cc→Responses` and detector-mutated paths, but they no longer define fidelity for a clean native Codex response.

## Risks / Trade-offs

- **[Risk] Carrier data duplicates large event JSON while it crosses the semantic pipeline.** → The stream carries a constant token and the FIFO ledger keeps the original/semantic string references without copying them. Default streaming consumes each entry at its source-group boundary (at most one in flight). `BufferScannableBlocks` retains one withheld text/thinking block's entries, while explicit whole-response detector buffering retains the whole response because that mode already withholds the entire semantic stream.
- **[Risk] A detector action could be attributed to the wrong original event.** → Use explicit monotonically increasing source ordinals and source-end boundaries; test zero/one/many semantic events per source.
- **[Risk] Private carrier leaks to Claude Code or Codex JSON.** → Fail closed at both outbound adapters, add recursive property-name assertions, and mutation-check the scrub.
- **[Risk] Lossless restoration bypasses model rewrite.** → Represent configured rewrite as an explicit group mutation and patch only the model field on the original event.
- **[Risk] Client cancellation is mistaken for upstream failure.** → Check the downstream cancellation token before fault-terminal emission and preserve existing cancellation accounting.
- **[Trade-off] Clean Codex responses become semantically lossless but not byte-identical.** → JSON value/order fidelity is the contract; SSE framing and property order are not observable requirements for Codex.

## Migration Plan

1. Land the carrier and group-boundary types inertly with unit coverage.
2. Enable native Codex restoration while retaining the generated fallback for carrier-free fixtures and cross-client routes.
3. Run the full unit suite and Windows Native AOT publish.
4. Run capture replay and a real headless Codex multi-tool scenario, then inspect Codex's own log.
5. Roll back by reverting the change; no config or persisted-data migration is involved.

## Open Questions

None.
