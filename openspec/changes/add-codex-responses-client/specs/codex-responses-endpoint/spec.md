## ADDED Requirements

### Requirement: Codex Responses endpoint

The bridge SHALL serve `POST /codex/responses` (the path a Codex custom provider produces from `base_url + /responses`, verified in `docs/codex-protocol-research.md` §3.4). The handler reads and audits the body, translates it to the IR via T1, runs the shared IR pipeline, translates the IR response back via T4, and writes the result to Codex. The existing Claude Code `/cc` routes SHALL be unchanged.

#### Scenario: Codex request is served on the endpoint

- **WHEN** a Codex custom provider with `base_url` pointed at the bridge's `/codex` prefix issues a Responses request
- **THEN** it is handled at `POST /codex/responses`, and `/cc` routes are unaffected

#### Scenario: No models or count_tokens route

- **WHEN** the Codex endpoint set is mounted
- **THEN** only `/codex/responses` exists (no `/codex/models`, no count_tokens), consistent with Codex not calling `/models` against a custom provider

### Requirement: Fully-typed Responses DTOs

The Codex Responses request, input items, content parts, tool variants, reasoning, text controls, and SSE events SHALL be modeled as fully-typed DTOs (no opaque `JsonElement` passthrough for fields the translators read or rewrite), grounded in `references/openai-sdk-pkg/` and the verified captures, and serialized exclusively through the source-generated JSON context.

#### Scenario: Request body round-trips through typed DTOs

- **WHEN** a Codex `ResponsesRequest` (model, instructions, input, tools, tool_choice, parallel_tool_calls, reasoning, store, stream, include, prompt_cache_key, text, client_metadata) is deserialized and re-serialized
- **THEN** it is handled by typed DTOs via the source-gen context, with no reflection-based serialization

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

#### Scenario: Codex request transits the IR and routes by model

- **WHEN** a Codex request for a `gpt-*` model is processed
- **THEN** T1 produces an IR body, the shared pipeline routes it (by model) to `CopilotResponses` + `/responses`, the strategy's T2 emits the wire request, T3 ingests the response, and T4 returns it in Responses shape

#### Scenario: Routing is on the IR, backend-agnostic

- **WHEN** the router resolves a model
- **THEN** it keys off the resolved model id (`claude-*` → `/v1/messages`, `gpt-*` → `/responses`), independent of which client's adapter produced the IR

### Requirement: Forward to native /responses with official Copilot headers

The bridge SHALL forward the Codex request to Copilot's native `/responses` endpoint signed with the Copilot bearer and the official VS Code Copilot header set (via the existing endpoint-agnostic header factory), dropping Codex's own `x-codex-*`/`originator`/`session-id` headers. It SHALL set `Copilot-Vision-Request: true` when an `input_image` is present and `x-initiator` per the last input item. Streaming SSE SHALL be forwarded preserving the event sequence including `function_call_arguments.*` and the terminal `response.completed`, with no `[DONE]` filtering (Copilot `/responses` emits none).

#### Scenario: Codex headers replaced with Copilot headers

- **WHEN** the bridge forwards a Codex request carrying `x-codex-*` headers
- **THEN** those are dropped and the official Copilot editor/version/integration headers + bearer are sent

#### Scenario: Tool-call stream reaches Codex intact

- **WHEN** Copilot returns a streaming `/responses` response with function-call argument events and a terminal `response.completed`
- **THEN** the bridge forwards every event in order and Codex receives the terminal event it requires
