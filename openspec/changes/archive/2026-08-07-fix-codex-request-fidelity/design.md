## Context

The native Codex path is Responses-shaped at both client and Copilot boundaries, but it currently pays a lossy round trip through the frozen Anthropic-shaped IR:

`Responses request → T1 → MessagesRequest IR → shared stages → T2 → Responses request`

The IR already has the intended escape hatch: provider-scoped opaque JSON at request and content-part levels. The frozen design says a source pushes its provider-native tail into that bag and a destination pulls only its own provider namespace. The as-built Codex adapter uses this for request knobs, unknown input item types, function namespace/grammar markers, opaque tool results, and reasoning fields, but coverage is incomplete: `MessageParam` has no provider extensions, known Responses DTOs discard unmodeled siblings, developer items lose their source position, top-level reasoning drops `context`, and T2 interprets native function output arrays as cross-protocol content.

The 2026-08-07 audit provides independent wire evidence:

- 233/233 production requests lost `reasoning.context: "all_turns"`; Codex source states that omission selects the `current_turn` default.
- Thousands of message `id`/`phase` values and function-output ids were discarded.
- Five developer messages per turn were moved into top-level instructions.
- Native structured function outputs were flattened.
- Paired real-capture probes showed Copilot accepts `reasoning.context`, valid message/tool-result metadata, in-place developer messages, and native output arrays. A complete current B3 request was accepted unchanged.
- A separate exact old capture was rejected only because an assistant message carried `id: "item_0"`; Copilot requires message ids to begin with `msg`. This is a necessary narrow destination coercion, not permission to discard every message id.
- The response path is already value-faithful and must remain unchanged.

Constraints are Native AOT/source-generated JSON, the frozen MessagesRequest IR, no source↔destination coupling, no regression on Claude Code→Responses translation, and real-client acceptance from Codex's own dispatch evidence.

## Goals / Non-Goals

**Goals:**

- Make a clean native Codex→Responses request JSON-value faithful, including future fields on known item types.
- Implement fidelity by extending provider-scoped IR carriage at the levels where data originates, not by bypassing the shared IR or passing a source object directly to a destination.
- Preserve shared-stage authority: a destination must not restore source data that would undo a routing, sanitization, or profile decision.
- Make every necessary destination mutation narrow, live-grounded, observable, and contract-tested.
- Preserve existing Claude Code→Responses translation and response-event fidelity.
- Keep the hot path AOT-safe and bounded for multi-megabyte Codex requests.

**Non-Goals:**

- Byte-identical JSON formatting, object-property order, HTTP chunking, or inbound client headers.
- Removing T1/T2 or replacing the Anthropic-shaped IR with a second canonical model.
- Letting the Codex source inspect a selected target, or letting the Responses destination inspect client/source identity.
- Changing routing configuration, model ids, response detectors, or the established response `custom_tool_call` id correction.
- Preserving wire values that a paired current Copilot probe proves invalid; those remain explicit destination coercions.

## Decisions

### 1. Extend the existing multi-level provider-extension architecture

The fix will use `ProviderExtensions["openai"]` as the only native carriage channel. `MessageParam` will gain the same optional `ProviderExtensions` property already present on `MessagesRequest` and `ContentBlockParam`. Known Responses DTOs/converters will retain unmodeled siblings so T1 can push them into the appropriate IR level.

Carriage levels:

- request bag: unknown envelope fields, complete top-level reasoning extras, ordered source-item records for developer/system and other zero-semantic/multi-semantic mappings;
- message bag: Responses message item metadata and unknown siblings;
- content-part bag: raw content-part extras, function-call extras, function-output extras plus its opaque native output marker, and complete reasoning-item extras.

The inner JSON remains provider-owned and opaque to core stages. Only the Responses source writes the OpenAI namespace, and only the Responses destination reads it. Those statements describe protocol ownership, not endpoint identity: neither side receives or checks `ClientProtocol`, route name, adapter type, or source/destination flags.

Alternative considered: store the inbound raw body on `BridgeContext` and let T2 patch it. Rejected because it creates a direct source→destination side channel outside the IR, makes source identity observable at the destination, and can restore content behind shared-stage decisions.

Alternative considered: add only the four fields observed in this capture to typed DTOs. Rejected because modeled Responses unions are open-ended; the next sibling field would recreate the same silent loss.

### 2. Represent source positions without removing semantic visibility

Developer/system input messages must remain visible to shared semantic stages and retain their original Responses positions. T1 will therefore do both, within the IR:

1. project their input-text content into `System` blocks for semantic inspection;
2. attach a provider record that identifies the originating Responses input item, original position, raw provider fields, and the system blocks that represent it.

T2 will pull those records, emit the source item at its original input position, and omit the corresponding source-derived system blocks from top-level `instructions`. System blocks that originated from top-level `instructions`, Claude Code, configuration, or a shared-stage insertion remain ordinary semantic system blocks and continue to build `instructions`.

If a shared stage changes a source-derived system block, T2 will patch that block's text into the original provider item while retaining its unrelated fields and position. A missing, duplicated, or structurally incompatible source record fails closed to the generated semantic representation and records a fidelity downgrade; it never blindly restores stale raw content.

The ordered record mechanism will subsume the current `passthrough_items` special case so known, unknown, and developer items use one stable sequence model.

Alternative considered: allow `developer` as a third `MessageParam.Role`. Rejected because the frozen IR and existing stages define `user|assistant`; broadening the semantic role union would leak provider syntax into the core.

### 3. Merge provider extras into destination-generated semantic items

For ordinary user/assistant messages and typed content, T2 remains responsible for the destination shape. It writes semantic fields from the current IR, then pulls provider extras and writes only non-conflicting siblings. This preserves shared-stage edits while retaining source metadata and future fields.

Conflict policy is destination-owned:

- semantic keys (`type`, `role`, `content`, call linkage, tool name/arguments, encrypted reasoning payload) come from the current IR;
- provider extras restore `id`, `phase`, `status`, annotations/detail, and future siblings when valid;
- a provider extra cannot overwrite a semantic key or a field explicitly mutated by routing/profile policy;
- duplicate/conflicting bag keys fail closed and are reported rather than silently winning by property order.

Message ids receive a Responses destination validation step. A valid `msg*` identity is restored. A nonempty identity known from the live probe to be rejected, such as `item_0`, is omitted while every other message field is retained. This policy belongs in the Responses builder/profile layer, not the Codex adapter.

Alternative considered: re-emit the entire raw item when present. Rejected because it would undo semantic edits and make carrier restoration authoritative over the pipeline.

### 4. Native function outputs remain opaque JSON values

T1 already marks Codex `function_call_output.output` as provider-native opaque content. T2 will honor that statement by writing the held `JsonElement` directly, preserving string/object/array kind and value. It will no longer run native opaque output through the Claude-oriented text/image flattening function.

Claude Code never sets the opaque native marker. Its semantic `tool_result` blocks continue through the existing target-capability logic: structured text/image output where live-proven, otherwise the established fallback. The destination pulls what the IR says; it does not ask which source produced it.

### 5. Top-level reasoning and unknown envelope fields ride the request bag

`reasoning.effort` remains semantic in `OutputConfig.Effort`; every other present reasoning property, including `summary`, `context`, and future siblings, rides the request-level OpenAI bag. T2 writes the coerced semantic effort and then merges non-conflicting reasoning extras.

Unknown top-level request fields also ride an explicit extension object. T2 re-emits them unless their names collide with a semantic/destination-controlled field. This makes a future Codex request additive by default instead of silently lossy.

The current target profile/routing order remains:

1. source pushes full provider state into IR;
2. shared routing/stages mutate semantic IR;
3. Responses destination pulls semantic plus provider state;
4. destination applies and records its live-grounded wire coercions last.

This order prevents the bag from resurrecting `service_tier`, rejected tool variants, an invalid message id, or a pre-route model/effort.

### 6. Record exact request mutations

The Responses builder will return a bounded mutation summary alongside body bytes, vision, and effective effort. Each entry identifies a stable mutation code and field class, for example:

- `route.model`
- `route.effort`
- `profile.effort`
- `profile.service_tier`
- `profile.tool.image_generation`
- `protocol.message_id`

The request summary log and trace metadata will expose the codes without logging prompt values or encrypted payloads. No mutation entries are allocated when tracing/diagnostic collection is disabled beyond the existing summary state. An unexpected inbound→outbound difference has no runtime auto-allow path; it is a contract-test failure.

### 7. Replace the lossy corpus oracle with an independent contract diff

The existing corpus replay verifies that current code reproduces a captured `upstream-req`; that artifact already lost `reasoning.context`, so the test freezes the bug. The request gate will instead:

- start from `inbound-req.body`;
- run the actual deserializer and T1→IR→T2 path;
- compare full JSON values and array order;
- classify only explicit route/profile/protocol mutations through a reviewed allowlist;
- fail on every other difference and print its first path plus item context.

Tests will state the contract before implementation and be mutation-checked by disabling each new push/pull leg. The 233-turn corpus will cover real shapes; small committed cases will isolate reasoning context, developer positioning, valid/invalid message ids, structured tool output, and unknown siblings on known item types.

Live ApiContract coverage will retain paired captured-body probes: unmodified accepted value versus the claimed rejected mutation, changing one axis only. Because a rewrite can become stale silently, each retained rewrite must join the contract sweep/catalog-versus-live check.

The real B3 ClientBehavior case remains the final gate and must prove namespaced function plus custom-exec round trips from the exact trace and zero Codex router fatal/error rows.

### 8. Preserve Native AOT and bound memory

All new wire data will be `JsonElement`, explicit dictionaries/records, source-generated DTO properties, or `Utf8JsonWriter` output. No reflection serializer or runtime type construction is introduced.

T1 already owns the parsed request and numerous encrypted reasoning values; extensions will clone only provider fragments that must outlive their source `JsonDocument`. Ordered source records reference one raw `JsonElement` per input item rather than duplicating whole request JSON. T2 streams those values into one existing request buffer. The implementation will add allocation/large-corpus checks and inspect AOT binary size.

## Risks / Trade-offs

- **[Risk] A carrier restores content after a shared-stage mutation.** → Merge semantic fields from current IR, use per-item provenance, apply destination coercions last, and mutation-test sanitization/routing cases.
- **[Risk] Known item extension data conflicts with typed fields.** → Reject/ignore conflicting provider keys under a deterministic destination policy and record a downgrade; never emit duplicate JSON property names.
- **[Risk] Developer-item provenance becomes inconsistent after message/system edits.** → Use stable ordered records plus original semantic fingerprints; patch compatible edits and fail closed to generated semantics otherwise.
- **[Risk] Provider extensions leak to Claude or wire JSON as `bridge_*`.** → Keep them under internal DTO properties consumed before serialization and add recursive no-marker assertions on both client edges.
- **[Risk] Preserving newly accepted fields exposes a backend drift on older models.** → Exact per-model live probes decide destination coercions; no family-name extrapolation or nearest-profile borrowing.
- **[Risk] Request memory grows for million-token contexts.** → Store granular raw fragments once, avoid a duplicate whole-body tree, run the long-context corpus, and measure allocations.
- **[Trade-off] JSON is value-faithful rather than byte-identical.** → This permits AOT-safe reserialization and semantic-stage patches while preserving every observable Responses value and array order.

## Migration Plan

1. Update `docs/pipeline-design.md` first to define request-side provider provenance and destination mutation order.
2. Add IR/message extension plumbing and inert source records with unit tests.
3. Populate T1 provider data and prove it survives the pipeline before enabling restoration.
4. Enable T2 merge/restoration and explicit destination coercions one field class at a time.
5. Replace the request corpus oracle, run mutation checks, and add permanent captured-byte ApiContract probes.
6. Run the full unit suite, integration suite, Windows Native AOT publish/size check, and the real B3 ClientBehavior verdict.
7. Roll back by reverting the change; there is no persisted format or configuration migration.

## Open Questions

None. Live probes resolved the two initially ambiguous wire facts: Copilot accepts native structured function-output arrays, and it rejects non-`msg` message identities such as `item_0` while accepting valid `msg*` identities and message phase metadata.
