# Verification

Verified on 2026-10-22 with .NET SDK 10.0.402 and the real desktop-bundled Codex CLI 0.163.0-alpha.11. No runtime dependency was added. All bridge subprocesses used isolated scratch directories and non-8765 ports.

## Contract and build checks

- Solution build: succeeded with zero warnings and zero errors.
- Solution non-integration test filter: 1,891 unit tests passed after the first review fixes; Playground has no non-integration cases.
- OpenSpec strict validation: passed.
- `git diff --check`: passed.
- Plaintext-marker mutation: replacing the empty encrypted-argument marker with a nonempty list made the sparse lifecycle contract fail. Original source was restored.
- Decryption-diagnostic mutation: discarding the reviewed diagnostic in the typed failure factory made both direct-terminal and trailing-error endpoint contracts fail. Original source was restored.

## Live Copilot / real native collaboration

Case: `Codex_AstraPlaintextCollaboration_ExecutesChildToolsAndFollowup_ForVerdict`.

Manifest: `tests/behavior-runs/manifests/codex-astra-plaintext-collaboration-20261022-090319-117.json` (local, ignored evidence).

The real Astra parent spawned one Astra child, the child read random input numbers, computed a result, generated its own GUID, wrote/read a file, sent the results to its parent, and then completed a follow-up on the same child to write/read the file's SHA-256. The parent independently verified all outputs.

- 15 upstream requests, all using exact `gpt-6-astra`.
- 12 distinct tool calls and 12 matching outputs, including six real custom `exec` calls.
- 13 requests carried agent messages; zero encrypted agent-message parts were present.
- Client-facing spawn, send-message, and follow-up calls all restored `namespace:collaboration` and `encrypted_function_args: []`.
- Actual arithmetic: `21160`; child-generated GUID: `24bb0321-b723-4b19-a7a3-fff8d3fe4bda`.
- Actual result-file SHA-256 and the child's saved follow-up agree: `06750A80A377C6495213F51BAA4D9E74AD2A0F8A3A22E8D6313CE6A80E65C883`.
- The manifest-selected client SQLite window contained 472 rows, zero router/dispatch fatals, zero ERROR rows, and zero retry rows. The client output contained no aborted execution.

Verdict: **PASS** from the client's execution results, paired wire calls/outputs, and client-owned dispatch evidence.

## Captured native HTTP replay

Case: `CapturedCollaborationInventory_ProducesAPlaintextSpawnCallAtNativeEdge`.

The committed fixture preserves the exact public collaboration namespace from the real client's `additional_tools` inventory, with private context replaced by a bounded synthetic prompt. Its live HTTP-edge replay completed and returned the exact plaintext task `Reply CAPTURED_PLAINTEXT_TASK_OK.` with the proper Codex namespace and empty encrypted-argument marker.

Verdict: **PASS** for the wire contract; this complements, rather than substitutes for, the live-client verdict above.

## Real-client failure-detail and recovery path

Case: `Codex_DecryptionDetailAfterEmptyFailure_ReachesClientAndToolRecovery_ForVerdict`.

Manifest: `tests/behavior-runs/manifests/codex-decryption-failure-detail-20261022-091607-651.json` (local, ignored evidence).

A deterministic upstream reproduced Copilot's observed `response.failed/error:null` followed by the exact structured decryption error. This is a real-client protocol test, separate from the live-Copilot collaboration scenario above.

- Exactly one injected failure reached the native client with `invalid_request_body` and the reviewed decryption message.
- Codex's own error notification included `stream disconnected before completion: Encrypted function output content could not be decrypted or decoded.` and `willRetry:true`.
- The subsequent custom `exec` and its matching output completed two actual nested shell operations (file write and independent readback); the final client message was the observed marker `DECRYPTION_DIAGNOSTIC_RECOVERY_48379`, which was absent from the user's task prompt.
- The exact manifest-selected SQLite window contained 82 rows, zero router/dispatch fatals, and zero ERROR rows.

Verdict: **PASS** for actionable client-visible failure detail and resumed tool execution.

## Operational scope

The workaround is opt-in with `Codex:PlaintextCollaboration:Enabled=true`, requires a restart, and applies only to stateless native Codex Responses traffic. Existing encrypted history is preserved and can still fail; start a fresh task when enabling. The error-detail correction is independent of the workaround switch. This change does not establish that Copilot's internal encryption defect is resolved.

## Copilot review round 1

The three review findings were reproduced with 14 new contract cases, all failing before the product fixes and passing afterward: qualified reserved-name collisions in all inventories, paired/standalone named-output identity projection, and bounded malformed-buffered-response handling with the workaround enabled. The complete unit suite then passed 1,891 cases.

The live current Codex Astra lifecycle was repeated after these fixes. Manifest: `tests/behavior-runs/manifests/codex-astra-plaintext-collaboration-20261022-094359-703.json`.

- 17 upstream requests, 14 matching tool/output rounds, seven custom exec calls, and zero encrypted agent-message parts.
- All three collaboration message actions retained their restored namespace and explicit empty plaintext marker.
- The independently verified arithmetic was `27212`; result-file SHA-256 matched the child's follow-up: `6598CB0CBC536EB390868B5514753638520B688A39C86EACCF20ADCCF2D1A0AB`.
- The run-specific client SQLite window had 513 rows and zero router/dispatch fatals, ERROR rows, or retries; client execution was not aborted.

The real-client decryption-detail/recovery case also passed again (manifest `codex-decryption-failure-detail-20261022-094530-815.json`, 82 SQLite rows, zero router/ERROR rows, exact diagnostic delivered, successful exec/output recovery). The live HTTP-edge capture replay passed all three variants: baseline, paired named output, and standalone named output. The named-output variants assert the actual projected upstream identities and unchanged output values.

## Copilot review round 2

The inline mixed-schema dispatch finding and body-only qualified-name finding were reproduced before fixing product code: 20 new cases failed. With the fixes, all 21 new contract cases passed, including an unchanged all-plain activation guard; the complete unit suite passed 1,912 tests. Solution build had zero warnings/errors and strict OpenSpec validation passed.

Activation still requires an encrypted recognized message schema. Once active, every recognized string-message action receives plaintext dispatch metadata, including already-plain siblings. Qualified aliases now require a declared collaboration child. Unrelated flat functions with dotted/double-underscore prefixes retain their choices and replay identities; flat names ambiguous with declared children fail before sending.

The real current Codex/Astra lifecycle passed both variants after these fixes:

- Original inventory: manifest `codex-astra-plaintext-collaboration-20261022-100056-100.json`, 15 upstream requests, 12 matching call/output rounds, six custom exec calls, zero encrypted agent-message parts. Independently verified arithmetic `13688` and result-file SHA-256 `BF6154E31C7B03F4FD3A5055DB10772874EB7F16EF8CDCA9C0D887D5204DE802`. Client SQLite window: 525 rows, zero router/dispatch fatals, ERROR rows, or retries.
- Mixed inventory: manifest `codex-astra-plaintext-collaboration-mixed-20261022-095938-535.json`, 18 upstream requests, 15 matching call/output rounds, seven custom exec calls, zero encrypted agent-message parts. A test-only ingress proxy removed only send/follow-up schema encryption before the real bridge; the encrypted-spawn/plain-send combination appeared in all 18 inbound inventories. Real client and live Copilot executed spawn, child-to-parent message, and same-child follow-up, all with restored namespaces and empty dispatch markers. Independently verified arithmetic `23308` and SHA-256 `9989A882C3B943E495BA8E15C7068A84E58C6E223E4F87C816725CE0D3BE0E1D`. Client SQLite window: 536 rows, zero router/dispatch fatals, ERROR rows, or retries.

Both clients completed without aborted execution. The three live HTTP-edge captured replay variants also passed again. Verdict: **PASS** from matching real execution results, run-specific wire evidence, and each client's own nonempty dispatch log.

## Copilot review round 3

The review reported no remaining code issues and one body-only documentation finding: the new decision row separated the Markdown table header from its delimiter. Moved that row below the delimiter and verified header/delimiter adjacency and `git diff --check`. No product or harness code changed; the second-round execution evidence above remains applicable.

## Copilot review round 4

Six new contracts reproduced lost dispatch metadata through a model-changing buffered endpoint, buffered translators, and the Claude carrier scrub. All failed before the fix. Buffered T3/T4 now preserve explicit dispatch metadata, and the Claude buffered edge removes its internal carrier. Calls without metadata remain unmarked. The complete unit suite passes 1,920 tests.

The real buffered lifecycle initially exposed an additional reconstruction problem: a native function item id became `item_1`, and Copilot rejected its next-turn echo. The model-only native path now preserves all original response values except the model, including item ids, dispatch markers, phases, reasoning, and future fields. Endpoint regressions caught all three message-action id changes before that correction. Semantic detector edits still reconstruct from IR and retain dispatch metadata. Temporarily bypassing the semantic-equality guard made the detector-edit test fail (`before detector` instead of `after detector`); the original source was restored and the full suite passed again.

Streaming controls passed after the dispatch-carrier fix:

- Original inventory: manifest `codex-astra-plaintext-collaboration-20261022-102326-087.json`; 15 upstream requests, 12 matching call/output rounds, six custom exec calls, no encrypted agent-message parts, independently verified arithmetic `9642` and file hash. Client SQLite: 498 rows, zero router fatals, ERROR rows, or retries.
- Mixed inventory: manifest `codex-astra-plaintext-collaboration-mixed-20261022-102432-849.json`; 15 upstream requests, 12 matching call/output rounds, six custom exec calls, 15 mixed inbound inventories, no encrypted agent-message parts, independently verified arithmetic `23558` and file hash. Client SQLite: 512 rows, zero router fatals, ERROR rows, or retries.

All four live captured-inventory HTTP-edge variants passed, including `stream:false` routed from `gpt-5.6-sol` to live Astra. That buffered contract asserts the restored model, complete plaintext task, empty dispatch marker, and exact preservation of the actual upstream item id.

Final buffered real-client manifest: `codex-astra-plaintext-collaboration-buffered-model-rewrite-20261022-103123-833.json`. The test-only ingress sends `stream:false` to the real bridge and wraps its actual JSON output in a transport-only SSE lifecycle for Codex's parser. It never changes returned tool identities, arguments, ids, or dispatch metadata; model-catalog requests remain untouched.

- 18 upstream requests, all buffered and targeting exact live `gpt-6-astra`; nine native responses restored the parent's requested `gpt-5.6-sol` model.
- 15 matching tool/output rounds, seven custom exec calls, successful spawn/child-to-parent message/same-child follow-up, and zero encrypted agent-message parts.
- All three native JSON message-action calls retained the collaboration namespace and explicit empty dispatch marker. No internal carrier crossed the native boundary.
- Independently verified arithmetic `22538`, child GUID `c2bc67d6-dba7-404c-8b96-17c4c3f9ae00`, and result-file SHA-256 `5CF7816A31432EEF2E508F8AECFC510B6FB121A6ADA5275859CEC73BA68371F6`.
- The completed client's SQLite window contained 476 rows, zero router/dispatch fatals, zero ERROR rows, and zero retries; no execution was aborted.

Verdict: **PASS** for real execution through the buffered model-changing path. Final solution build: zero warnings/errors; 1,920 unit tests passed after restoring the mutation; strict OpenSpec validation and `git diff --check` passed.

## Copilot review round 5 and status classification

The current-head review explicitly reported `No open issues`; its sole remaining note asked for confirmation of the documented client-owned execution evidence. The shipping script incorrectly counted the known evidence-only overview as a suppressed code finding. The operator raised this PR's review limit from five to ten rounds and authorized continuing the existing workflow.

The mirrored status scripts now distinguish the strictly recognized evidence-only overview using `COPILOT_EVIDENCE_CONFIRMATION`, while positive suppressed/open/previously-missed counts, unknown concerns, extra body text, unresolved inline threads, and API errors remain blocking. This does not waive execution verification: the shipping agent re-read the saved client digests and matching execution evidence above. Runtime and harness code are unchanged from verified head `71d86968851295f8c579778f6f39f4675cf60296`.

The actual status script passed all 11 offline `gh`-facade contracts. Four reproduced incorrect classifications before the fix, including a positive previously-missed count without a closer-look heading. The other cases retain the fail-closed guardrails, current-head provenance, clean-review supersession, and API-failure behavior. All four `AgentRepositoryCompatibilityTests` passed. Existing unrelated skill-document changes remain outside the shipping commit; only the classification additions are staged from those documents.

## Copilot review round 6

The review again reported `No open issues`, using the observed variation `Protocol-sensitive dispatch and terminal handling warrant final human review of the reported client-owned execution evidence, which was not independently verified here.` The conservative classifier blocked that unrecognized wording. A regression reproduced the block before extending the same bounded evidence-only sentence pattern. All 12 offline contracts now pass, including real hidden counts, unknown concerns, extra text, unresolved threads, and API failures; all four mirrored-skill compatibility tests pass. Runtime and harness code remain unchanged from the verified head above.
