## 1. Authentication Contract Tests

- [x] 1.1 Replace the 401-only Copilot client contract with tests proving the first 401 or 403 refreshes/reuses one lease and replays exact request bytes once across Responses, Messages, models, and count-tokens surfaces.
- [x] 1.2 Add mixed-status and persistent-refusal tests proving `401 -> 403`, `403 -> 401`, `403 -> 403`, and `401 -> 401` never exceed two sends or one lease rejection.
- [x] 1.3 Add tests proving 400, 402, 429, validation, quota, and other non-401/403 statuses never enter authentication replay.
- [x] 1.4 Add generation-race tests proving a rejection of generation N reuses an already-published N+1 without a redundant Copilot token exchange.
- [x] 1.5 Mutation-check request fidelity and retry accounting: changing replayed body/header/endpoint facts, resetting the transient budget, or giving the replay more than one attempt must fail a contract test.

## 2. Generation-Aware 403 Recovery

- [x] 2.1 Introduce a closed internal Copilot lease-rejection reason for 401 versus 403 while preserving `GetCopilotTokenAsync` as the sealed auth facade's only caller surface.
- [x] 2.2 Thread the typed rejection reason through `AuthService`, clear only the rejected current generation, reuse newer generations, and emit status-specific secret-free refresh triggers.
- [x] 2.3 Extend `CopilotClient.SendAuthenticatedAsync` so the first 403 follows the same single exact-replay path as 401 and one shared replay flag bounds mixed status sequences.
- [x] 2.4 Delay policy/entitlement classification until a refreshed/reused lease also returns 403; retain terminal 401, quota, billing, rate-limit, and validation classifications without leaking response or credential bytes.
- [x] 2.5 Add a Debug-only, one-shot first-403 behavior seam that exercises the production rejection/refresh/replay path while sending the replay to real Copilot; ensure Release/AOT output contains no seam activation path.

## 3. Catalog Failure Cooldown

- [x] 3.1 Add `Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds` with exact default 300 in code and stock appsettings plus startup validation for 1..3600.
- [x] 3.2 Add option-binding/validation tests proving absent configuration uses 300, valid explicit values remain exact, and out-of-range values fail before serving.
- [x] 3.3 Extend `CodexCatalogOverlayService` with a process-local failure retry deadline that serves stale last-known-good facts or a cold unvalidated baseline without new upstream I/O during cooldown.
- [x] 3.4 Preserve refresh single-flight at cooldown expiry, clear failure state on success, leave caller cancellation isolated from the shared refresh, and never persist failure state.
- [x] 3.5 Add deterministic-time tests for cold/stale failure, suppressed polls, warning count, exact cooldown expiry, concurrent retry, successful reset, and fresh-process immediate retry; mutation-check removal of the cooldown gate.

## 4. Documentation and Diagnostics

- [x] 4.1 Update `docs/pipeline-design.md`, `docs/copilot-api-research.md`, `docs/token-storage.md`, and the durable decision log from 401-only recovery to bounded 401/403 CAPI lease recovery.
- [x] 4.2 Document first-403 ambiguity, second-403 terminal classification, mixed-status bounds, generation races, and why restart previously appeared to fix the incident.
- [x] 4.3 Document the live-overlay cooldown key and fail-open catalog behavior in README/appsettings/catalog references without presenting metadata warnings as inference failures.
- [x] 4.4 Update both real-client-verify skill mirrors with the forced-403 recovery case and run `AgentRepositoryCompatibilityTests`.

## 5. Automated Verification

- [x] 5.1 Run focused auth retry, AuthService, catalog overlay, options, endpoint, and logging tests; resolve failures without weakening the 401/403 contract.
- [x] 5.2 Run the full unit suite, solution build, Release CLI build, and strict OpenSpec validation with zero code/build warnings.
- [x] 5.3 Publish or run the platform-appropriate Native AOT verification and confirm the bridge/updater packaging contract remains intact and the binary-size change is recorded if material.

## 6. Real-Client Acceptance

- [x] 6.1 Add a real Codex ClientBehavior actuator that crosses the forced first-403 path, refreshes/replays inside one bridge request, executes a complex multi-command/custom-exec task, and records an exact run manifest.
- [x] 6.2 Judge the Codex run from its own `logs_2.sqlite` plus the per-run trace: one 403 recovery, fresh generation replay, matching tool call/output, completed canary, no abort, and zero router/dispatch fatal.
- [x] 6.3 Drive real Claude Code through the shared Messages recovery path on a multi-tool task and confirm transcript tool-use/result completion with no visible 403 retry exhaustion.
- [x] 6.4 Add a real-process catalog test proving repeated polls during a persistent `/models` failure produce one upstream attempt/warning per cooldown while every downstream catalog response remains safe.
- [x] 6.5 Record final manifests, client-owned verdicts, test/build/AOT results, and the production incident evidence in `verification.md`; finish all artifacts before the archive/PR workflow.
