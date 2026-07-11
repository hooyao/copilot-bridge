## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The request translator (T1) SHALL carry a `function_call`'s `arguments` regardless of its shape: a JSON object (a plain function tool) is carried as the IR `tool_use.input` object and re-emitted byte-faithfully; **raw non-JSON text** (a custom/grammar tool such as Codex's `exec`, echoed back on a follow-up turn) SHALL be carried through the IR and re-emitted verbatim, NOT rejected. T1 SHALL NOT throw a 400 for non-JSON or non-object arguments — a custom tool's input is legitimately non-JSON, and Copilot round-trips it (live-probed 200). Empty/whitespace arguments SHALL become `{}` (a valid empty-input call).

#### Scenario: Custom-tool call echoed with raw-text arguments round-trips

- **WHEN** a Codex follow-up request echoes a prior custom (`exec`) tool call as a `function_call` whose `arguments` is raw JavaScript (not JSON)
- **THEN** T1 accepts it without a 400, and T2 re-emits the `function_call` to Copilot with the `arguments` string byte-identical to what Codex sent (not `{}`, not a double-encoded string) — and Copilot accepts it (200)

#### Scenario: JSON function-tool arguments are unchanged

- **WHEN** a Codex request carries a plain function tool call whose `arguments` is a JSON object
- **THEN** it round-trips exactly as before (object element in, same JSON string out) — the grammar-text path does not affect it

#### Scenario: Empty arguments become an empty object

- **WHEN** a `function_call`'s `arguments` is empty or whitespace
- **THEN** the emitted arguments is `{}`, a valid empty-input call (not treated as grammar text)
