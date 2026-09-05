## 1. Contract and regression evidence

- [x] 1.1 Add a de-identified captured Responses SSE fixture in which one logical message carries different ids on added, content/text delta and done, output-item done, and terminal output events.
- [x] 1.2 Add a contract test that states the observable requirement first, proves all client-facing message lifecycle ids are identical, and fails against the current rolling-id restoration while also asserting every non-id value and non-message identity is unchanged.
- [x] 1.3 Update `docs/pipeline-design.md` before framework code to document stable client-facing message identity as a narrow native-fidelity correction alongside the existing custom-tool correction.

## 2. Native response correction

- [x] 2.1 Extend the native T4 restoration path with a per-request message identity map keyed only after `response.output_item.added` establishes an output index as `type: message`.
- [x] 2.2 Use the added message id as the canonical identity (with a deterministic non-`msg` fallback only when absent) and rewrite that message's later item ids, all mapped top-level `item_id` references, and completed/incomplete terminal output id without changing event count, order, or unrelated JSON values.
- [x] 2.3 Preserve existing custom-tool `ctc` correction and prove reasoning, function/tool, web-search, image, and unknown item identities remain untouched.
- [x] 2.4 Add the second-turn request regression proving an echoed corrected message id is stripped before Copilot while message content, phase, status, and future siblings survive.

## 3. Permanent verification net

- [x] 3.1 Cover multiple message output indexes, commentary/final phases, refusal/annotation or future top-level `item_id` events, completed and incomplete terminals, and a missing/non-message mapping near miss.
- [x] 3.2 Update native response corpus comparison so only enumerated message-id paths are normalized and any other event/value difference still fails.
- [x] 3.3 Mutation-check the new tests: demonstrate the captured lifecycle test fails with message normalization disabled, then restore the implementation and confirm it passes.
- [x] 3.4 Add a `Kind=ClientBehavior` actuator for current Codex with reasoning disabled, a real multi-tool round trip, and a final visible canary so the message start/completion path is exercised without a reasoning-id warning confounder.

## 4. Validation

- [x] 4.1 Run the focused message-id, native-fidelity, request-round-trip, and stream-robustness unit tests.
- [x] 4.2 Run `dotnet test CopilotBridge.slnx --filter "Category!=Integration"` and the Windows Native AOT publish/size check.
- [x] 4.3 Drive the real current `codex.exe` through a bridge subprocess on a non-8765 port; require three matching tool call/output pairs, the final canary exactly once, stable final-message ids in the per-run trace, no message start/completion mismatch, no abort, and zero router/dispatch fatal in the client's own log.
- [x] 4.4 Confirm the production bridge on port 8765 was never stopped, all scratch processes were stopped by verified PID/path, and no test-created credential copy remains outside its source directory.

Validation note: the full non-integration run reported 1,735 passes and one unrelated existing `CredentialServiceMigrationTests.Refresh_does_not_reenter_migration_when_legacy_file_reappears_while_locked` failure (expected 8, actual 9); the same test failed when rerun alone and no auth source/test file is changed. Re-running the remaining suite with that case excluded passed 1,735/1,735. Windows Native AOT publish succeeded at 14,828,032 bytes.
