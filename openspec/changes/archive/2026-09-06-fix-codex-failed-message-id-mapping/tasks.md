## 1. Contract and regression

- [x] 1.1 Update `docs/pipeline-design.md` to state that synthesized terminal message identity is keyed by native output provenance, not compacted array position.
- [x] 1.2 Add the reasoning-index-0, message-index-1, failed-terminal regression and demonstrate that it fails against the positional implementation.

## 2. Native terminal implementation

- [x] 2.1 Bind each open semantic output block to the exact native `output_index` from its restored added event and retain that provenance with completed synthesized items.
- [x] 2.2 Normalize synthesized failed-terminal message ids using retained native provenance while leaving restored native terminals and non-message identities unchanged.

## 3. Review-loop hardening

- [x] 3.1 Update both mirrored `ship-pr` status scripts to fail loudly on their review query, report current-head suppressed Copilot findings, and include them in `OPEN_COMMENTS`.
- [x] 3.2 Run the repository compatibility tests and live-check the new status signal against merged PR #83's current-head suppressed finding.

## 4. Permanent and real-client verification

- [x] 4.1 Extend the deterministic Codex retry behavior case so its failed prefix contains reasoning at native index 0 and a completed message at native index 1.
- [x] 4.2 Run focused identity/fidelity/timeout tests and the full non-integration solution suite.
- [x] 4.3 Run the real current Codex client through the non-8765 retry scenario and verify the failed-prefix identity, retry/tool-output round trip, final canary, no abort, and zero client router/dispatch fatals from the exact run manifest.
- [x] 4.4 Run the Windows Native AOT publish check, confirm the production bridge on port 8765 was untouched, and record validation evidence before archive.

## Validation evidence

- The new contract regression failed before the product fix with expected `opaque-after-reasoning-added` versus actual `item_0`, then the complete message-id suite passed 10/10 after native-index provenance was implemented.
- Focused identity, native-fidelity, stream-robustness, and repository-compatibility tests passed 49/49. The full non-integration solution run passed 1,745/1,745.
- Real Codex 0.153.3 B5 PASS manifest: `codex-native-retryable-stream-timeout-20260906-034708-527.json`. The per-run trace shows reasoning index 0, message index 1, and the sole failed-terminal output at slot 0 all retaining `opaque-retry-prefix-added`; no `item_0` id is present. Codex then completed two command executions, echoed the matching `custom_tool_call_output`, and emitted the final canary with no abort signature. Its explicitly flushed SQLite window contains 114 rows, zero router/dispatch fatals, and zero ERROR rows.
- The hardened status script changed live merged PR #83 from `OPEN_COMMENTS=0` to `OPEN_COMMENTS=1` with `SUPPRESSED_FINDINGS=1`, while both skill mirrors pass compatibility checks.
- Windows Native AOT publish succeeded at 14,837,760 bytes. The production listener remained PID 18304 at `C:\Users\yahu2\Desktop\copilot-bridge\copilot-bridge.exe` before and after verification; all tests used free high ports.
