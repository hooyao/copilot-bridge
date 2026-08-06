## MODIFIED Requirements

### Requirement: Capability is exact and probe-grounded

The bridge SHALL enable structured multimodal function outputs only for exact model profiles whose current Copilot behavior was established by a direct two-turn probe containing `function_call_output.output` content items. It MUST NOT infer this capability from model family, ordinary top-level vision support, or a nearest-profile fallback.

The capability SHALL be three-valued: **Supported** (probed and accepted), **Unsupported** (probed and rejected), and **Unknown** (the exact model is not in the catalog). Only Supported enables structured output; both other states retain the string/verbatim compatibility behavior. A boolean that collapses Unsupported and Unknown MUST NOT be used, because those two states differ in what an operator needs to do about them.

When an image-bearing tool result is downgraded to the compatibility path because the exact model's capability is **Unknown**, the bridge SHALL record that downgrade as an observable event carried out of the request translator and surfaced in the request's audit record. A model rename on the Copilot side therefore appears as an explicit signal rather than as a turn that silently reaches the model without its image. A downgrade for a model probed as **Unsupported** is expected behavior and need not be reported per request.

The decision SHALL be driven by the IR alone. A source that requires its tool output to remain uninterpreted SHALL state that on the IR block, and the request translator SHALL read that statement; the translator MUST NOT be parameterized by which client produced the request.

#### Scenario: Proven model uses structured output
- **WHEN** the exact selected model profile is `gpt-5.6-sol` with the recorded successful live probe
- **THEN** a supported image tool result uses structured output.

#### Scenario: Unprobed model uses compatibility fallback
- **WHEN** the exact selected model has no proven structured-function-output capability
- **THEN** the bridge retains the established string/verbatim compatibility behavior
- **AND** it does not copy a positive capability from a fuzzy nearest profile.

#### Scenario: Source-marked opaque output is never reinterpreted
- **WHEN** two requests carry identical tool-result JSON but only one source marked its output opaque on the IR
- **THEN** the marked one is re-emitted verbatim and the unmarked one is interpreted as Anthropic blocks
- **AND** neither outcome depends on a client-identity parameter passed to the translator.

#### Scenario: Uncatalogued model downgrading an image is reported
- **WHEN** a request carrying an image tool result resolves to a model with no exact catalog entry
- **THEN** the tool result takes the string compatibility path and vision is not claimed
- **AND** the request's audit record reports that an image was downgraded for an unknown-capability model, so a Copilot-side model rename is visible rather than silent.

#### Scenario: Probed-unsupported model downgrades without per-request noise
- **WHEN** a request carrying an image tool result resolves to a model catalogued as not supporting structured multimodal output
- **THEN** the tool result takes the string compatibility path
- **AND** no unknown-capability downgrade is reported, because the outcome is the recorded expectation for that model.
