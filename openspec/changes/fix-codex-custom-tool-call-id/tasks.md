# Tasks — round-trip the custom_tool_call `ctc_` id through the IR

## 1. Ground the contract
- [x] 1.1 Confirm the failure from the real trace: a follow-up turn echoes a prior
  `exec` call as `custom_tool_call` with `id`=`item_1` → Copilot `/responses`
  400 `Invalid 'input[4].id': 'item_1'. Expected an ID that begins with 'ctc'`
  (`ctc/20260714-034441-0001`, `call_EVynmIrkWbMh10Q1hmdULJG4`).
- [x] 1.2 Find the real upstream id on the first-turn response
  (`request-traces/20260714-022622-0499`): `ctc_0679bd5b187491ee…`, carried ONLY on
  the `custom_tool_call_input.delta`/`.done` events' `item_id` (the
  output_item.added/.done ids are rolling encrypted blobs). Copilot checks the
  `ctc` prefix, not the exact value (its own output id is a fresh `ctco_<uuid>`).
- [x] 1.3 Confirm determinism: 6 `ctc`-prefix 400s across 869 morning responses,
  first at `022629-0500` — the request right after the exec was generated.

## 2. Fix
- [x] 2.1 T3 stream `ResponsesToAnthropicStream`: capture the `ctc_` id off the
  `custom_tool_call_input.delta`/`.done` `item_id` (gated on the `ctc` prefix) and
  emit it on `content_block_stop` as `bridge_custom_tool_call_id`. Reset per block.
- [x] 2.2 T3 buffered `BufferedResponsesToAnthropic`: carry the upstream item `id`
  onto the tool_use block as the same marker, only when it begins with `ctc`.
- [x] 2.3 T4 `IrToResponsesOutboundAdapter`: `custom_tool_call` added event uses a
  deterministic `ctc_<call-id-suffix>` synthesis; the completed item uses the real
  captured id from the marker (fallback: the synthesis). Never `item_N`. Buffered
  `BufferedAnthropicToResponses` mirrors it.
- [x] 2.4 `ClaudeCodeOutboundAdapter`: scrub `bridge_custom_tool_call_id` from the
  `content_block_stop` event (streaming) and the tool_use block (buffered) on the
  CC→gpt route so it never reaches `claude.exe`.

## 3. Tests (from contract, mutation-checked)
- [x] 3.1 `CodexCustomToolCallIdRoundTripTests`: real `ctc_` id survives T3→T4 to
  the completed item; the completed id ALWAYS begins with `ctc`; the no-ctc
  fallback still synthesizes a `ctc_` id (never `item_N`); a non-`ctc` `item_id`
  is NOT echoed (the exact production failure mode); the marker never leaks to the
  Codex-facing wire; the CC→gpt scrub drops the marker but keeps the event;
  a marker-free `content_block_stop` is a byte-identical passthrough.
- [x] 3.2 `ClaudeCodeBufferedResponsesAdapterTests`: buffered real-`ctc_` id
  survives to the item; buffered no-ctc synthesizes `ctc_`; buffered marker
  scrubbed at the Claude edge.
- [x] 3.3 Request side (T1→T2, the exact failing follow-up path):
  `EchoedCustomToolCall_WithCtcId_RoundTripsTheIdVerbatim` — a Codex echo of a
  `custom_tool_call` WITH its `ctc_` id survives the round trip verbatim to the
  upstream wire (mutation-checked: stripping the id from the passthrough reddens it).
- [x] 3.4 Production-adapter regression (`CodexEndpointTests`): the buffered
  byte-preserving shortcut is diverted for a non-`ctc` `custom_tool_call` id
  (rewritten to `ctc`) and preserved for a `ctc` one — driving the real
  `IrToResponsesOutboundAdapter` via the endpoint.
- [x] 3.5 Consumption-boundary hardening (`CodexCustomToolCallIdRoundTripTests`,
  `ClaudeCodeBufferedResponsesAdapterTests`): a non-`ctc` marker at T4 is rejected
  (falls back to the synthesized `ctc_` id), streaming and buffered.
- [x] 3.6 Mutation-verified: reverting T4 to `item_N` reddens the id tests;
  disabling the scrub reddens the scrub test; dropping the T3 marker reddens the
  real-id round-trip; dropping the byte-shortcut divert reddens the endpoint test;
  removing the T4 prefix guard reddens the poisoned-marker test.

## 4. Real-client verification (the mandate)
- [x] 4.1 Real `codex.exe` 0.144.3 multi-turn exec task (`gpt-5.6-sol`,
  `Codex_CodeComputation_DrivesCustomExecPath`) through the fixed bridge subprocess:
  bridge→codex wire shows `output_item.added` id `ctc_<call-id>` (synth) and
  `output_item.done` id `ctc_<real upstream>` (captured); both turns 200; no
  `ctc`-prefix 400 anywhere in the trace; codex `logs_2.sqlite` clean (0
  router/dispatch fatals); exec ran (exit 0, canary in stdout, `turn.completed`).
  The `bridge_custom_tool_call_id` marker did not leak to codex.
- [x] 4.2 Real `codex.exe` multi-step shell task (function_call path) → 4 rounds
  all 200, no regression on the plain-tool path.
- [ ] 4.3 (Needs the user) A real DESKTOP-Codex session that echoes a
  `custom_tool_call` back WITH the `item_1`-style id — `codex exec` 0.144.3
  happened to echo the call back with no id, so it did not itself reproduce the
  original `item_1` echo; the fix is proven on the bridge→codex wire (the id Codex
  would store is now `ctc_`-prefixed).

## 5. Docs
- [x] 5.1 No CLAUDE.md/AGENTS.md constitution change needed — the durable protocol
  fact is captured in the spec delta (the `custom_tool_call` id must round-trip
  `ctc`-prefixed).
