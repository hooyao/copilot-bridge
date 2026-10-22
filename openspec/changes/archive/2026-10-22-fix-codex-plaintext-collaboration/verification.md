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
