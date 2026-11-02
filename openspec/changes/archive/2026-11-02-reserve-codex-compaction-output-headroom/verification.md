# Verification

## Result

PASS for the output-headroom catalog policy. Only prevention item 1 is implemented. Reasoning-output guards and failed-stream history recovery remain deferred. The installed bridge and global Codex configuration were not changed.

## Policy and contract tests

- Full unit suite: 1,966 passed.
- Final focused catalog/endpoint/fallback suite after mutation restoration: 91 passed.
- Captured live-model snapshot contract: passed.
- Pinned Codex 0.144.1 catalog consumer: passed with 12 remote entries, preserved instructions/capacities, and the 778,000 threshold persisted in the real client's cache. The official release archive's SHA-256 was checked before running the isolated binary.
- OpenSpec validation and `git diff --check`: passed.
- Added contract cases were red against the original implementation. Disabling only the new growth ceiling made 6 of 12 policy cases fail; restoring it made the suite green. Cases also cover whole-thousand rounding, the 1,000-token representability boundary, output-heavy small contexts, percentage ceilings, resolved aliases, unchanged capacities, invalid metadata, and 32-bit upper limits with 64-bit arithmetic.

## Real Codex automatic compaction

Client: `codex-cli 0.166.0-alpha.5.1`, isolated app-server and bridge subprocess, deterministic upstream, no local context or compaction override.

Final manifest: `tests/behavior-runs/manifests/codex-catalog-output-headroom-20261102-065343-890.json`.

- Actual catalog: total/max context 1,050,000; auto-compaction threshold 778,000.
- The catalog snapshot is stored separately at `catalog/models.json`, outside the top-level audit-file health check. Before starting Codex, the actuator confirms that metadata capture alone produced no top-level JSON audit. The completed run produced 16 actual per-request audit files.
- Controlled upstream seed: 800,000 input + 20 output tokens, between the new and previous thresholds. These are test stimuli, not a claim about a real 800k-token prompt or Copilot's boundary behavior.
- The next request was compaction with `trigger=auto`, `reason=context_limit`, and `phase=pre_turn`; there were no context-window rejections.
- Client stdout recorded a completed `contextCompaction` item and two actual `commandExecution` items, both exit 0. The read returned `catalog_headroom_compact_canary_1102`.
- The subsequent upstream request carried the matching `custom_tool_call_output` for `call_codex_context_recovery_exec`, with successful write/read results.
- The manifest-selected client SQLite log contained 122 rows in the bounded run window, zero router/dispatch fatals, zero ERROR rows, and zero retry rows. The digest is adjacent to that manifest's dispatch database.

Counterfactual manifest: `tests/behavior-runs/manifests/codex-catalog-output-headroom-20261102-062354-780.json`. With the growth ceiling temporarily disabled, the actual catalog returned 892,000 and the otherwise identical real-client run produced zero compaction requests. Its semantic verdict is FAIL for the required early-compaction behavior even though the thin actuator completed. Product source and build outputs were restored afterward; the final manifest above reconfirmed automatic compaction.

## Live Copilot task

Manifest: `tests/behavior-runs/manifests/codex-gpt61-output-headroom-live-20261102-062130-720.json`.

The same real client fetched the bridge catalog and called live `gpt-6.1-sol` through an isolated bridge. Five custom-exec calls created input records, calculated a weighted sum, independently checked 112, wrote a receipt, and read both outputs. All five client `commandExecution` items completed with exit 0; all five call IDs had matching outputs on subsequent requests. The final client message reported 112 and the exact receipt. The isolated SQLite dispatch log contained 235 rows, zero router/dispatch fatals, zero ERROR rows, and zero retry rows.

Authentication used the existing test helper to stage a disposable, non-refreshable GitHub CLI credential; no installed refresh-bearing credential was copied or rotated.

## Configuration-dependent routing assessment

PR #98's third review requested assessment of configuration-dependent model-routing limits without identifying an additional code defect. The new ceiling consumes the validated limits of the already-resolved target; it changes neither location matching nor model-availability rules. The targeted recheck passed 28 cases covering routing, capacity validation, and output-growth boundaries:

- `ExactAliasedResponsesTargetKeepsSourceInstructionsAndUsesTargetLimits` and `ConfiguredModelLocationProjectsResolvedAstraLimitsUnderGpt56Slug` preserve source instructions and use the actual target's capacities, including the revised 728,000 threshold for the coherent 1,000,000/872,000/128,000 fixture.
- `EarlierRequestDependentRouteHidesAliasInsteadOfAdvertisingLaterFallbackLimits` rejects ambiguous capacity projection when an earlier effort-dependent location may select a smaller target. The projector does not advertise the later fixed target's larger capacity in that case.
- `CrossModelAliasWithUnmappableTargetLimitsIsHidden` and `ConfiguredModelLocationHidesAliasWhenValidatedAstraIsAbsent` preserve the existing validated-alias availability guards. Direct models with invalid capacities retain their reviewed limits; no invalid live capability becomes a capacity uplift.
- `ConfiguredModelLocationKeepsReviewedBaselineWhenOverlayIsUnavailable` preserves the existing degraded-baseline behavior.
- The output-growth contract cases retain the existing positive percentage policy when a full output reserve is not representable. The additional ceiling therefore does not invalidate coherent capacities or change alias visibility merely because maximum output is large.

This assessment found no additional routing change needed for the compaction policy. Runtime request-dependent routing remains governed by the existing conservative catalog contract.

## Limits

This verifies catalog-driven compaction and continued real tool execution, plus ordinary live-Copilot compatibility. It does not repair an already oversized history, bound abnormal reasoning output, or establish new backend token limits. The 16,000-token margin is bridge policy; it is not reported as an upstream capability.
