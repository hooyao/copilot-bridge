# Verification — Native Codex context recovery

Date: 2026-08-24

## Contract-first evidence

- The catalog test failed before implementation with expected `892000`, actual `898000`.
- The endpoint test failed before implementation with expected HTTP 200, actual HTTP 400.
- Mutating the total-context policy back to 90% made the 85% contract fail.
- Removing the exact message predicate made the altered-message near miss fail because it was incorrectly adapted.
- Focused classifier, endpoint, catalog, and existing Claude Code context-rewriter tests pass after restoring the implementation.

## Captured-byte regression

`CodexContextRecoveryContractTests.MinimizedProductionCompactionCapture_EmitsRecoverableNativeFailure` passes against the de-identified production-shape fixture. The fixture retains streaming native Codex, automatic pre-turn compaction metadata, reasoning/tool history, and the exact Copilot context error without retaining user content.

## Real Codex path-specific verdict

- Client: real Codex app-server `0.148.0-alpha.15`
- Model: `gpt-5.6-sol`
- Case: `Codex_ContextWindow400_TrimsCompactionAndCompletesToolTurn_ForVerdict`
- Manifest: `tests/behavior-runs/manifests/codex-native-context-recovery-20260824-084420-487.json`

Evidence:

- raw `upstream-resp`: HTTP 400 with the exact Copilot `invalid_request_body` context message;
- client-facing `inbound-resp`: HTTP 200 `text/event-stream`, exactly one injected `response.failed`, `error.code=context_length_exceeded`, and no `X-Reasoning-Included`;
- Codex emitted two compaction requests; the retry reduced retained input from 9 items to 8;
- the retry received a compact summary and normal work resumed;
- trace request 4 returned `custom_tool_call(call_codex_context_recovery_exec)` and request 5 carried the matching `custom_tool_call_output`;
- Codex's own app-server stdout records both nested PowerShell commands completed with exit code 0, the read output contains `codex-context-recovery-canary-64082`, and the turn completed;
- the SQLite digest scanned 174 rows with zero router/dispatch fatals and no retry exhaustion;
- the sole ERROR row is the expected recovery action from `codex_core::compact`: context exceeded while compacting, removing the oldest history item.

Verdict: **PASS** for the native context-error adaptation and client-owned compact/retry recovery path.

## Real Codex live-Copilot regression verdict

- Case: `Codex_MultiStepToolChain_ProducesDispatchLogForVerdict`
- Manifest: `tests/behavior-runs/manifests/codex-multistep-toolchain-20260824-085111-891.json`
- Client: real Codex app-server `0.148.0-alpha.15`
- Model: live Copilot `gpt-5.6-sol`

The disposable Debug bridge ran on a random high port and used the already-running
loopback bridge on 8765 solely as its authenticated Copilot upstream because the
test-only legacy credential mirror had expired without a refresh token. The temporary
harness override was restored immediately after the run; no product or test behavior
retains that route.

Evidence:

- the per-run trace contains a live `custom_tool_call(exec)` and the next request carries its matching `custom_tool_call_output`;
- Codex executed the nested three-command task, every command completed with exit code 0, and the read output contained `codex-behavior-canary-51742`;
- the final agent message contains the exact canary and the turn completed;
- Codex's SQLite digest scanned 204 rows with zero router/dispatch fatals, zero ERROR rows, and zero retry rows;
- stdout contains no execution-abort signature.

Verdict: **PASS** for ordinary live-Copilot tool execution through the changed bridge.

## Offline and catalog validation

- focused context classifier/endpoint/catalog tests: PASS;
- minimized production-shape ApiContract replay: 1/1 PASS;
- full unit suite: 1,678/1,678 PASS;
- solution tests with `Category!=Integration`: 1,678/1,678 PASS; Playground correctly had no matching non-Integration tests;
- `AgentRepositoryCompatibilityTests`: 4/4 PASS after updating both real-client skill mirrors;
- captured Copilot catalog snapshot contract: 1/1 PASS;
- official-release Codex 0.144.1 pinned consumer: PASS, consuming 8 complete endpoint entries and observing `auto_compact_token_limit=892000` through the real Rust catalog parser;
- `dotnet build CopilotBridge.slnx`: PASS with 0 warnings and 0 errors;
- `git diff --check`: PASS;
- strict OpenSpec validation: PASS.
