## 1. Freeze and Prove the Contract

- [x] 1.1 Prove with real Claude Code 2.1.220 that an opaque `redacted_thinking.data` value survives a real Bash tool trajectory byte-for-byte and remains hidden from final text.
- [x] 1.2 Probe live gpt-5.6-sol replay variants and establish that `summary` is required with `encrypted_content`, while `id`/`content` are optional but fidelity-relevant when present.
- [x] 1.3 Add a failing T3→Claude→T2 contract test from a complete captured reasoning item; require block lifecycle/order and exact reconstruction of encrypted content, empty/non-empty summary, id, and content.
- [x] 1.4 Mutation-check that dropping summary, changing one encrypted byte, disabling T3 carriage, or decoding on the native Codex path makes the appropriate contract fail.

## 2. Implement the Layered Carrier

- [x] 2.1 Push the whole reasoning item into the IR from T3 unconditionally — a hidden `redacted_thinking` block plus a `bridge_reasoning_item` marker — mirroring the existing `web_search_call` mechanism, with no knowledge of the downstream client.
- [x] 2.2 Pull at each client edge: the native Codex edge emits no output item for the block (its ledger restores the original events), while the Claude edge folds the marker into the block's own `data` and scrubs it.
- [x] 2.3 Unfold at the Claude inbound edge back into the part-level provider bag T2 already reads, so the request builder needs no knowledge of the envelope.
- [x] 2.4 Keep the codec versioned, size-bounded, and strict; fail closed on corrupt/oversized/newer-version payloads without exposing provider state as text.
- [x] 2.5 Leave the plain blob rather than a carrier when an upstream item is not replayable, so an unreplayable item degrades to stateless instead of failing the next turn.

## 3. Regression and Capture Coverage

- [x] 3.1 Add T3 lifecycle tests for reasoning before a tool call, block indexes, and incomplete-item downgrade.
- [x] 3.2 Add edge tests for fold/scrub, unfold/restore, provider-native blobs, and malformed carriers.
- [x] 3.3 Keep native Codex reasoning echo passing through its own T1 bag unchanged.
- [x] 3.4 Run focused suites, the full unit suite, and the solution non-integration gate.

## 4. Live Multi-Turn Acceptance

- [x] 4.1 Retain the deterministic real-Claude redacted-thinking echo probe and the direct gpt-5.6-sol field-requirement probe as permanent evidence.
- [x] 4.2 Add and run a live CC→gpt two-turn ApiContract test that carries reasoning to the client and restores it upstream.
- [x] 4.3 Confirm from a real `claude.exe` multi-tool run that the carrier reaches the client, no marker leaks, tools execute, and every upstream stays 2xx.

## 5. Harness, Documentation and Build Gates

- [x] 5.1 Fix `ClaudeProcess.ResolveClaudeExe` so it never silently selects an npm shim (which re-parses arguments through a shell and truncates any prompt containing `>`), searching the npm package layout and failing with a diagnostic instead.
- [x] 5.2 Update `docs/pipeline-design.md` with the source-push/dest-pull split for both reasoning and multimodal tool output.
- [x] 5.3 Run strict OpenSpec validation and both Windows Native AOT publishes with mtime/size proof and zero trim/AOT warnings.

## Verification Evidence

- Client capability: a deterministic Anthropic upstream sent `redacted_thinking` + a Bash call to real Claude Code 2.1.220; the transcript stored the block hidden, Bash executed, and the next inbound request echoed the exact data (including `+`, `/`, `=`) before the same tool call.
- Backend requirement (live gpt-5.6-sol): `encrypted_content` alone → 400 `Missing required parameter: 'input[1].summary'`; `id`+blob → 400; blob+summary → 200; id+blob+summary → 200; blob+summary+content → 200; full item → 200.
- Layering guard: `IdenticalJson_OpaqueWhenSourceMarkedIt_InterpretedOtherwise` — the same tool-result JSON stays verbatim when the source marked it opaque and is interpreted when it did not, so a reintroduced client-identity parameter cannot keep both halves green.
- Regression caught and fixed during the refactor: T4 rendered the new reasoning IR block as 8 empty assistant messages (`CodexWebSearchRoundTripTests`); the Codex edge now emits no output item for it.
- Unit suite: `dotnet test tests/CopilotBridge.UnitTests --no-restore` → 1,505 passed. Solution non-integration gate → 1,505 passed.
- Live ApiContract: `CcReasoningReplayHeadlessTests` → turn 1 carried a hidden carrier with no `bridge_reasoning_item` property; turn 2 restored `id`/`encrypted_content`/`summary`/`content` and the upstream returned 200 (a dropped summary is a 400 here).
- Real client: `ClaudeCodeBehaviorTests` 10/10, including a CC→gpt multi-tool run whose transcript shows the `cbridge_rr_…` carrier delivered hidden, three tools executed, and the canary returned; Codex behavior suite 6/6 unchanged.
- Native AOT: clean Release/RID rebuild generated native code for both executables with zero trim/AOT warnings. `copilot-bridge.exe` 14,111,232 B (mtime 2026-08-06T07:14:59Z); `copilot-updater.exe` 5,019,136 B (mtime 2026-08-06T07:15:15Z).
