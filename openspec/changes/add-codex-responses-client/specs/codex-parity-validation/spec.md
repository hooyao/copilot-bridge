## ADDED Requirements

### Requirement: Round-trip self-inverse on real Codex captures

The translators SHALL be validated for round-trip fidelity using real, de-identified, committed Codex request captures: `Responses →T1→ IR →T2→ Responses` preserves the request under the per-field fidelity bar (`docs/ir-definition-design.md` §7.1), with diffs classified by the change-1 field-diff harness as `identical | allowed-transform | VIOLATION`.

#### Scenario: A1 round-trip preserves a real Codex request

- **WHEN** a committed real Codex `ResponsesRequest` capture is run `T1 → IR → T2`
- **THEN** the emitted Responses body equals the input under the fidelity bar, modulo only the documented coercions (effort clamp, `service_tier` strip, `image_generation` drop), with any other diff a VIOLATION

### Requirement: Opaque byte-passthrough and bag transport

The translators SHALL preserve byte-for-byte the opaque fields (`function_call` arguments, `apply_patch` custom tool body, reasoning `encrypted_content`) and SHALL carry un-modeled knobs through the IR via the `ProviderExtensions` bag.

#### Scenario: A2 opaque fields byte-identical

- **WHEN** a real capture with tool-call arguments, an `apply_patch` custom tool, and `encrypted_content` round-trips through the IR
- **THEN** each of those is byte-identical input→output

#### Scenario: A4 un-modeled knobs transit via the bag

- **WHEN** `store`/`include`/`prompt_cache_key`/`text.verbosity` ride `ProviderExtensions["openai"]` through the Anthropic IR
- **THEN** they reappear intact at T2 (before any documented coercion)

### Requirement: Tool-pairing and stream round-trip fidelity

The translators SHALL preserve tool-call/result pairing across a multi-turn request (A5) and SHALL round-trip the streaming response: `responses-sse` fixtures `T3 → IR → T4` yield the same event types in order, deltas that concatenate to identical text, byte-identical argument fragments, and the preserved terminal `response.completed` (A6).

#### Scenario: A5 tool pairing survives

- **WHEN** a multi-turn capture with `function_call` + `function_call_output` round-trips
- **THEN** `call_id` linkage and ordering survive

#### Scenario: A6 stream round-trip preserves the event sequence

- **WHEN** a real `responses-sse` fixture is run `T3 → IR → T4`
- **THEN** the re-emitted event sequence matches in order, text concatenation is identical, argument fragments are byte-identical, and `response.completed` is present with no spurious `[DONE]`

### Requirement: Hot-path byte-equality after registering the Codex strategy

Adding `CopilotResponsesStrategy` and the Codex adapters to the shared pipeline registry SHALL NOT change the bytes the bridge sends for the existing Claude Code path.

#### Scenario: H1 CC output byte-identical

- **WHEN** real `cc-request` fixtures run through `Pipeline<MessagesRequest>` before and after the Codex strategy is registered
- **THEN** the serialized upstream body is byte-identical, and the existing Anthropic suite passes unchanged (H2)

### Requirement: Live codex.exe end-to-end proof

A live integration test SHALL drive the real `codex.exe` through `/codex/responses` to Copilot `/responses` and assert a full turn completes, closing the loop between offline fixtures and live behavior. It MUST use an ephemeral non-default port and MUST NOT mutate the user's `~/.codex/config.toml`.

#### Scenario: E1 real round-trip completes

- **WHEN** the harness runs `codex exec --json` with the provider's `base_url` pointed at an ephemeral bridge instance and a tool-forcing prompt
- **THEN** a full turn — text and a tool call — completes and reaches Codex's stdout JSONL, and the bridge's four-file IO audit is saved for post-hoc diff against the offline goldens

#### Scenario: Harness leaves the environment untouched

- **WHEN** the harness configures Codex
- **THEN** it injects the provider via `codex exec -c` overrides (not by editing `~/.codex/config.toml`), uses a non-8765 port, and is tagged `[Trait("Category","Integration")]`
