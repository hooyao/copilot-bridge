## Context

The shared request IR models Anthropic `ToolResultBlockParam.Content` as an opaque `JsonElement` because the protocol permits either a string or an array of content blocks. `ResponsesRequestBuilder` currently treats every array as text: recognized text blocks contribute their text, while images and other blocks contribute compact JSON. That rule was grounded in live gpt-5.5 rejection of Anthropic-shaped output arrays and correctly protects native Codex round trips from JSON-wrapper injection, but it conflates two different target shapes.

The official Responses function-output body is `string | FunctionCallOutputContentItem[]`; the structured array supports `input_text` and `input_image`. A direct live two-turn probe proved that current Copilot `gpt-5.6-sol` accepts this shape, semantically consumes a generated red PNG, and answers `red`. The deployed bridge nevertheless sent the real Claude Code image result as a large JSON/base64 string with `vision=false`.

Constraints:

- Model capabilities are live-probed facts, never family-name guesses.
- The native Codex T1→T2 round trip must remain semantically unchanged.
- Native Anthropic `/cc` passthrough must remain byte-neutral.
- JSON remains explicit/AOT-safe; no reflection serialization or new dependency.
- A bridge/upstream 200 is not acceptance: the real Claude Code transcript must prove that the model understood the image after the client executed the image-returning tool.

## Goals / Non-Goals

**Goals:**

- Preserve ordered text and image modalities for Claude-originated tool results when the selected Responses model is proven to accept structured multimodal function output.
- Preserve exact image data URLs (media type plus base64 bytes, or source URL), text values, ordering, and `call_id`.
- Set the existing `Vision` signal when an emitted function output contains an image.
- Keep scalar and native-Codex output behavior unchanged.
- Fail safe on unknown block types and models whose capability is unknown.
- Permanently guard the real captured Claude Code shape and validate the actual client/tool/model loop.

**Non-Goals:**

- Encrypted reasoning carriage, full JSON-Schema preservation, prompt-cache tuning, or WebSearch support.
- Claiming support for gpt-5.5, other gpt-5.6 slots, or future models without direct live evidence.
- Translating documents, MCP resources, encrypted output content, or arbitrary future Anthropic tool-result variants in this change.
- Changing the Anthropic request DTO or normal text/image message translation.

## Decisions

### 1. Add an explicit per-model structured-function-output capability

`CodexModelProfile` will carry a boolean for structured multimodal function outputs. Only rows established by a direct two-turn function-call-output probe will enable it. The initial proven row is `gpt-5.6-sol`; every other current row remains false until probed.

The builder already resolves an exact or nearest profile. A nearest-profile fallback MUST NOT inherit this positive capability, because that would extrapolate support to an unprobed model. Capability selection therefore uses only the exact profile for this field, while existing effort/custom-tool behavior may continue using the nearest profile.

Alternative: enable by model prefix or ordinary `Vision` catalog metadata. Rejected because top-level `input_image` acceptance does not prove `function_call_output.output` array acceptance, and sibling models have repeatedly differed on cross-field constraints.

Alternative: emit structured output for every Responses model and rely on backend 400s. Rejected because a false positive breaks the entire tool loop; the old string fallback is usable and explicit.

### 2. Distinguish Claude multimodal arrays from native Codex output

The structured branch is entered only when all of these hold:

1. the target's exact profile enables structured multimodal function output;
2. the tool-result content is an array;
3. the array contains at least one recognized Anthropic `image` block;
4. every block is a supported Anthropic `text` or `image` block with a valid required shape.

It then emits a Responses content-item array in the same order:

- Anthropic `text` → Responses `input_text` with identical text;
- Anthropic base64 `image` → Responses `input_image` with an exact data URL;
- Anthropic URL `image` → Responses `input_image` with the exact URL.

A text-only array keeps the established newline-flattened string contract. An array with any unsupported or malformed block falls back as a whole to the established string/compact-JSON representation rather than partially translating it and silently dropping content. Scalar/object outputs remain verbatim.

Alternative: deserialize blocks through the polymorphic Anthropic DTOs. Rejected because `ToolResultBlockParam.Content` intentionally remains opaque and explicit `JsonElement` inspection is smaller, AOT-safe, and avoids changing unrelated IR behavior.

Alternative: structure every array and preserve unknown blocks verbatim inside it. Rejected because the Responses content-item union is closed enough that arbitrary Anthropic block objects can produce a backend 400.

### 3. Vision signaling is an output of successful structured emission

`WriteToolResultOutput` will receive the capability and a `ref vision`. It sets `vision=true` only when it actually writes an `input_image` item. This reuses the existing builder return value, strategy call, and `CopilotHeaderFactory` path; no separate header code is needed.

This keeps the signal honest: a fallback JSON string containing base64 must not claim vision, while a nested tool-result image must claim it exactly like a top-level image.

### 4. Tests derive modality from the protocol contract

The first unit test constructs a Claude-shaped `MessagesRequest` with a tool call followed by text+image tool-result content and targets a test profile whose capability is true. It asserts the exact ordered output array, exact data URL, unchanged `call_id`, and `Vision=true`. It is expected to fail on the pre-change implementation because output is a string and vision is false.

Content conservation will compare structured function-output items by modality and value rather than accepting base64 embedded in text as conservation. Existing native-Codex text-array tests remain unchanged.

The ApiContract test will replay a de-identified/minimized real Claude Code request shape containing a real `Read` image result and inspect the exact upstream body/header. The live ClientBehavior case will create a known solid-color PNG in a disposable directory, require real Claude Code to call `Read`, and require the final answer to identify the color. The verdict will use the stream-json transcript plus exact trace, not HTTP status.

## Risks / Trade-offs

- **[Risk] A model accepts top-level images but rejects structured function output.** → Keep a separate exact-profile capability and probe the two-turn function-output shape directly.
- **[Risk] Nearest-model matching accidentally enables an unprobed model.** → Never inherit the positive capability from `GetNearest`; unknown/new models use the string fallback.
- **[Risk] Partial conversion drops an unknown tool-result block.** → Structure only all-supported arrays; otherwise fall back for the complete array.
- **[Risk] The data URL is altered or double-encoded.** → Write from source media type/data directly and assert exact string equality against captured/generated bytes.
- **[Risk] Existing native Codex output changes.** → Gate on the Claude image block shape plus exact capability and retain native round-trip/content-conservation regression tests.
- **[Trade-off] Unprobed models continue receiving a textual image representation.** → This is an explicit safe fallback, not a completeness claim; each model can be enabled after live proof.

## Migration Plan

1. Freeze the contract with a failing unit test and record the old failure.
2. Add the exact-profile capability and implementation.
3. Add modality-aware conservation and captured-byte regression coverage.
4. Run focused and full tests, then live capability/ApiContract probes.
5. Run the real Claude Code CC→gpt image-tool behavior case and render the verdict from client evidence.
6. Publish both Windows Native AOT executables and record results.

Rollback is a code revert; no configuration or persisted data changes exist. The fallback remains the previous behavior.

## Open Questions

- Which additional currently served Responses models accept and semantically consume structured multimodal function output? Resolve only through the same two-turn live probe before enabling each row.
- Do URL-sourced images inside Claude Code tool results occur in real traffic? The writer can preserve them by contract, but acceptance should be probe-backed before claiming live coverage.
