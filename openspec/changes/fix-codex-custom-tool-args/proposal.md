## Why

gpt-5.6's `exec` is a **custom (grammar-constrained) tool**, and Copilot's native
`/responses` streams a custom tool's call input via
`response.custom_tool_call_input.delta`/`.done` — NOT the
`response.function_call_arguments.*` a plain function tool uses.

The bridge's T3 translator (`ResponsesToAnthropicStream`, Responses SSE → IR) only
handled `function_call_arguments.*`. It opened a `tool_use` block for a
`custom_tool_call` item but then **swallowed every `custom_tool_call_input.delta`**,
so the tool's arguments never reached the IR. T4 re-emitted the call to Codex with
`arguments:""`, and Codex **aborted every exec call** (`function_call_output:
"aborted"`).

Measured on a real gpt-5.6-sol session (0.4.10-beta, latest Codex): **239 of 242
custom-tool calls lost their arguments** — the model ran nothing while every turn
returned HTTP 200. Grounded in the desktop capture (232 `custom_tool_call_input.delta`
+ a `.done` with the full 386-char exec call) and a live probe
(`CustomToolStreamingProbe`: gpt-5.6-sol emits `custom_tool_call_input.*`, never
`function_call_arguments`, `item.type=custom_tool_call`).

## What Changes

- **T3 handles custom-tool input events.** `response.custom_tool_call_input.delta`
  → `input_json_delta` (identical mapping to `function_call_arguments.delta`);
  `.done` carries the complete input under the field `input` (not `arguments`) and
  is emitted as a single fragment ONLY when the stream sent no deltas (a
  `_blockSawArgsDelta` guard prevents a double-emit on the normal delta path).
- **Symmetric `.done` fallback for function tools.** The same no-delta fallback is
  added for `function_call_arguments.done` (field `arguments`) — inert today (real
  function tools stream via deltas) but closes the identical latent gap. (So T3
  gains **three** new event cases: custom `.delta`, custom `.done`, function `.done`.)
- **Response-stage validation must skip grammar-text input.** A custom tool's input
  is arbitrary text (raw JS), not a JSON object, so the response-side
  `ToolInputValidationDetector` must NOT JSON-parse it — otherwise every valid exec
  call trips "malformed JSON" and, under `MalformedJsonAction=Abort*`, is killed
  before T4. T3 stamps the custom `tool_use` content_block with a bridge-internal
  marker `bridge_input_is_grammar_text:true`, and the detector skips validation for
  a marked block (both the streaming `StopBlock` path and `InspectBuffered`). The
  marker is IR-internal: T4 rebuilds the Responses output item from `type/id/name`,
  so it never reaches the Codex client, and only T3 emits it, so `/cc` is untouched.
- T4 needs no change: custom-tool deltas normalize to `input_json_delta` in T3 and
  flow through T4's existing `input_json_delta → function_call_arguments` path, so
  the Codex-facing wire stays `function_call` (which Codex accepts).

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: the streaming-translation requirement is extended so
  the Responses→IR translator (T3) carries a **custom tool's** call input
  (`custom_tool_call_input.*`), not only a function tool's
  (`function_call_arguments.*`); both map to the same IR `input_json_delta`. The
  response-stage tool-input validator SHALL skip JSON/schema validation for a
  grammar-text (custom) tool block so a valid exec call is never flagged malformed.

## Impact

- **Modified production code**:
  - `Pipeline/Strategies/Codex/ResponsesToAnthropicStream.cs` — three new event
    cases (`custom_tool_call_input.delta`/`.done`, `function_call_arguments.done`),
    the `_blockSawArgsDelta` flag + per-item reset, and the
    `bridge_input_is_grammar_text` marker on a custom tool_use block.
  - `Pipeline/Response/Detection/ToolInputValidationDetector.cs` — skip
    JSON/schema validation for a marked grammar-text block on both the streaming
    (`StopBlock`) and buffered (`InspectBuffered`) paths (+ the `_currentBlockIsGrammarText`
    flag and its reset). This is what makes custom-tool forwarding work under an
    `Abort*` configuration.
- **Tests**: `CodexStreamRoundTripTests` gains custom-tool tests (delta→input_json_delta,
  full T3→T4 round-trip with non-empty args + a marker-non-leak assertion,
  `.done`-only fallback, a function-tool `.done`-only fallback, a back-to-back
  two-call flag-reset guard, and a **real-capture** round-trip); `ToolInputValidationDetectorTests`
  gains `CustomGrammarTool_RawTextInput_NotJsonValidated_EvenUnderAbort` (full
  response-stage path). All mutation-checked. Fixture `responses-sse-customtool.txt`
  (de-identified, neutral JS). Live grounding: `CustomToolStreamingProbe`, plus
  `CodexLoadTaskSmokeTests` still green.
- **No breaking changes** — additive; the `/cc` (Claude Code) path, the function-tool
  path, and default (Observe) validation behavior are unchanged.
- **Fixes a silent data-loss bug** that made gpt-5.6 exec-tool use completely
  non-functional through the bridge despite HTTP 200s.
