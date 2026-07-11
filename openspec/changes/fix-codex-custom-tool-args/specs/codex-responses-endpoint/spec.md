## MODIFIED Requirements

### Requirement: Forward to native /responses with official Copilot headers

The bridge SHALL forward the Codex request to Copilot's native `/responses` endpoint signed with the Copilot bearer and the official VS Code Copilot header set (via the existing endpoint-agnostic header factory), dropping Codex's own `x-codex-*`/`originator`/`session-id` headers. It SHALL set `Copilot-Vision-Request: true` when an `input_image` is present and `x-initiator` per the last input item. Streaming SSE SHALL be forwarded preserving the event sequence including `function_call_arguments.*` and the terminal `response.completed`, with no `[DONE]` filtering (Copilot `/responses` emits none).

The response-side streaming translator (T3, Responses SSE → IR) SHALL carry a tool call's arguments for BOTH tool shapes Copilot emits: a plain function tool's `response.function_call_arguments.delta`/`.done` AND a **custom (grammar-constrained) tool's** `response.custom_tool_call_input.delta`/`.done` (the latter is how gpt-5.6's `exec` tool streams its input). Both SHALL map to the IR `input_json_delta` so the arguments reach the IR and are re-emitted downstream; the custom-tool `.done` field is `input` (the function-tool `.done` field is `arguments`). A tool call's arguments SHALL NOT be dropped for any tool shape Copilot streams — dropping them makes the client receive an empty-argument call and abort it.

Because a custom tool's input is arbitrary grammar-constrained TEXT (raw JavaScript for `exec`), not a JSON object, the response-stage tool-input validator SHALL NOT JSON-parse or schema-validate a custom-tool block — otherwise a valid call is flagged "malformed JSON" and, under an `Abort*` action, killed before it reaches the client. T3 SHALL mark a custom tool's `tool_use` block so the validator can identify it, and the validator SHALL skip validation for a marked block on BOTH its streaming and buffered code paths. This marker is bridge-internal: it SHALL NOT appear on the Codex-facing wire (the outbound translator rebuilds the tool item without it) and SHALL NOT appear on the Claude Code (`/cc`) path (only the Codex translator emits it).

#### Scenario: Custom-tool call input reaches the client

- **WHEN** Copilot's `/responses` streams a `custom_tool_call` (e.g. gpt-5.6's grammar `exec` tool) whose input arrives as `response.custom_tool_call_input.delta` fragments then `response.custom_tool_call_input.done`
- **THEN** T3 emits the fragments as IR `input_json_delta`, and the re-emitted Codex-facing `function_call_arguments.done` (and the `function_call` output item) carries the complete, non-empty arguments — never `""`

#### Scenario: Custom-tool raw input is not JSON-validated (no false abort)

- **WHEN** a custom (grammar) tool block whose input is raw text (not a JSON object) reaches the response-stage tool-input validator, on either the streaming or buffered path, even under `MalformedJsonAction`/`SchemaViolationAction` = an `Abort*` action
- **THEN** the validator skips it — no "malformed JSON"/schema flag, no abort — because a custom tool's input has no JSON shape to validate

#### Scenario: Validation-bypass marker never leaks

- **WHEN** a custom-tool response is translated back to the Codex `/responses` wire, and separately when any Claude Code (`/cc`) response is produced
- **THEN** the bridge-internal grammar-text marker appears in neither: the Codex-facing output is rebuilt from the tool's `type`/`id`/`name` without it, and the `/cc` path never emits it

#### Scenario: No-delta fallback carries the full input

- **WHEN** a tool-call stream delivers the complete arguments only on its `.done` event with zero preceding deltas (custom tool `input`, or function tool `arguments`)
- **THEN** T3 emits that full string once as an `input_json_delta` so the call is non-empty, and does NOT double-emit when deltas were present

#### Scenario: Streaming tool arguments normalized regardless of upstream shape

- **WHEN** Copilot returns a streaming `/responses` result with tool calls, whose arguments arrive as either `function_call_arguments.*` (function tools) or `custom_tool_call_input.*` (custom/grammar tools)
- **THEN** the bridge carries the arguments from EITHER upstream shape through the IR (as `input_json_delta`) and re-emits them to Codex as `response.function_call_arguments.*` (T4's single Codex-facing shape — the custom event types are NOT preserved on the wire), preserving the overall event order and the terminal `response.completed`, with no `[DONE]` inserted
