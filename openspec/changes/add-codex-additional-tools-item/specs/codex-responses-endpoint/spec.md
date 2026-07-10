## MODIFIED Requirements

### Requirement: Fully-typed Responses DTOs

The Codex Responses request, input items, content parts, tool variants, reasoning, text controls, and SSE events SHALL be modeled as fully-typed DTOs (no opaque `JsonElement` passthrough for fields the translators read or rewrite), grounded in `references/openai-sdk-pkg/` and the verified captures, and serialized exclusively through the source-generated JSON context.

The `input[]` item union SHALL model the `additional_tools` discriminator (a Codex harness tool-registration preamble, first observed from the gpt-5.6 family) in addition to `message` / `function_call` / `function_call_output` / `reasoning`. Its nested `tools` payload SHALL be carried opaque (a `JsonElement`, never rewritten) because it contains reserved built-in tool schemas (e.g. the `collaboration` namespace) that Copilot's native `/responses` validates against and rejects if altered — so byte fidelity of that payload is required. An unrecognized `input[]` discriminator SHALL NOT cause the request to fail deserialization for a shape Copilot accepts natively.

The `additional_tools` item SHALL round-trip byte-faithfully: T1 SHALL carry it through the IR via the request-level `ProviderExtensions["openai"]` escape-hatch (not the messages array, not a new typed IR field), and T2 SHALL re-emit it into the outbound `input[]` — ahead of the conversation messages, matching every observed capture — with its `tools` payload unchanged and no model coercions applied to it. On the Claude Code `/cc` path this item never appears, and its carriage SHALL add no bag key there, keeping `/cc` serialization byte-identical.

#### Scenario: Request body round-trips through typed DTOs

- **WHEN** a Codex `ResponsesRequest` (model, instructions, input, tools, tool_choice, parallel_tool_calls, reasoning, store, stream, include, prompt_cache_key, text, client_metadata) is deserialized and re-serialized
- **THEN** it is handled by typed DTOs via the source-gen context, with no reflection-based serialization

#### Scenario: additional_tools item deserializes instead of 400-ing

- **WHEN** a Codex request carries an `input[]` item with `type: "additional_tools"` (as the gpt-5.6 CLI emits, e.g. `request-traces/20260710-145459-0001`)
- **THEN** it deserializes to a modeled `ResponsesInputItem` variant with no `Polymorphism_UnrecognizedTypeDiscriminator` fault, and the request proceeds through T1/routing/T2

#### Scenario: additional_tools round-trips byte-faithfully to Copilot

- **WHEN** a Codex request containing an `additional_tools` item at `input[0]` transits T1 then T2
- **THEN** the outbound `/responses` body contains an `additional_tools` item whose `tools` payload is byte-identical to the inbound item and is positioned before the conversation messages, and Copilot's native `/responses` accepts it (verified live: `ResponsesProbe.AdditionalToolsVerbatim` → 200)

#### Scenario: Claude Code path unaffected

- **WHEN** a Claude Code `/cc` request (which never carries `additional_tools`) is processed
- **THEN** the IR has no `additional_tools` bag key and the `/cc` output bytes are unchanged from before this change
