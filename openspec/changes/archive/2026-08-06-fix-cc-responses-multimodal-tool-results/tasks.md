## 1. Freeze the Behavioral Contract

- [x] 1.1 Add a unit contract test for a Claude-originated text/image/text tool result targeting a proven-capable profile; require ordered `input_text`/`input_image` items, exact data URL, unchanged call id, and `Vision=true`.
- [x] 1.2 Run the test against the current implementation and record that it fails because output is a string and vision is false; mutation-check the final test by restoring the old flattening behavior and requiring the same failure.
- [x] 1.3 Add fallback contract cases for text-only arrays, malformed/unknown sibling blocks, scalar/object outputs, unprobed exact models, and a fuzzy nearest-profile model that must not inherit the positive capability.

## 2. Implement Capability-Safe Translation

- [x] 2.1 Add the exact, live-probe-grounded structured multimodal function-output capability to `CodexModelProfile`; initially enable only `gpt-5.6-sol`.
- [x] 2.2 Translate an all-supported Claude tool-result array containing at least one image into ordered Responses `input_text`/`input_image` items while preserving media type/data or URL and call id.
- [x] 2.3 Set the existing vision flag only when an `input_image` is actually emitted; retain the complete-array compatibility fallback for malformed/unsupported blocks and all legacy scalar/text/native-Codex paths.
- [x] 2.4 Update comments and durable model-profile evidence so they describe the string-or-content-items contract and exact capability boundary without extrapolating to sibling models.
- [x] 2.5 Keep the translator free of client identity: mark opaque provider output at the Codex source (`opaque_tool_output`) and have T2 pull that fact from the IR, with a test asserting identical JSON diverges only by that mark.

## 3. Permanent Regression Coverage

- [x] 3.1 Make `ContentConservationTests` inventory function-output text/image modalities and values independently so base64 embedded in a string fails conservation.
- [x] 3.2 Add a de-identified captured-byte ApiContract replay from the real Claude Code `Read` image-result shape; assert exact upstream structured output, vision header/signal, ordering, and call identity.
- [x] 3.3 Run focused request-builder/image/conservation/ApiContract suites and the full unit and non-integration suites.

## 4. Live Capability and Real-Client Acceptance

- [x] 4.1 Retain or add a permanent direct two-turn `gpt-5.6-sol` capability probe that submits structured image function output and proves semantic image understanding, not merely HTTP acceptance.
- [x] 4.2 Leave every additional model disabled unless it is individually probed; this change probes/enables only `gpt-5.6-sol` and explicitly records all sibling/unprobed rows as false without family extrapolation.
- [x] 4.3 Add and run a `Kind=ClientBehavior` CC→gpt case in a disposable directory where real Claude Code reads a generated solid-color PNG and reports its color.
- [x] 4.4 Use `real-client-verify` on only this run's manifest; require transcript `Read` tool-use/result, correct final color, exact structured trace with vision signaling, second-turn completion, and no bridge marker leak.

## 5. Build and Documentation Gates

- [x] 5.1 Update relevant durable protocol/design documentation with the proven structured function-output capability and compatibility fallback.
- [x] 5.2 Publish both Windows Native AOT executables with zero trim/AOT warnings and record sizes/results.
- [x] 5.3 Reconcile the OpenSpec artifacts and verification evidence, then run strict validation; do not commit, push, archive, or publish externally unless requested.

## Verification Evidence

- Pre-fix contract failure: `ClaudeToolResult_TextImageText_PreservesStructuredOutputAndSetsVision` saw a scalar JSON value instead of `JsonArray`; the independent conservation test later reported `items:text/image` versus `scalar:...image JSON` under the flattening mutation.
- Mutation check: forcing `structuredMultimodalOutput=false` made both the exact-shape image test and modality-aware conservation test fail; restoring the branch made them green.
- Focused unit gates: request-builder/image/fallback/native-Codex/count/conservation suites passed, including explicit `/cc` provenance and opaque `/codex` output.
- Full unit suite: `dotnet test tests/CopilotBridge.UnitTests --no-restore` → 1,497 passed.
- Solution non-integration gate: `dotnet test CopilotBridge.slnx --filter "Category!=Integration" --no-restore` → 1,497 passed; Playground has no non-integration cases.
- Permanent direct capability probe: `Gpt56Sol_StructuredImageFunctionOutput_IsAcceptedAndUnderstood` → first 200, second 200, answer `Red`.
- Captured-shape ApiContract after the final provenance fix: `CapturedClaudeImageToolResult_BecomesStructuredResponsesOutput` → client 200/upstream 200; exact call id, ordered text/image, exact generated PNG data URL, and traced `copilot-vision-request=true`.
- Final real-client manifest: `cc-to-gpt-image-tool-result-20260806-033949-024.json`; real Claude Code 2.1.220 executed `Read`, recorded the image tool result, completed two turns, and returned `red`. Exact trace `serve-d1992728c59d49dcbc58638bb84b2475` contains the matching `function_call`/structured `function_call_output`, exact image data URL, vision header, three upstream 200s, and zero bridge marker leaks.
- Native AOT: clean Release/RID rebuild generated native code for both executables with zero trim/AOT warnings. `copilot-bridge.exe` = 14,099,968 B (mtime advanced to 2026-08-06T03:48:10.5989590Z); `copilot-updater.exe` = 5,019,136 B (mtime advanced to 2026-08-06T03:48:26.6105885Z).
- `openspec validate fix-cc-responses-multimodal-tool-results --strict` passed. No commit, push, archive, or external publication was performed.
