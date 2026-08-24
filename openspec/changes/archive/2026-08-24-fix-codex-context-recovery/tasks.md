## 1. Contract And Regression Tests

- [x] 1.1 Update `docs/pipeline-design.md` first with the 85% catalog policy and native Codex pre-stream context-recovery boundary.
- [x] 1.2 Add contract-first catalog projection tests requiring 892,000 for the current 1,050,000 / 922,000 model limits and retaining the lower prompt-derived guard.
- [x] 1.3 Add contract-first classifier and endpoint tests for the exact streaming `/codex` context 400, raw-versus-downstream audit separation, one failed terminal, no reasoning header, and every near-miss passthrough.
- [x] 1.4 Mutation-check the new threshold and exact-error guards by temporarily breaking each product value/condition and confirming the focused tests fail for the intended reason.

## 2. Implementation

- [x] 2.1 Change Codex catalog projection to cap automatic compaction at 85% of total context, retain the prompt guard, and round down to a whole thousand tokens.
- [x] 2.2 Refactor the confirmed Copilot context-window classifier for reuse without weakening the existing Claude Code error translation.
- [x] 2.3 Adapt only a confirmed streaming native `/codex/responses` context rejection into one HTTP 200 Responses `response.failed` event with `context_length_exceeded`.
- [x] 2.4 Preserve the raw upstream 400/body and record the adapted downstream status/event independently, without `X-Reasoning-Included` or stale content headers.

## 3. Permanent And Real-Client Verification

- [x] 3.1 Add a minimized, de-identified ApiContract captured-byte replay carrying automatic pre-turn compaction metadata and the confirmed Copilot error response.
- [x] 3.2 Add a deterministic `Kind=ClientBehavior` Codex scenario that injects the exact context 400 on the first compact attempt, observes reduced-history retry, and completes a real tool round trip.
- [x] 3.3 Run the path-specific real Codex scenario and render PASS/FAIL from its manifest, per-run trace, stdout, and `logs_2.sqlite` window.
- [x] 3.4 Run an ordinary live-Copilot real Codex multi-step tool case and verify matching tool call/output, no abort, and zero router/dispatch fatals.

## 4. Documentation And Validation

- [x] 4.1 Update README context guidance, `docs/codex-implementation-design.md`, `docs/copilot-api-research.md`, and the dated decision log with the 85% threshold and exact native recovery adapter.
- [x] 4.2 Run focused tests, the full unit suite, solution tests excluding Integration, `dotnet build`, and `git diff --check`.
