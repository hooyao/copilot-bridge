## ADDED Requirements

### Requirement: Field-diff harness with explicit diff classification

The test framework SHALL provide a field-diff harness (extending the existing `ApiComparisonTests` JsonNode differ) that compares two request/response bodies and classifies every difference as `identical`, `allowed-transform`, or `VIOLATION`. Only `VIOLATION`s fail a test. The allowed-transform list MUST be an explicit, reviewed allowlist tied to our own translation code (e.g. model-id normalization) — never a silent tolerance.

#### Scenario: Unlisted difference fails

- **WHEN** the harness compares two bodies and finds a difference not on the allowed-transform allowlist
- **THEN** it classifies the diff as `VIOLATION` and the test fails with the diff reported

#### Scenario: Listed transform passes

- **WHEN** the harness finds a difference that matches an explicit allowed-transform entry (e.g. a model id rewritten by our normalizer)
- **THEN** it classifies the diff as `allowed-transform` and does not fail

### Requirement: Invariant tests assert our translators, not Copilot behavior

The A-invariant tests SHALL assert mathematical properties of our own translation that hold regardless of how Copilot behaves, using captured traces only as input samples. They MUST NOT encode "what Copilot currently accepts/rejects" — that is the live contract tests' job (a later change).

#### Scenario: Goldens encode only our transforms

- **WHEN** an invariant golden expectation is defined
- **THEN** it reflects only our own deterministic transforms (e.g. normalization, bag re-application), never an assertion about Copilot's current acceptance

### Requirement: Round-trip self-inverse on the IR

The framework SHALL verify that translating into the IR and back is self-inverse under the per-field fidelity bar (byte-identical for tool args / opaque reasoning blobs; semantically equal for structural moves; referential integrity for tool-call ids).

#### Scenario: A1 round-trip preserves the input

- **WHEN** a captured request sample is run into the IR and back out
- **THEN** the output equals the input under the fidelity bar, with any diff classified by the harness

### Requirement: Opaque byte-passthrough

The framework SHALL verify that opaque fields — tool-call arguments, tool results, and reasoning signatures/blobs — are byte-identical input→output (raw-text compare, not value compare).

#### Scenario: A2 tool args and signatures are byte-identical

- **WHEN** a sample containing tool-call JSON arguments and a thinking-block signature round-trips through the IR
- **THEN** the arguments JSON and the signature are byte-identical to the input

### Requirement: Provider-extensions bag survival and transport

The framework SHALL verify the escape hatch is never silently dropped and carries un-modeled knobs intact through the pipeline.

#### Scenario: A3 canary survives

- **WHEN** an unknown value `ProviderExtensions["openai"]["__canary__"]` is injected into the IR mid-pipeline
- **THEN** it emerges byte-identical at the outbound boundary (guarding against a converter dropping the bag)

#### Scenario: A4 un-modeled knobs transit intact

- **WHEN** knobs like `store`/`include`/`prompt_cache_key`/`text.verbosity` ride the bag through the Anthropic IR
- **THEN** they reappear intact at the outbound boundary

### Requirement: Hot-path byte-equality test

The framework SHALL include a hot-path regression test (H1) that replays real Claude Code request fixtures through the pipeline before and after the escape hatch is added and asserts the serialized upstream body is byte-identical, plus the unchanged-suite guarantee (H2).

#### Scenario: H1 byte-identical upstream body

- **WHEN** a real `cc-request` fixture is serialized for the upstream call with the escape-hatch code present but empty
- **THEN** the bytes match the pre-change output exactly

#### Scenario: Fixtures are real and version-stamped

- **WHEN** a request fixture is added to the committed corpus
- **THEN** it is de-identified real wire data captured from `claude.exe` via the bridge, stamped with client version + capture date, and used only as an input sample (not as an oracle of Copilot behavior)
