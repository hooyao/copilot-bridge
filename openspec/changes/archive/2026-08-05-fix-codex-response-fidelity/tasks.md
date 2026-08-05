## 1. Ground and Freeze the Contract

- [x] 1.1 Add a de-identified Responses SSE fixture that contains detailed reasoning, encrypted content, commentary/final phases, complete usage including `cache_write_tokens`, stable response/item ids, tool calls, delta metadata, and unknown event/field extensions.
- [x] 1.2 Add a full ordered event-value diff test that reproduces the current T3→T4 loss on the fixture before implementation, and mutation-check that dropping one reasoning/metadata field makes it fail.
- [x] 1.3 Add premature-EOF coverage from the audited custom-tool shape: deltas without `.done`, output-item completion, or a response terminal must never become a successful synthetic completion.

## 2. Lossless Native Responses Carrier

- [x] 2.1 Add an AOT-safe private event carrier that holds the original Responses event plus the exact semantic IR event sequence derived from it, with no whole-response buffering.
- [x] 2.2 On native `/codex` Responses routes, make T3 emit semantic events for inspection followed by the private carrier for every source event, including reasoning and unknown events that produce zero semantic events.
- [x] 2.3 Make T4 feed every semantic event through its existing state machine, restore the original Responses event only when the detector-approved semantic sequence still matches, retain the generated fallback for carrier-free streams, preserve the proven model-only rewrite, and fail closed on every other mutation.
- [x] 2.4 Preserve an intentional client-model rewrite by patching only the model field on restored original events; preserve every unrelated field.
- [x] 2.5 Drop every private carrier defensively at the Claude Code outbound edge and prove no carrier or `bridge_*` property reaches either client.

## 3. Honest Fault Terminals

- [x] 3.1 Preserve the bounded `response.failed` security surface on both client routes: retain the normalized machine code in the Codex envelope and accounting but never expose the upstream generated error message.
- [x] 3.2 Change failed/incomplete stream flush so it emits one `response.failed` without synthesizing custom/function tool `.done`, output-item completion, or a successful terminal for an open block.
- [x] 3.3 Preserve the safe prefix on upstream exception/premature EOF, distinguish downstream cancellation, and mutation-check exactly-one-terminal behavior across direct, buffered-detector, and block-buffered detector paths.

## 4. Contract and Regression Tests

- [x] 4.1 Prove full value/order conservation for reasoning summaries, encrypted reasoning, phase, ids, usage details, delta metadata, terminal metadata, and unknown extensions on a clean native Codex route.
- [x] 4.2 Prove response leak/runaway/tool-validation detector decisions cannot be bypassed by original-event restoration, including a mutation that restores a rejected carrier.
- [x] 4.3 Prove carrier-free T3/T4 fixtures and `/cc→Responses` translation remain valid, and native `/cc→Anthropic` request/response bytes are unchanged.
- [x] 4.4 Add a bounded corpus verifier for four-file traces and run it against the audited two-day window, requiring zero undeclared request/response differences after the fix.
- [x] 4.5 Correct tool-result array flattening so `text`, `input_text`, and `output_text` blocks contribute only their text values, with non-text blocks preserved as compact JSON; mutation-check all variants.
- [x] 4.6 Preserve modeled reasoning item `id`, `encrypted_content`, `summary`, and `content` fields through T1/T2; replay the exact live turn-two 400 shape and mutation-check summary/content independently.

## 5. Documentation and Build Gates

- [x] 5.1 Update `docs/pipeline-design.md` with the same-protocol carrier, detector authority, cross-client scrub, and fault-terminal ownership.
- [x] 5.2 Run focused tests, the complete unit suite, solution non-integration tests, and mutation checks; record exact commands/results.
- [x] 5.3 Publish both Windows Native AOT executables with zero trim/AOT warnings and record bridge/updater sizes in `docs/size-history.md`.

## 6. Real-Client Acceptance

- [x] 6.1 Run a real headless Codex multi-turn, multi-tool `Kind=ClientBehavior` task through a bridge subprocess on a non-default port that exercises reasoning plus function/custom tool paths.
- [x] 6.2 Use the real-client verifier to inspect the run manifest and Codex `logs_2.sqlite`; require executed tool output, a completed turn, and zero router/dispatch/incompatible-payload fatals.
- [x] 6.3 Diff that run's four-file trace at full event-value granularity and confirm reasoning, phase, ids, usage metadata, terminal integrity, and marker isolation.

## 7. Finalize Change Artifacts

- [x] 7.1 Reconcile every implementation and verification task with current evidence and strictly validate the change. Inspection of the mechanically synced main spec purpose/scope, PR review, merge, and release remain tracked by the invoking `ship-pr` workflow rather than by the archived implementation checklist.

## Verification Evidence

- Contract mutation checks: the ordered fidelity fixture failed on the old T3/T4 path (20 source events became 13); reasoning summary/content and `input_text`/`output_text` tool-result tests failed before their product fixes and passed afterward.
- Focused response/request/fault suites: 71 passed; broader Codex stream, detector, endpoint, namespace, custom-tool, marker, and hot-path regressions passed.
- Complete unit suite: `dotnet test tests/CopilotBridge.UnitTests --no-restore` → 1,491 passed after Copilot review round 1.
- Solution non-integration gate before review follow-ups: `dotnet test CopilotBridge.slnx --filter "Category!=Integration" --no-restore` → 1,487 passed; Playground had no matching non-integration tests. The final unit gate covers all round-1 product changes.
- Fixed-window corpus replay (`20260804-160000-0000` through `20260805-165534-0081`): 3,227 target turns; 3,222 complete request chains, 3,219 normal SSE responses, and 2 buffered responses replayed with zero undeclared differences; 5 incomplete four-file captures and 1 historical unterminated stream were classified, not counted as complete.
- Final real-client run: `codex-xhigh-reasoning-fidelity-20260805-183458-833.json`; three rounds all 200; detailed+xhigh reasoning, namespaced `function_call` + matching output, custom `exec` + matching output, 73 reasoning-summary deltas, three exact upstream→inbound event-value pairs, no marker leak; Codex turn completed with canary and no abort; SQLite window contained 475 rows, 0 router/dispatch fatals, and 0 ERROR rows.
- Windows Native AOT after Copilot review round 1: bridge and updater published with zero trim/AOT warnings; `copilot-bridge.exe` 14,094,848 B, `copilot-updater.exe` 5,019,136 B.

## PR Review Follow-ups

- Copilot round 1: preserve newline separators around empty tool-result text blocks.
- Copilot round 1: retain the `ctc`-prefixed custom-tool identity correction on the native Responses restoration path, including added/done/terminal lifecycle copies without changing sibling fields.
- Copilot round 1: document that `BufferScannableBlocks` retains one complete text/thinking block's source-event ledger entries, distinct from whole-response buffering.
