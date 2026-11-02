# Verification

## Result

PASS for the output-headroom catalog policy. Only prevention item 1 is implemented. Reasoning-output guards and failed-stream history recovery remain deferred. The installed bridge and global Codex configuration were not changed.

## Policy and contract tests

- Full unit suite: 1,966 passed.
- Final focused catalog/endpoint/fallback suite after mutation restoration: 91 passed.
- Captured live-model snapshot contract: passed.
- OpenSpec validation and `git diff --check`: passed.
- Added contract cases were red against the original implementation. Disabling only the new growth ceiling made 6 of 12 policy cases fail; restoring it made the suite green. Cases also cover whole-thousand rounding, the 1,000-token representability boundary, output-heavy small contexts, percentage ceilings, resolved aliases, unchanged capacities, invalid metadata, and 32-bit upper limits with 64-bit arithmetic.

## Real Codex automatic compaction

Client: `codex-cli 0.166.0-alpha.5.1`, isolated app-server and bridge subprocess, deterministic upstream, no local context or compaction override.

Final manifest: `tests/behavior-runs/manifests/codex-catalog-output-headroom-20261102-062510-813.json`.

- Actual catalog: total/max context 1,050,000; auto-compaction threshold 778,000.
- Controlled upstream seed: 800,000 input + 20 output tokens, between the new and previous thresholds. These are test stimuli, not a claim about a real 800k-token prompt or Copilot's boundary behavior.
- The next request was compaction with `trigger=auto`, `reason=context_limit`, and `phase=pre_turn`; there were no context-window rejections.
- Client stdout recorded a completed `contextCompaction` item and two actual `commandExecution` items, both exit 0. The read returned `catalog_headroom_compact_canary_1102`.
- The subsequent upstream request carried the matching `custom_tool_call_output` for `call_codex_context_recovery_exec`, with successful write/read results.
- The manifest-selected client SQLite log contained 113 rows in the bounded run window, zero router/dispatch fatals, zero ERROR rows, and zero retry rows. The digest is adjacent to that manifest's dispatch database.

Counterfactual manifest: `tests/behavior-runs/manifests/codex-catalog-output-headroom-20261102-062354-780.json`. With the growth ceiling temporarily disabled, the actual catalog returned 892,000 and the otherwise identical real-client run produced zero compaction requests. Its semantic verdict is FAIL for the required early-compaction behavior even though the thin actuator completed. Product source and build outputs were restored afterward; the final manifest above reconfirmed automatic compaction.

## Live Copilot task

Manifest: `tests/behavior-runs/manifests/codex-gpt61-output-headroom-live-20261102-062130-720.json`.

The same real client fetched the bridge catalog and called live `gpt-6.1-sol` through an isolated bridge. Five custom-exec calls created input records, calculated a weighted sum, independently checked 112, wrote a receipt, and read both outputs. All five client `commandExecution` items completed with exit 0; all five call IDs had matching outputs on subsequent requests. The final client message reported 112 and the exact receipt. The isolated SQLite dispatch log contained 235 rows, zero router/dispatch fatals, zero ERROR rows, and zero retry rows.

Authentication used the existing test helper to stage a disposable, non-refreshable GitHub CLI credential; no installed refresh-bearing credential was copied or rotated.

## Limits

This verifies catalog-driven compaction and continued real tool execution, plus ordinary live-Copilot compatibility. It does not repair an already oversized history, bound abnormal reasoning output, or establish new backend token limits. The 16,000-token margin is bridge policy; it is not reported as an upstream capability.
