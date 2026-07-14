## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The response translator (T3) and the Codex-facing outbound translator (T4) SHALL preserve a `custom_tool_call`'s item id across the IR round-trip such that the id Codex stores and echoes on the next turn begins with `ctc`. Copilot's `/responses` endpoint REQUIRES the echoed `custom_tool_call` item id to begin with `ctc` (it rejects any other with `Invalid 'input[N].id': '<id>'. Expected an ID that begins with 'ctc'`), and it checks the PREFIX only — the id need not equal the original. T3 SHALL capture the real upstream `ctc_` id (carried on the `custom_tool_call_input.delta`/`.done` events' `item_id`, NOT on `output_item.added`/`.done`, whose ids are rolling encrypted blobs) and ride it to T4 as a bridge-internal marker. T4 SHALL emit a `ctc`-prefixed id for the `custom_tool_call` item — the real captured id on the completed item, and a deterministic `ctc_`-prefixed synthesis (derived from the `call_id`, no RNG) where the real id is not yet known or was never observed. T4 SHALL NOT emit a non-`ctc` id (e.g. `item_N`) for a `custom_tool_call`. The bridge-internal marker SHALL never reach a client: T4 rebuilds the Codex-facing item from typed fields and drops it, and on the CC→gpt route (no T4) `ClaudeCodeOutboundAdapter` scrubs it from the `content_block_stop` event and the buffered tool_use block. A `message`, `function_call`, or text item is unaffected (it carries no id on the T2 wire, byte-identical to before).

#### Scenario: Custom-tool call id round-trips ctc-prefixed and the echo is accepted

- **WHEN** Copilot streams a `custom_tool_call` (Codex's `exec`) whose real item id is `ctc_…`, and on the next turn Codex echoes that call back
- **THEN** the Codex-facing completed `custom_tool_call` item carries a `ctc`-prefixed id (the real captured id), so when Codex echoes it, T1→T2 forward a `ctc`-prefixed id and Copilot accepts it (200) — never the `item_N` that 400s with `Expected an ID that begins with 'ctc'`

#### Scenario: No upstream ctc id observed still yields a ctc-prefixed id

- **WHEN** the upstream stream surfaces no plaintext `ctc_` id for the `custom_tool_call` (e.g. an older shape, or the id never appeared on an input event)
- **THEN** T4 synthesizes a deterministic `ctc_`-prefixed id from the `call_id` for the item — still prefix-conformant, never `item_N`, so the echo turn cannot 400

#### Scenario: The ctc-id marker never leaks to a client

- **WHEN** the response is produced by the Codex T3 (which stamps `bridge_custom_tool_call_id` on `content_block_stop`) and delivered on either the Codex route or the CC→gpt route
- **THEN** the client never receives the marker — T4 rebuilds the Codex-facing item from typed fields (marker dropped), and `ClaudeCodeOutboundAdapter` scrubs it from the `content_block_stop` event (and the buffered tool_use block) before it reaches `claude.exe`
