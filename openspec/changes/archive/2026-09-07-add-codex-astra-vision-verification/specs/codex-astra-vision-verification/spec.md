## ADDED Requirements

### Requirement: Native Codex image attachment verification

The behavior harness SHALL drive the real Codex app-server with a native local-image input through the stock `gpt-5.6-sol -> gpt-6-astra` route. The task SHALL require interpretation of the image, a native file-editing tool call and a separate shell read carrying that interpretation, and a final answer.

#### Scenario: Attached image reaches Astra and is understood

- **WHEN** the attachment case runs with live Copilot credentials and the reviewed Codex binary
- **THEN** the first inference request SHALL contain the image supplied by Codex, the upstream model SHALL be `gpt-6-astra`, and the recorded client evidence SHALL allow the verifier to compare the written and final answers with the image's expected content

### Requirement: Native Codex image tool verification

The behavior harness SHALL drive a separate real Codex task whose initial input contains only text and which obtains the image through an actual `view_image` tool call. The task SHALL then persist and read back the visual answer with separate tool calls.

#### Scenario: Tool-produced image completes the client loop

- **WHEN** the image-tool case runs
- **THEN** the client SHALL execute `view_image`, a subsequent model request SHALL contain its image, and the trace SHALL contain matching tool calls and outputs for the completed task through Astra

### Requirement: Independent visual evidence and client verdict

Each case SHALL generate a fresh image with randomized color positions and a numeric code encoded only in its pixels. Expected content SHALL NOT appear in the client prompt or source filename. The harness SHALL preserve the image, digest, expected answer, observed file output, client transcript, exact dispatch database window, and bridge trace for the verifier.

#### Scenario: Complete evidence supports a PASS

- **WHEN** the verifier evaluates a completed run
- **THEN** PASS SHALL require correct image-derived content in the client final answer and written output, matching tool outputs, the intended image-delivery path, upstream `copilot-vision-request=true` on image requests, Astra routing, no execution abort, and a nonempty manifest-selected SQLite window with zero router or dispatch fatals

#### Scenario: Transport success does not establish vision success

- **WHEN** xUnit passes or the bridge returns HTTP 200 but an image, correct answer, tool round-trip, or usable client log is missing
- **THEN** the run SHALL NOT be reported as a verified visual PASS

### Requirement: Native tool-output images activate the vision flag

When a native Responses `function_call_output` or `custom_tool_call_output` contains an `input_image` in its output content array, the request builder SHALL activate the existing Copilot vision header without changing the output value, its ordering, or unrelated fields. This SHALL include standalone named outputs without a call ID.

#### Scenario: Opaque output carries an actual native image

- **WHEN** an image-bearing native tool output passes through the Codex-to-Responses pipeline
- **THEN** the image and adjacent output content SHALL be preserved and `Copilot-Vision-Request` SHALL be `true`

#### Scenario: Text merely describes an image shape

- **WHEN** a native tool output contains image-shaped JSON only as text or unrelated nested metadata
- **THEN** it SHALL remain opaque and SHALL NOT activate the vision flag
