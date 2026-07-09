# Tasks — fix-response-pollution-buffered-runaway

## 0. Baseline self-check (worktree may fork from stale main)

- [x] 0.1 Confirm the worktree base has the span-based `RunawayGuardDetector` (#31) and the `SystemReminderMatcher` (#30): `RunawayGuardDetector.cs` uses `GetAlternateLookup`/`_tokenTail`, and `ResponseLeakAutomaton.cs` contains `SystemReminderMatcher`. If either is absent, stop — the branch forked from stale code.
- [x] 0.2 Run the existing unit suite green before any change: `dotnet test tests/CopilotBridge.UnitTests --filter "Category!=Integration"`. (768 passed)

## 1. Config surface (options + appsettings)

- [x] 1.1 Add `RepetitionMaxConsecutiveRepeat` (int, default 50) to `RunawayGuardOptions.cs` with an XML docstring: per-block consecutive-identical-token trip threshold; `<= 0` disables the run-length signal; independent of `RepetitionWindow`; read at startup, restart required; a false positive is resolved by raising the value.
- [x] 1.2 Add `BufferScannableBlocks` (bool, default false) to `ResponseLeakGuardOptions.cs` with an XML docstring: when true (and `PreserveStream` true) withhold each scannable block's deltas until block end and relay only if clean, so a streamed leak is suppressed before any byte reaches the client; non-scannable blocks stream live; no effect when `PreserveStream` is false or the guard is disabled.
- [x] 1.3 Update `src/CopilotBridge.Cli/appsettings.json`: add both keys under their sections with `_`-prefixed comment strings matching the option docstrings; keep the existing runaway comment's shape.

## 2. RunawayGuard: shared per-block scan core + buffered path (Bug A)

- [x] 2.1 Refactor `RunawayGuardDetector` so the per-block repetition tokenizer/window logic is a private core callable with a text fragment (streaming) and with a whole-block string (buffered), sharing `_tokenTail` reassembly and per-block reset — no behaviour change to the streaming path yet.
- [x] 2.2 Implement `InspectBuffered(byte[] body)`: parse the Anthropic Messages body with `JsonDocument`; for each `text` block (and `thinking`) reset per-block state and feed the block string through the core; on a trip return `Abort(...)` with the same `Signal`/status and set `ctx.RunawayDetected`. Fail open (return `None`) on an unparseable body or one with no `content` array. No reflection-based serialization (AOT).
- [x] 2.3 Verify the buffered path is actually reached: `RunawayGuardDetector` does NOT set `RequiresBuffering` (it must scan an already-buffered `application/json` body, not force whole-response buffering), and `ResponseInspectionStage.ApplyBuffered` calls `InspectBuffered` for it.

## 3. RunawayGuard: consecutive-run signal (Bug B)

- [x] 3.1 Add a per-block consecutive-identical-token counter to the shared core (previous-token + count; identical → increment, different → reset to 1; reset per block). Reuse the existing span-keyed token identity so no per-token string allocation is added on the degenerate path.
- [x] 3.2 Trip when the run reaches `RepetitionMaxConsecutiveRepeat` (when `> 0`), using the same abort machinery/`Signal`/`runaway=true` as the other signals; ensure it evaluates on both streaming and buffered paths via the shared core.
- [x] 3.3 Ensure a run split across deltas (including a token split at its terminating whitespace) is counted as consecutive via `_tokenTail` reassembly.

## 4. ResponseLeak: opt-in block-buffered streaming suppression (Class 3 residual)

- [x] 4.1 Surface `BufferScannableBlocks` from the leak detector to the stage (a stage-visible flag), without adding cross-request state.
- [x] 4.2 In `ResponseInspectionStage.WrapStream`, add a per-block withhold-then-relay branch gated on that flag + `PreserveStream`: buffer a scannable block's `content_block_start`→deltas→`content_block_stop` while still feeding detectors live; at block end abort (emit no block bytes) if a leak was detected, else flush the buffered events in order. Non-scannable blocks (`tool_use`/`input_json_delta`) stream live, never withheld.
- [x] 4.3 Confirm the mode is inert when `PreserveStream=false` (whole-response buffering already applies) and when the guard is disabled.

## 5. Observability (runaway logging parity)

- [x] 5.1 Extend `RunawayGuardDetector.Trip` to include the delivery mode (`stream`/`buffer`) in its single `Warning`, still naming the reason/signal, never the runaway content; confirm the record carries the request trace id via the scoped logger.

## 6. Tests (from the contract; mutation-check each new test)

- [x] 6.1 RunawayGuard buffered path: a buffered Anthropic body whose `text` block floods then ends in a valid `tool_use` aborts with the configured status and no leaked content; a clean buffered body passes through byte-unchanged; an unparseable body fails open. (Contract: runaway-guard "Runaway detection applies on the buffered delivery path".)
- [x] 6.2 Run-length signal: a short consecutive flood (below `RepetitionWindow` total tokens) trips on both streaming and buffered paths; an interrupted run does not trip; diverse output does not trip; `RepetitionMaxConsecutiveRepeat=0` disables it; a delta-split run is counted consecutive. (Contract: "Consecutive-identical-token run-length signal".)
- [x] 6.3 Regression: reproduce the real `count` (108 tokens, 100× one token) and `court` (1405 tokens) buffered shapes and assert both now abort — copy the shapes from `diag-captures/` (NOT committed) into a test fixture string.
- [x] 6.4 Block-buffered suppression: with `BufferScannableBlocks=true`, a `text` block carrying a closed `<system-reminder>`/`<invoke>` leak is aborted with NONE of the block's deltas emitted; a clean scannable block relays all its events unchanged; a `tool_use` block streams live (not withheld); default-off reproduces today's relay-until-detection behaviour. (Contract: response-leak-guard "Block-buffered delivery".)
- [x] 6.5 Observability: a runaway trip on each delivery path emits exactly one `Warning` naming reason + delivery mode, and contains none of the runaway content. (Contract: observability "Runaway detection is logged at the detection point".)
- [x] 6.6 For every new test, mutation-check: break the product code (e.g. no-op the run-length trip, or the buffered scan) and confirm the test goes red; restore.

## 7. Docs + validation

- [x] 7.1 Update `docs/pipeline-design.md` §6.1: RunawayGuard runs on both delivery paths; document the run-length signal and the `BufferScannableBlocks` suppression mode as durable architecture.
- [x] 7.2 `openspec validate fix-response-pollution-buffered-runaway` passes; run `dotnet test tests/CopilotBridge.UnitTests --filter "Category!=Integration"` green.
- [x] 7.3 AOT safety confirmed by static check: changed files use only read-only `JsonDocument.Parse` (no reflection-based `JsonSerializer`, no new serializable DTO). Full AOT publish not required for this diff.

## 8. Pre-PR review follow-ups (4-agent review: correctness / silent-failure / test-coverage / comment-accuracy)

- [x] 8.1 (silent-failure MEDIUM) Guard `typeEl.GetString()` with a `ValueKind == String` check in `RunawayGuardDetector.InspectBuffered`, `ResponseInspectionStage.IsScannableBlock`, and the pre-existing sibling `ResponseLeakDetector.InspectBuffered` — a non-string `type` field threw `InvalidOperationException` past the `JsonException`-scoped fail-open (→ 502 / stream crash). Now fails open on a malformed block, as documented.
- [x] 8.2 (correctness Important) Add `RepetitionMaxConsecutiveRepeat` to the client-facing `RunawayMessage` remediation text — it previously named only the three signals that do NOT affect the new run-length signal.
- [x] 8.3 (comment must-fix) Restore the broken `FeedRepetition` docstring (missing opening `<summary>`); fix three comments (`ResponseLeakGuardOptions`, `appsettings.json`, `docs §6.1`) that wrongly gated `thinking`-block withholding on `ScanThinking` (code withholds unconditionally); doc `<=0` disable wording; `_prevTokenBuf` capacity (~2×) precision; trailing-flush graceful-only note; debug log on the buffered fail-open.
- [x] 8.4 (test-coverage) Add + mutation-check tests for the gaps the first suite missed: buffered DENSITY trip (parity), `_prevTokenBuf` prefix-token length guard, multi-block flush/reset, unclosed-block flush, `PreserveStream=false` inertness (direct `BuffersScannableBlocks` theory), and density-vs-run-length precedence/attribution. Full unit suite 798 green.
