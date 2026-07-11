# Tasks — fix gpt-5.6 custom-tool argument loss (T3)

## 1. Ground the contract
- [x] 1.1 From `request-traces` upstream-resp, extract the custom-tool event
  sequence: `output_item.added` (item.type=`custom_tool_call`, call_id/name) →
  `custom_tool_call_input.delta` (field `delta`) × N → `custom_tool_call_input.done`
  (field `input`, NOT `arguments`) → `output_item.done`. Confirmed 239/242 calls
  lost args before the fix.
- [x] 1.2 Live-probe gpt-5.6-sol with a custom (grammar) tool + forced call
  (`CustomToolStreamingProbe`) → 200, emits `custom_tool_call_input.delta`/`.done`,
  never `function_call_arguments`, `item.type=custom_tool_call`.

## 2. Fix T3
- [x] 2.1 Add `response.custom_tool_call_input.delta` → `input_json_delta` (mirrors
  `function_call_arguments.delta`); set `_blockSawArgsDelta`.
- [x] 2.2 Add `response.custom_tool_call_input.done` fallback: emit the full `input`
  once ONLY when no deltas were seen (`!_blockSawArgsDelta && _blockOpen`).
- [x] 2.3 Reset `_blockSawArgsDelta = false` on every new output item
  (`OnOutputItemAdded`), so back-to-back tool calls don't leak the flag.
- [x] 2.4 Symmetric `.done` fallback for `function_call_arguments.done` (field
  `arguments`) — inert today, closes the identical latent gap (review follow-up).
- [x] 2.5 T4 unchanged (custom deltas normalize to `input_json_delta` → existing
  `function_call_arguments` emit).
- [x] 2.6 T3 stamps the custom `tool_use` content_block with a bridge-internal
  marker `bridge_input_is_grammar_text:true`; `ToolInputValidationDetector` skips
  JSON/schema validation for a marked block (streaming `StopBlock` + buffered
  `InspectBuffered`, + the `_currentBlockIsGrammarText` flag/reset). Without this a
  valid exec call (raw JS) trips "malformed JSON" and an `Abort*` config kills it
  before T4 (review round 2). Marker is IR-internal — T4 drops it, `/cc` untouched.

## 3. Tests (from contract, mutation-checked)
- [x] 3.1 `CustomTool_T3_TranslatesInputDeltasToInputJsonDelta`: 2 fragments → 2
  `input_json_delta` (distinguishes delta path from `.done` fallback).
- [x] 3.2 `CustomTool_FullRoundTrip_ArgumentsReachCodexNonEmpty`: T3→T4 emits the
  full args on `function_call_arguments.done` AND the `function_call` output item.
- [x] 3.3 `CustomTool_DoneOnly_NoDeltas_StillCarriesInput`: `.done` fallback.
- [x] 3.4 `CustomTool_RealCapture_ArgumentsRoundTripEqualUpstream`: the REAL
  de-identified gpt-5.6-sol custom-tool SSE (`responses-sse-customtool.txt`) →
  args round-trip equal upstream input, non-empty.
- [x] 3.5 Mutation-checked: disabling the delta handler reddens the 2-count test;
  disabling both delta+done reddens all custom-tool tests. Full suite 830 green.

## 4. Live verification
- [x] 4.1 `CodexLoadTaskSmokeTests` (gpt-5.6-sol) still passes — real codex.exe
  multi-tool task, 4× /responses 200, function_call/output round-trips, exit 0.
- [ ] 4.2 Post-merge on the new beta: re-run a real desktop-Codex exec session and
  confirm the audit shows non-empty custom-tool `arguments` (no more `aborted`).

## 5. Docs
- [x] 5.1 T3 header doc-comment updated with the custom_tool_call_input mapping.
