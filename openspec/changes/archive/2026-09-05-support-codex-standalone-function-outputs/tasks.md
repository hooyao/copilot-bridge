## 1. Backend and Client Contract

- [x] 1.1 Add and run a live Copilot ApiContract probe for a minimal standalone named `function_call_output`, recording whether the native shape is accepted without a `call_id`.
- [x] 1.2 Replay a sanitized request derived from the captured Codex Desktop 0.153.3 heartbeat bytes and confirm the same backend result with realistic headers/history shape.
- [x] 1.3 Record the Codex #39782 source contract and the live Copilot finding in the design/protocol documentation before implementation policy is finalized.

## 2. Contract-First Regression Tests

- [x] 2.1 Add a focused unit fixture/test proving paired outputs retain their existing path while standalone named outputs deserialize and round-trip with absent `call_id`, exact values, future siblings, and order.
- [x] 2.2 Add a negative contract test requiring an actionable rejection when both `call_id` and `name` are absent.
- [x] 2.3 Add the standalone shape to the inbound corpus replay and mutation-check that removing its special branch reproduces a red test.

## 3. Request Pipeline Implementation

- [x] 3.1 Update the source-generated Responses DTO contract so `function_call_output` mirrors Codex 0.153.3 optional `call_id`, `name`, and `namespace` fields with one explicit paired-or-named invariant.
- [x] 3.2 Keep paired outputs on the semantic IR tool-result path and carry standalone named outputs through the existing ordered OpenAI provider passthrough.
- [x] 3.3 Ensure T2 re-emits standalone outputs unchanged, does not synthesize a call/message, and preserves the paired-output behavior and cross-client isolation.

## 4. Documentation and Verification

- [x] 4.1 Update `docs/pipeline-design.md` and `docs/codex-protocol-research.md` with the standalone-output contract, authority semantics, validation, and backend evidence.
- [x] 4.2 Run focused tests, the full non-integration solution suite, `AgentRepositoryCompatibilityTests`, and relevant ApiContract tests. PASS: 1739 non-integration tests, 14 standalone backend probes, 4 repository-compatibility tests.
- [x] 4.3 Add and run a real Codex app-server ClientBehavior case using `thread/inject_items`, then judge its trace, stdout, and `logs_2.sqlite` under the real-client-verification rubric. PASS: manifest `codex-standalone-named-function-output-20260905-164042-528.json`; two custom-exec call/output loops, injected-only canary, router/ERROR/retry rows all zero.
- [x] 4.4 Reconcile every task with actual evidence and prepare only this change's paths for OpenSpec archive/sync and the current PR.
