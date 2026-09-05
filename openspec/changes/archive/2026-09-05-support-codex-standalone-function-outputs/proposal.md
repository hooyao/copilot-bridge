## Why

Codex Desktop 0.153.3 deliberately emits standalone named `function_call_output`
items for externally injected tool events such as thread heartbeats. These items have
no preceding model tool call and therefore no `call_id`; bridge v0.5.14 still models
every function output as paired and rejects the request before T1, making every
heartbeat and later compaction of the affected thread fail with HTTP 400.

## What Changes

- Accept both Codex function-output variants: a paired output identified by
  `call_id`, and a standalone output identified by a non-empty `name` with optional
  `namespace`.
- Preserve standalone outputs' absent `call_id`, name, namespace, output value,
  provider metadata, and input position through the shared IR and Responses
  destination without inventing a model call or lowering tool-tier authority.
- Reject a function output that has neither a usable `call_id` nor a non-empty name
  with an actionable client error.
- Ground the destination behavior in a live Copilot probe using the captured Codex
  0.153.3 shape, then guard the accepted behavior with contract-first unit tests,
  production-corpus replay, and a real Codex Desktop heartbeat/continuation run.
- Update the durable Codex protocol and pipeline documentation with the standalone
  output contract and its source provenance (`openai/codex` #39782).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-request-fidelity`: Extend native request carriage from open item types and
  future sibling fields to the two valid shapes of the already-modeled
  `function_call_output` discriminator, including standalone named external-tool
  events without a `call_id`.

## Impact

- Request DTOs and source-generated JSON metadata under
  `src/CopilotBridge.Cli/Models/Responses/`.
- Codex T1/T2 request translation and opaque provider-extension carriage.
- Request diagnostics, captured-body contract tests, corpus replay, and real-client
  behavior coverage.
- `docs/codex-protocol-research.md` and `docs/pipeline-design.md`.
