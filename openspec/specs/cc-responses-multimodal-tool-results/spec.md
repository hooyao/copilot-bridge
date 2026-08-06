# cc-responses-multimodal-tool-results Specification

## Purpose
Preserve the modality of Claude Code tool results across the Responses request edge: an image a client tool returned must reach a capable model as an image, not as JSON or base64 text. Defines capability-gated structured `function_call_output` content items, whole-array compatibility fallback, vision signalling, and the IR-driven rule that keeps a provider's own opaque output uninterpreted.
## Requirements
### Requirement: Supported Claude tool-result images retain their modality

For a Claude Code request routed to a Responses target whose exact live-probed model profile supports structured multimodal function outputs, the bridge SHALL translate an Anthropic `tool_result.content` array made entirely of supported text and image blocks into `function_call_output.output` content items in the same order. Anthropic text blocks SHALL become Responses `input_text` items. Anthropic image blocks SHALL become Responses `input_image` items. The bridge MUST preserve the tool-use call id, each text value, and each image source exactly at JSON-value level.

For a base64 image, exact preservation means the emitted data URL contains the same media type and base64 data. For a URL image, it means the emitted `image_url` equals the source URL. The bridge MUST NOT satisfy this requirement by embedding image JSON or base64 in an output string.

#### Scenario: Ordered text and base64 image are structured
- **WHEN** a supported target receives a tool result whose content is text, then a base64 image, then text
- **THEN** its `function_call_output.output` is an array containing `input_text`, `input_image`, and `input_text` in that order
- **AND** the two text values, image media type/base64 data, and `call_id` equal the source values.

#### Scenario: URL image remains the same URL
- **WHEN** a supported target receives a tool result containing an Anthropic URL image block
- **THEN** the matching Responses output item is `input_image` with the exact source URL.

### Requirement: Multimodal function output enables Copilot vision signaling

Whenever the bridge emits an `input_image` inside `function_call_output.output`, it SHALL mark the request as vision-bearing so the existing upstream client sends `Copilot-Vision-Request: true`. A fallback string that merely contains image JSON or base64 MUST NOT by itself set the vision signal.

#### Scenario: Nested tool-result image sets vision
- **WHEN** a supported tool-result image is translated into an `input_image` output item
- **THEN** the request builder returns `Vision=true`
- **AND** the upstream request carries `Copilot-Vision-Request: true`.

### Requirement: Capability is exact and probe-grounded

The bridge SHALL enable structured multimodal function outputs only for exact model profiles whose current Copilot behavior was established by a direct two-turn probe containing `function_call_output.output` content items. It MUST NOT infer this capability from model family, ordinary top-level vision support, or a nearest-profile fallback.

The capability SHALL be three-valued: **Supported** (probed and accepted), **Unsupported** (probed and rejected), and **Unknown** (the exact model is not in the catalog). Only Supported enables structured output; both other states retain the string/verbatim compatibility behavior. A boolean that collapses Unsupported and Unknown MUST NOT be used, because those two states differ in what an operator needs to do about them.

When an image-bearing tool result is downgraded to the compatibility path because the exact model's capability is **Unknown**, the bridge SHALL record that downgrade as an observable event carried out of the request translator and surfaced in the request's audit record. A model rename on the Copilot side therefore appears as an explicit signal rather than as a turn that silently reaches the model without its image. A downgrade for a model probed as **Unsupported** is expected behavior and need not be reported per request. Nor SHALL a downgrade be reported when the SOURCE required its tool output to stay uninterpreted: that string path is what the source asked for and would occur on a supported model too, so attributing it to model capability would be false.

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

### Requirement: Unsupported arrays fail safe without partial loss

The bridge SHALL use structured output only when every block in the image-bearing array is a recognized, valid Anthropic text or image block. If any block is unsupported or malformed, the bridge SHALL apply the established fallback to the complete array rather than partially translating supported blocks and dropping or reordering the rest. Text-only arrays, scalar strings, objects, and native Codex output SHALL retain their established behavior.

#### Scenario: Unknown sibling block triggers whole-array fallback
- **WHEN** an image-bearing tool-result array also contains an unsupported content-block type
- **THEN** the bridge does not emit a partial structured array
- **AND** every source block remains represented through the compatibility fallback.

#### Scenario: Text-only array retains newline flattening
- **WHEN** a tool-result array contains only recognized text-like blocks
- **THEN** the output remains a newline-separated string with the existing empty-block separator behavior.

### Requirement: Real-client evidence proves semantic image delivery

Acceptance SHALL include a captured Claude Code request replay and a real headless Claude Code CC→gpt run on an image-returning tool path. The captured replay SHALL assert the exact structured upstream shape and vision signal. The real-client verdict SHALL require the client transcript to show the image-returning tool use/result and a final answer that correctly identifies a generated known image, cross-checked with the exact bridge trace. An upstream HTTP 200 alone is insufficient.

#### Scenario: Claude Code identifies a generated image through the bridge
- **WHEN** real Claude Code reads a generated solid-color PNG through the CC→`gpt-5.6-sol` route
- **THEN** its transcript contains the actual `Read` tool-use and image result
- **AND** the exact upstream replay contains an `input_image` function-output item with vision enabled
- **AND** the final client turn correctly identifies the generated color without bridge marker leakage.

