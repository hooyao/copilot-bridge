## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The request translator (T1) SHALL accept **any** `input[]` item type. The Responses `input[]` union is open-ended (gpt-5.6 multi-agent / tool-search / compaction features keep adding types); T1 SHALL NOT reject an item whose `type` it does not model with a `Polymorphism_UnrecognizedTypeDiscriminator` 400. A MODELED type (`message`, `function_call`, `function_call_output`, `reasoning`) binds to its record and is translated through the IR — modeled items are NOT verbatim (e.g. a developer/system `message` folds into the top-level `instructions`, a `function_call`'s arguments cross the string↔object boundary). An UNMODELED type — the `additional_tools` harness preamble, `agent_message` (inter-agent), and any future type — is captured opaquely as a whole `JsonElement` and re-emitted VERBATIM (byte-faithful, every sibling field preserved). The bridge SHALL preserve the input ORDER of opaque items relative to the emitted modeled items and to each other. An `agent_message` carrying an `encrypted_content` blob SHALL round-trip with full value fidelity (the encrypted bytes are never mutated).

#### Scenario: Unmodeled input item type is forwarded, not rejected

- **WHEN** a Codex request carries an `input[]` item whose `type` the bridge does not model (e.g. `tool_search_call`, `compaction`, `additional_tools`, `agent_message`)
- **THEN** T1 deserializes it without throwing, carries it opaquely (whole element) through the IR, and T2 re-emits it verbatim to Copilot — instead of the previous 400

#### Scenario: agent_message round-trips verbatim and in order

- **WHEN** a Codex request carries an `agent_message` (author/recipient/content with an `encrypted_content` blob) between two conversation messages
- **THEN** the emitted upstream `input[]` contains the same `agent_message` — every field including the encrypted blob preserved — positioned between the same two messages; and an opaque item that PRECEDES another opaque item (e.g. an unknown item before the `additional_tools` preamble) keeps its relative order

#### Scenario: Modeled item types bind and translate as before

- **WHEN** a Codex request carries modeled item types (message, function_call, function_call_output, reasoning)
- **THEN** each binds to its typed record and is translated through the IR exactly as before this change (no new transformation) — the opaque-passthrough mechanism does not alter the modeled path
