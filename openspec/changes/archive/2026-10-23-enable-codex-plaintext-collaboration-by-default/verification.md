# Verification

## Reported failure

The supplied 2026-10-23 desktop trace used native `/codex/responses`, exact `gpt-6-astra`, xhigh effort, detailed reasoning summary, and stateless delivery. Its upstream inventory still declared `collaboration` with encrypted string messages for all three recognized actions, and two agent messages retained encrypted task content. Copilot returned its decryption failure. The user confirmed that the workaround flag was off. Private task content and ciphertext were not committed.

## Default-on contracts

Five cases failed before the product correction: runtime default, omitted section, omitted key, packaged configuration, and default strategy projection. They pass after changing both the option initializer and packaged value to true. Explicit-false and other-client isolation contracts remain green.

- Solution build: zero warnings and errors.
- Complete unit suite: 1,927 passed.
- Mirrored skill compatibility: four passed after documenting the native-binary harness input.
- Strict OpenSpec validation and `git diff --check`: passed.

## Native AOT and real current Codex at xhigh

Built both native Windows x64 executables with .NET SDK 10.0.402 and the Visual Studio 2026 linker. No runtime dependency was added. Candidate bridge version: `0.5.27-beta+36c9de018ea7185874c3385b029fbfa92ca953ba` (working-tree build based on the prior merged commit).

- Native executable size: 15,131,136 bytes.
- Native executable SHA-256: `937C6E6980ED4BFF944DC0286EC2C82742DFEE7B60D405C2BABFFC08A641D356`.
- The selected publish directory's stock appsettings had `Enabled:true`.
- The real-process harness supplied `PlaintextCollaboration:null`; it did not force-enable the setting. The actual binary path/hash and null override are recorded in the run's `binary.json`.

Case: `Codex_AstraDefaultPlaintextCollaboration_Xhigh_ExecutesChildToolsAndFollowup_ForVerdict`.

Manifest: `tests/behavior-runs/manifests/codex-astra-plaintext-collaboration-default-xhigh-20261023-060010-108.json` (local evidence).

The real desktop-bundled Codex 0.163.0-alpha.11 parent spawned an Astra/xhigh child, which read random numbers, computed the requested sum, generated a fresh GUID, wrote/read its result, sent the observed values to its parent, and completed a same-child follow-up to write/read the file hash. The parent independently verified the files.

- All 16 upstream requests used exact `gpt-6-astra` and xhigh effort; the initial request also used detailed summary and all-turns context, matching the reported request axes.
- The upstream inventory used `bridge_plaintext_collaboration`, with the recognized message encryption annotations removed.
- 12 distinct tool calls had 12 matching outputs, including seven custom exec calls.
- All three collaboration message actions restored the native identity and explicit empty encrypted-argument marker.
- 25 replayed agent-message items had zero encrypted task-content parts.
- Independently verified arithmetic: `21400`; child GUID: `7e88ec9b-0aa2-4763-8a1a-a7b3660df583`; result-file SHA-256: `25CC40225DF15AC34F2E5D16CEE90A3F860C0B168B0E727E183E3D0560E36FED`.
- The manifest-selected client SQLite window contained 659 rows, zero router/dispatch fatals, zero ERROR rows, and zero retries. The turn completed without aborted execution.

Verdict: **PASS** for the native AOT candidate's unforced default-on xhigh collaboration path. This is a newly built native candidate, not a claim that the previous downloaded 0.5.26-beta default-off binary was correct.

## Existing configuration

Omitted settings receive the enabled code default. An existing explicit false remains false, including a false value retained from the 0.5.26-beta template; remove that override or set it true to enable this installation. Restart and start a fresh task. The change does not recover old ciphertext, replace the installed running bridge, or alter credentials.
