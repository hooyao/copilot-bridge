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
  function tools stream via deltas) but closes the identical latent gap.
- T4 needs no change: custom-tool deltas normalize to `input_json_delta` in T3 and
  flow through T4's existing `input_json_delta → function_call_arguments` path, so
  the Codex-facing wire stays `function_call` (which Codex accepts).

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: the streaming-translation requirement is extended so
  the Responses→IR translator (T3) carries a **custom tool's** call input
  (`custom_tool_call_input.*`), not only a function tool's
  (`function_call_arguments.*`). Both map to the same IR `input_json_delta`.

## Impact

- **Modified production code**: `Pipeline/Strategies/Codex/ResponsesToAnthropicStream.cs`
  (two new event cases + the `_blockSawArgsDelta` flag).
- **Tests**: `CodexStreamRoundTripTests` gains 4 custom-tool tests (delta→input_json_delta,
  full T3→T4 round-trip with non-empty args, `.done`-only fallback, and a
  **real-capture** round-trip proving the exact broken data now carries its args),
  all mutation-checked. Fixture `responses-sse-customtool.txt` (de-identified,
  neutral JS, no encrypted blobs / session content). Live grounding:
  `CustomToolStreamingProbe`, plus `CodexLoadTaskSmokeTests` still green.
- **No breaking changes** — additive; the `/cc` (Claude Code) path and function-tool
  path are unchanged (function `.done` fallback is guarded inert).
- **Fixes a silent data-loss bug** that made gpt-5.6 exec-tool use completely
  non-functional through the bridge despite HTTP 200s.
