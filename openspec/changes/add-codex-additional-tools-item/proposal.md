## Why

The 2026-07 Codex CLI (gpt-5.6 family — verified live on `gpt-5.6-sol`) started
emitting a new `input[]` item the bridge has never seen:
`{ "type": "additional_tools", "role": "developer", "tools": [ exec / wait /
request_user_input / collaboration-namespace ] }` — a harness tool-registration
preamble, always at `input[0]`.

The inbound `ResponsesInputItem` union only models `message` / `function_call` /
`function_call_output` / `reasoning`. On the new discriminator STJ throws
`Polymorphism_UnrecognizedTypeDiscriminator` and the request **400s at T1
deserialization, before Copilot is contacted** (captures
`request-traces/20260710-145459-0001`/`-0002`).

This is a bridge modeling gap, not a Copilot rejection: a live probe replaying the
exact captured item (reserved `collaboration` schema intact) returns **200**
(`ResponsesProbe.AdditionalToolsVerbatim`). So the fix is **faithful carriage
(passthrough), not translation** — model the item, carry it through the IR
verbatim, re-emit byte-faithfully. Purely additive (gpt-5.5-era Codex never sent it).

## What Changes

- **Model the `additional_tools` input item** as a fifth `ResponsesInputItem`
  variant (`ResponsesAdditionalToolsItem`) with `role` (string) and `tools` (an
  opaque `JsonElement` — the bridge never reads a tool's internals, same rationale
  as the request-level `Tools` field being opaque). Register it on the polymorphic
  union and in `JsonContext`.
- **T1 carriage**: `ResponsesToIrInboundAdapter` folds the `additional_tools`
  item into the request-level `ProviderExtensions["openai"]` bag verbatim — NOT
  into the messages array (it is not conversation content; it is a Codex harness
  tool-registration preamble that Copilot re-ingests as-is). The item's original
  `input[0]` position and full byte content are preserved in the bag.
- **T2 re-emit**: `ResponsesRequestBuilder` writes the carried `additional_tools`
  item back into the outbound `input[]` at the position the bag records
  (first, matching every observed capture), so the wire bytes Copilot receives
  match what Codex sent. No coercion, no tool drops applied to it (it round-trips
  200 as-is; the `image_generation`/`custom` drops that apply to the request-level
  `tools[]` are a separate, already-implemented path).
- **Grounding artifacts**: the live probe file
  `tests/CopilotBridge.Playground/ResponsesProbe.AdditionalTools.cs` (isolates
  input-item vs top-level-tools acceptance) and
  `ResponsesProbe.AdditionalToolsVerbatim.cs` (replays the real capture), plus the
  committed fixture `Fixtures/codex-additional-tools-verbatim.json`.

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: the "Fully-typed Responses DTOs" requirement is
  extended to enumerate `additional_tools` as a modeled input-item discriminator
  whose `tools` payload is carried opaque and round-trips byte-faithfully through
  T1→IR→T2. No other requirement in that capability changes.

### New Capabilities
<!-- None. This is an additive fidelity fix to an existing capability. -->

## Impact

- **Modified production code**:
  - `Models/Responses/ResponsesInput.cs` — add the `additional_tools` derived type + discriminator.
  - `Models/JsonContext.cs` — register the new DTO.
  - `Pipeline/Adapters/Codex/ResponsesToIrInboundAdapter.cs` — carry the item into the openai bag (T1).
  - `Pipeline/Strategies/Codex/ResponsesRequestBuilder.cs` — re-emit it into `input[]` (T2).
- **Tests**: an IR round-trip invariant (T1→T2 preserves the `additional_tools`
  bytes and position, from the committed capture) in `CopilotBridge.UnitTests`;
  the Playground probes above are `[Trait("Category","Integration")]`.
- **AOT invariants binding**: source-gen JSON only (the opaque `tools` is a
  `JsonElement`, AOT-clean); no reflection.
- **Hot path byte-identical**: `/cc` (Claude Code) never carries this item — the
  new bag key is Codex-only; the `WhenWritingNull`/absent-key rules keep `/cc`
  serialization unchanged (H1).
- **No breaking changes** — additive; unblocks gpt-5.6 Codex end-to-end.
