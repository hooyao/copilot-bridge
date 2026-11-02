# Tasks

## 1. Compaction policy and contract

- [x] 1.1 Update the pipeline contract and capacity references with the output-growth reserve and small-window fallback; verify documented examples match the contract.
- [x] 1.2 Add contract tests for output reserve, rounding, percentage ceilings, alias targets, invalid capacities, and unrepresentable reserves; confirm the new behavior fails against the old projector.
- [x] 1.3 Apply the additional growth ceiling with 64-bit arithmetic and preserve existing capacity validation; run the catalog tests and mutation-check removal of the new guard.

## 2. Client acceptance

- [x] 2.1 Add a catalog-driven real-Codex compaction actuator without client limit overrides; verify automatic compaction and actual custom-exec output from its manifest, trace, and isolated client dispatch log.
- [x] 2.2 Run a live-Copilot multi-tool task through an isolated bridge with the revised catalog and inspect the client's execution evidence.

## 3. Integration checks

- [x] 3.1 Run the unit suite, captured-model snapshot contract, and OpenSpec validation; inspect the final diff and record verification evidence in the change.
