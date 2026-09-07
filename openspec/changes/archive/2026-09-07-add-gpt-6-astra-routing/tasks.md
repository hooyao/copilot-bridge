## 1. Live Contract Evidence

- [x] 1.1 Fetch the official OpenAI Astra model, migration, reasoning, and async-tool documentation and record the compatibility constraints in the design.
- [x] 1.2 Discover `gpt-6-astra` from Copilot and live-probe liveness, every effort boundary, fields, tools, sync/async custom-tool streaming, structured image output, and a real GPT-5.6 Codex request with only the model changed.
- [x] 1.3 Add Astra to the permanent Responses sweep, regenerate the reviewed snapshot, and run the two-way catalog-versus-live assertion.
- [x] 1.4 Produce a real Astra behavior capture and reconfirm the rewrite-causing `none`/`minimal` rejection by mutating only effort on that capture.

## 2. Contract-First Tests

- [x] 2.1 Add registry/catalog tests for exact Astra dispatch, accepted efforts, `low` fallback, custom-tool support, multimodal support, and total model count.
- [x] 2.2 Add stock-config and route-planner tests proving exact `gpt-5.6-sol -> gpt-6-astra`, `none`/`minimal -> low`, accepted effort preservation, and no sibling-family match.
- [x] 2.3 Add catalog-projection tests proving the routed source stays visible with client-owned instructions but uses Astra limits, and hides when the exact target profile/live capability is absent.
- [x] 2.4 Mutation-check each new contract assertion by breaking the corresponding production value and confirming the focused test fails before restoring it.

## 3. Implementation

- [x] 3.1 Add the exact Astra Responses registry and profile entries with probe-citing comments, and include Astra in all applicable probe/model inventories.
- [x] 3.2 Add the active stock Location for the flagship alias and its explicit effort migration without changing Luna, Terra, or Sol Fast.
- [x] 3.3 Make Codex catalog projection resolve configured model routes and project the resolved target's live limits under the source client slug.
- [x] 3.4 Add or adapt a route-specific `Kind=ClientBehavior` case whose manifest records the gpt-5.6 client identity and Astra upstream target.

## 4. Documentation

- [x] 4.1 Update `docs/pipeline-design.md`, `docs/routing.md`, `docs/context-window.md`, `docs/codex-protocol-research.md`, and the Responses snapshot/model counts.
- [x] 4.2 Add the dated architectural decision to `docs/design.md` and update README setup/status/release-facing guidance for fresh versus upgraded configurations.
- [x] 4.3 Keep repo-owned `.agents`/`.claude` skill mirrors semantically identical if any skill text or latest-model policy changes.

## 5. Verification

- [x] 5.1 Run focused tests, the full unit suite, solution-wide non-integration tests, OpenSpec validation, and `AgentRepositoryCompatibilityTests`.
- [x] 5.2 Run the mandatory `CODEX_SMOKE_MODEL=gpt-6-astra` real-client load-task smoke and verify its wire tool loop.
- [x] 5.3 Run the route-specific real Codex behavior case on a complex multi-tool task, then read its exact manifest, trace, stdout, and `logs_2.sqlite`; require a matching tool output, canary, no abort, and zero router/dispatch fatals.
- [x] 5.4 Publish the Windows Native AOT binaries with `build-aot.bat`, confirm both executables and the expected size range, then leave the tree ready for OpenSpec archive and `ship-pr`.

## 6. PR Review Follow-ups

- [x] 6.1 Make catalog limit projection require an invariant first route and hide aliases shadowed by earlier request-dependent Locations; add a mutation-proven contract test.
- [x] 6.2 Make the Astra large-context probe select a model-specific real-client capture and confirm 310,307 input tokens return 200.

## 7. PR Review Round 2 Follow-ups

- [x] 7.1 Add `ultra` to the permanent Responses B2/B3 vocabulary and snapshot, and reconfirm its rejection on real Astra client bytes.
- [x] 7.2 Hide a validated cross-model alias when its target limits are missing or inconsistent, while preserving the direct-model and unavailable-overlay baseline behavior; mutation-check the new contract test.

## 8. PR Review Round 3 Follow-up

- [x] 8.1 Record the repository owner's explicit authorization for the fresh-install default route and confirm that existing installations retain their user-owned Locations array; no product change is required.
