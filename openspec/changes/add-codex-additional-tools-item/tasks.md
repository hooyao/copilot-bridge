# Tasks — add `additional_tools` input-item carriage

## 1. Model the item
- [x] 1.1 Add `ResponsesAdditionalToolsItem : ResponsesInputItem` in
  `Models/Responses/ResponsesInput.cs` with `Role` (string) and `Tools`
  (`JsonElement`, opaque). Register `[JsonDerivedType(typeof(...), "additional_tools")]`
  on the `ResponsesInputItem` union.
- [x] 1.2 `Models/JsonContext.cs` — no change needed: derived types are
  auto-discovered from `[JsonDerivedType]` on the already-registered
  `ResponsesInputItem` base (confirmed by the deserialization test + build).
- [x] 1.3 Unit: the exact failing capture shape now deserializes to a
  `ResponsesRequest` with no `JsonException`
  (`CodexAdditionalToolsRoundTripTests.AdditionalToolsItem_Deserializes_NoPolymorphismThrow`,
  contract 3). Mutation-checked: removing the derived type → red.

## 2. T1 — carry into the openai bag
- [x] 2.1 In `ResponsesToIrInboundAdapter.AdaptAsync`, collect every
  `ResponsesAdditionalToolsItem` from `input[]` and add an `additional_tools`
  array to the `openai` bag in `BuildOpenAiBag` (each element: `{role, tools}`,
  written verbatim via `Utf8JsonWriter`, AOT-clean). Not added to `messages`.
- [x] 2.2 `BuildOpenAiBag`'s `hasAny` guard now includes `additionalTools.Count > 0`.

## 3. T2 — re-emit into input[]
- [x] 3.1 `ResponsesRequestBuilder.Build` writes `WriteAdditionalToolsItems` FIRST
  in `input[]` (before message-derived items), verbatim.
- [x] 3.2 `WriteBagFields` skips the `additional_tools` key (re-emitted into
  `input[]`, not as a top-level field), mirroring `reasoning_summary`.

## 4. Contract tests (CI-safe)
- [x] 4.1 Round-trip fidelity: T1→T2 preserves the `additional_tools` `tools`
  bytes and emits the item before conversation messages
  (`AdditionalToolsItem_RoundTrips_ByteFaithful_AheadOfMessages` +
  `AdditionalToolsItem_NotFoldedIntoSystemOrMessages`, contract 1/1b).
  Mutation-checked: disabling T2 re-emit → both red.
- [x] 4.2 `/cc` hot-path byte-equality: already fully guarded by the existing
  `HotPathByteEqualityTests` (H1a/H1b/H1c). This change touches only the Codex
  T1/T2 (Responses adapters), never the `/cc` Anthropic path or `MessagesRequest`
  serialization, and a `/cc` request never carries `additional_tools` → no bag key.
  No new test needed; full suite (825) stays green.

## 5. Live grounding (integration, already exercised)
- [x] 5.1 `ResponsesProbe.AdditionalTools*` probes + committed
  `Fixtures/codex-additional-tools-verbatim.json`; verbatim capture → 200 OK
  (cache_write 4116 tokens).
- [x] 5.2 End-to-end: real `codex.exe` (gpt-5.6-sol) drove a multi-step tool task
  through the bridge and completed clean — 4× `/codex/responses` all `200`
  (the `additional_tools` preamble no longer 400s), tool round-trips succeeded,
  `exit=0`, canary in stdout. Also proven at the HTTP edge by
  `CodexAdditionalToolsHeadlessTests` (verbatim capture → 200 + `response.completed`).
  Reusable harness added: `CodexLoadTaskSmokeTests` (model via `CODEX_SMOKE_MODEL`),
  and the copilot-model-sync skill now mandates this load-task smoke per Codex id.

## 6. Docs
- [x] 6.1 Folded into `docs/ir-definition-design.md` §3: the openai bag now carries
  `additional_tools`, and the T1 mapping table records it (T2 re-emits into
  `input[]` ahead of messages).
