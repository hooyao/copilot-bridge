## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The outbound translator (T4) SHALL emit a CUSTOM (grammar) tool call — gpt-5.6's `exec` — to Codex as a `custom_tool_call` output item, streamed via `response.custom_tool_call_input.delta/.done` (field `input`), NOT a `function_call` with `function_call_arguments`. Codex's exec handler accepts only the Custom payload; a `function_call` is rejected as an "incompatible payload" and every exec aborts at dispatch (a failure invisible in the bridge's HTTP status). A plain (non-grammar) function tool SHALL still be emitted as a `function_call`. T4 SHALL decide the shape per output block from the T3-stamped `bridge_input_is_grammar_text` marker, resetting that decision on every block so a custom tool followed by a function tool each emit their own shape.

The bridge SHALL NOT leak its internal `content_block` markers (`bridge_input_is_grammar_text`, `bridge_tool_namespace`) to any client. On the Codex route T4 removes them by rebuilding the output item from typed fields; on the CC→gpt route (Claude Code routed to a gpt-5.6 `/responses` backend, where there is no T4) the Claude Code outbound adapter SHALL scrub them from `content_block_start` events before they reach the client. The scrub SHALL be a byte-identical pass-through when no marker is present.

#### Scenario: exec is emitted to Codex as a custom_tool_call

- **WHEN** Copilot streams a custom (grammar) tool call (item type `custom_tool_call`, e.g. `exec`)
- **THEN** T4 re-emits it to Codex as a `custom_tool_call` output item with its input on `response.custom_tool_call_input.done`, and NOT as a `function_call` — so codex constructs a Custom payload and dispatches the tool instead of aborting with "incompatible payload"

#### Scenario: a plain function tool is still a function_call

- **WHEN** Copilot streams a plain function tool call (item type `function_call`) in the same or a later block
- **THEN** T4 emits it as a `function_call` with `function_call_arguments.*` — the custom-tool decision does not bleed across blocks

#### Scenario: bridge markers never reach a Claude Code client

- **WHEN** the CC→gpt route produces a tool_use `content_block_start` carrying `bridge_input_is_grammar_text` and/or `bridge_tool_namespace`
- **THEN** the Claude Code outbound adapter removes those keys from the content_block before the event reaches `claude.exe`, while preserving every real field (type/id/name/input); a marker-free event passes through byte-identical
