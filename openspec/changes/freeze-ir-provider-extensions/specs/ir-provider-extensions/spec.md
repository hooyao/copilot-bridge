## ADDED Requirements

### Requirement: Namespaced provider-extensions escape hatch

The IR SHALL carry a namespaced escape-hatch — `ProviderExtensions` — that holds provider-specific data the Anthropic-shape IR does not type, keyed by provider name, with opaque JSON values the pipeline never interprets. It MUST be serialized AOT-safely through the single source-generated JSON context (values as `JsonElement` copied verbatim, no per-provider reflection-serialized DTO).

#### Scenario: Un-modeled provider knobs survive in the bag

- **WHEN** a request carries fields the Anthropic IR has no typed home for (e.g. `store`, `service_tier`, `include`, `prompt_cache_key`, `text.verbosity`) under a provider key
- **THEN** they are preserved verbatim in `ProviderExtensions["<provider>"]` and are recoverable unchanged, rather than being dropped

#### Scenario: The bag is opaque to the pipeline

- **WHEN** the request flows through pipeline stages
- **THEN** no stage parses or mutates the inner JSON of `ProviderExtensions`; values are treated as opaque `JsonElement` copied verbatim

### Requirement: Provider-extensions attaches at request and content-part level

`ProviderExtensions` SHALL be attachable at the request level (`MessagesRequest`) and the content-part level (`ContentBlockParam`). Request-level support is required; part-level is defined for symmetry and future use and MAY ship empty.

#### Scenario: Request-level bag is present

- **WHEN** the IR models a request
- **THEN** `MessagesRequest` exposes a `ProviderExtensions` slot that round-trips through serialization

#### Scenario: Part-level bag is defined

- **WHEN** the IR models a content part
- **THEN** `ContentBlockParam` (base or per-variant) supports a `ProviderExtensions` slot, even if unused by current clients

### Requirement: Frozen IR contract

The IR contract SHALL be fixed as: `MessagesRequest` + `MessageParam` + the `ContentBlockParam` tagged-union as the IR body; `ProviderExtensions` as the lossless tail; reasoning via the existing `ThinkingBlockParam{Thinking,Signature}` / `RedactedThinkingBlockParam{Data}` plus `OutputConfig.Effort`; tool input/result as byte-faithful `JsonElement`; and the existing Anthropic SSE event model as the streaming IR. The IR body shape SHALL NOT grow per-provider fields — anything a provider sends that the body does not type goes in the bag.

#### Scenario: Un-modeled field goes to the bag, not a new IR field

- **WHEN** a future provider sends a field not in the frozen IR body
- **THEN** it is carried in `ProviderExtensions`, and no per-provider field is added to `MessagesRequest`/`MessageParam`/`ContentBlockParam`

#### Scenario: Existing reasoning and tool fidelity are part of the frozen contract

- **WHEN** a request carries a thinking block with a signature, or a tool call with JSON input
- **THEN** the signature round-trips via `ThinkingBlockParam.Signature`/`RedactedThinkingBlockParam.Data` and the tool input round-trips byte-faithfully via `JsonElement`, as frozen contract guarantees

### Requirement: Hot path stays byte-identical

Adding the escape hatch SHALL NOT change the bytes the bridge sends upstream for the existing Claude Code path. When `ProviderExtensions` is empty/absent (the Claude Code case), the serialized request MUST be byte-identical to the pre-change output.

#### Scenario: Empty bag emits nothing

- **WHEN** a Claude Code request (no provider-extensions) is serialized for the upstream call before and after this change
- **THEN** the two byte sequences are identical

#### Scenario: Existing Anthropic suite passes unchanged

- **WHEN** the existing Anthropic playground + unit test suite runs after this change
- **THEN** it passes with no test edits made to accommodate the change
