## Context

The native Codex path restores each clean Copilot Responses event from `NativeResponsesEventLedger`, while T4 also consumes the shared Anthropic-shaped semantic stream to maintain synthesized-terminal state. PR #83 added canonical message identities keyed by native `output_index` and routed generated `response.failed` terminals through the native identity rewrite.

That rewrite still assumes `response.output[]` ordinal equals native `output_index`. The assumption holds for restored native terminals because they retain every output item, but not for a synthesized failure: T4 deliberately drops `redacted_thinking` carriers from `_completedItems`. A native message at index 1 can therefore occupy synthesized terminal slot 0 and miss its canonical id stored under key 1.

The same review exposed a process gap. Copilot can place an actionable finding only in a review body under `Suppressed comments`, with no GraphQL review thread. The existing ship status script counts only unresolved threads and therefore reported a false all-clear for PR #83.

## Goals / Non-Goals

**Goals:**

- Associate every synthesized completed output item with the exact native `output_index` observed on its restored `response.output_item.added` event.
- Normalize a synthesized failed terminal's message ids from that provenance, independently of compacted array position.
- Prove the reasoning-before-message failure sequence through a contract regression and a real Codex retry/tool trajectory.
- Make current-head suppressed Copilot findings a nonzero ship status signal in both mirrored skill trees.

**Non-Goals:**

- Reinsert omitted reasoning items into synthesized failed terminals.
- Change clean restored terminal ordering, successful-stream fidelity, or non-message identities.
- Treat historical review-body findings from superseded commits as open findings.
- Change Codex request identity replay rules.

## Decisions

### 1. Bind native index provenance when the restored added event crosses T4

T4 sees the semantic `content_block_start` before its native carrier is restored. When `RewriteNativeProtocolIds` then processes the corresponding native `response.output_item.added`, it will bind that event's exact `output_index` to the currently open generated block. On block completion, `_completedItems` will retain both the generated JSON and this optional native index.

This avoids adding another bridge-private property to the shared IR and therefore avoids a new cross-client scrub obligation. Inferring provenance from the semantic block index was rejected because it encodes the same positional assumption in a different field; carrying the native event's explicit value is authoritative.

### 2. Use item provenance only for synthesized terminal normalization

Restored native completed/incomplete terminals keep their original full output arrays, so their array ordinals continue to map directly to native output indexes. A synthesized `response.failed` terminal instead resolves each message item's lookup key from its `_completedItems` provenance, falling back to the array ordinal only when no native provenance exists (for non-native/generated streams).

Custom-tool ids remain keyed by `call_id`, so they are unaffected. The terminal JSON shape, item order, error bounding, and usage values remain unchanged.

### 3. Extend the existing deterministic real-client retry case

The deterministic first stalled sampling stream will emit a completed reasoning item at native index 0 and a completed partial message at native index 1 before it stalls. The bridge timeout then synthesizes `response.failed`, after which real Codex must retry, execute the existing custom tool, echo its output, and complete. The verdict will compare the failed terminal message id with the previously streamed added id, then apply the existing trace/stdout/SQLite tool-execution gates.

This reuses the established non-8765 subprocess and retry boundary instead of adding a second nearly identical live test.

The behavior harness is aligned to reviewed Codex 0.153.3: its `additional_tools`
declaration nests `exec` under a namespace, and generated custom-tool code invokes
`tools.exec_command`. Because app-server's SQLite log sink batches asynchronously,
the harness deletes only its isolated temporary thread after the turn; that lifecycle
endpoint explicitly flushes `LogDbLayer` before deletion, guaranteeing the manifest's
dispatch database is non-empty before the process exits.

### 4. Count actionable current-head review bodies separately and fold them into open status

`pr-status.sh` will fetch Copilot review submissions, filter to the PR's current head SHA, and classify bodies containing either a positive `Suppressed comments (N)` count or the `Needs a closer look` heading. It will print `SUPPRESSED_FINDINGS=<n>` and set `OPEN_COMMENTS` to unresolved review threads plus this body-only signal.

Filtering by exact review `commit_id` makes a new pushed fix clear the prior signal while preserving it until Copilot reviews the new head. A failed review query remains fail-loud. Both `.agents` and `.claude` copies will remain semantically identical, and the live merged PR #83 is the regression fixture: its current-head review has one suppressed finding and zero unresolved threads.

## Risks / Trade-offs

- **[Risk] A native added carrier arrives without an open semantic block.** → Leave provenance absent and retain the safe ordinal fallback; no existing native event is mutated by the bookkeeping failure.
- **[Risk] A completed item is associated with the wrong block.** → Bind only while a non-reasoning semantic block is open and clear provenance at every block boundary; regression tests cover reasoning followed by message.
- **[Risk] Review-body wording changes.** → Recognize both the structured suppressed-count phrase and the current heading, expose the count separately, and keep fail-loud behavior for malformed/API failures.
- **[Trade-off] `OPEN_COMMENTS` now includes a body-only signal that has no resolvable thread.** → The signal clears on the next pushed commit because it is current-head scoped; this is preferable to merging an actionable suppressed finding.

## Migration Plan

1. Add the failing contract regression and demonstrate the existing positional implementation fails it.
2. Implement native-index provenance and rerun focused/full non-integration tests.
3. Extend and run the real Codex retry behavior case, then inspect its exact manifest, trace, stdout, and client dispatch log.
4. Harden and live-check both mirrored ship scripts against PR #83.
5. Archive the OpenSpec change before opening the PR, complete the Copilot review loop, squash-merge, and publish the next patch beta.

Rollback removes the provenance bookkeeping and review-body signal. No persisted data or configuration migration is involved.

## Open Questions

None.
