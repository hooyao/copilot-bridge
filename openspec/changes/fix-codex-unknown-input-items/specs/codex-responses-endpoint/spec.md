## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The request translator (T1) SHALL accept **any** `input[]` item type. The Responses `input[]` union is open-ended (gpt-5.6 multi-agent / tool-search / compaction features keep adding types); T1 SHALL NOT reject an item whose `type` it does not model with a `Polymorphism_UnrecognizedTypeDiscriminator` 400. A modeled type binds to its record; an UNMODELED type is captured opaquely (byte-faithful) and carried through the IR. T2 SHALL re-emit every item — modeled, `agent_message`, or unknown — VERBATIM and IN ORDER into the outbound `input[]`. An `agent_message` (gpt-5.6 inter-agent message) carrying an `encrypted_content` blob SHALL round-trip byte-faithfully (the encrypted bytes are never mutated).

#### Scenario: Unmodeled input item type is forwarded, not rejected

- **WHEN** a Codex request carries an `input[]` item whose `type` the bridge does not model (e.g. `tool_search_call`, `compaction`)
- **THEN** T1 deserializes it without throwing, carries it opaquely through the IR, and T2 re-emits it verbatim to Copilot — instead of the previous 400

#### Scenario: agent_message round-trips byte-faithfully and in order

- **WHEN** a Codex request carries an `agent_message` (author/recipient/content with an `encrypted_content` blob) between two conversation messages
- **THEN** the emitted upstream `input[]` contains the same `agent_message` with author, recipient, and the encrypted blob byte-identical, positioned between the same two messages

#### Scenario: Modeled item types are unchanged

- **WHEN** a Codex request carries only modeled item types (message, function_call, function_call_output, reasoning, additional_tools)
- **THEN** each binds and re-emits exactly as before — byte-identical to the pre-fix behavior
