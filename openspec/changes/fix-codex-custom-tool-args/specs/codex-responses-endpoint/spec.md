## MODIFIED Requirements

### Requirement: Forward to native /responses with official Copilot headers

The bridge SHALL forward the Codex request to Copilot's native `/responses` endpoint signed with the Copilot bearer and the official VS Code Copilot header set (via the existing endpoint-agnostic header factory), dropping Codex's own `x-codex-*`/`originator`/`session-id` headers. It SHALL set `Copilot-Vision-Request: true` when an `input_image` is present and `x-initiator` per the last input item. Streaming SSE SHALL be forwarded preserving the event sequence including `function_call_arguments.*` and the terminal `response.completed`, with no `[DONE]` filtering (Copilot `/responses` emits none).

The response-side streaming translator (T3, Responses SSE → IR) SHALL carry a tool call's arguments for BOTH tool shapes Copilot emits: a plain function tool's `response.function_call_arguments.delta`/`.done` AND a **custom (grammar-constrained) tool's** `response.custom_tool_call_input.delta`/`.done` (the latter is how gpt-5.6's `exec` tool streams its input). Both SHALL map to the IR `input_json_delta` so the arguments reach the IR and are re-emitted downstream; the custom-tool `.done` field is `input` (the function-tool `.done` field is `arguments`). A tool call's arguments SHALL NOT be dropped for any tool shape Copilot streams — dropping them makes the client receive an empty-argument call and abort it.

#### Scenario: Custom-tool call input reaches the client

- **WHEN** Copilot's `/responses` streams a `custom_tool_call` (e.g. gpt-5.6's grammar `exec` tool) whose input arrives as `response.custom_tool_call_input.delta` fragments then `response.custom_tool_call_input.done`
- **THEN** T3 emits the fragments as IR `input_json_delta`, and the re-emitted Codex-facing `function_call_arguments.done` (and the `function_call` output item) carries the complete, non-empty arguments — never `""`

#### Scenario: No-delta fallback carries the full input

- **WHEN** a tool-call stream delivers the complete arguments only on its `.done` event with zero preceding deltas (custom tool `input`, or function tool `arguments`)
- **THEN** T3 emits that full string once as an `input_json_delta` so the call is non-empty, and does NOT double-emit when deltas were present

#### Scenario: Streaming SSE preserved with tool events

- **WHEN** Copilot returns a streaming `/responses` result with tool calls
- **THEN** the bridge forwards the event sequence — including the tool-argument events for both function and custom tools — and the terminal `response.completed`, with no `[DONE]` inserted
