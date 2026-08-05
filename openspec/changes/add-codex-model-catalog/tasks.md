## 1. Freeze the external contracts

- [x] 1.1 Record the exact upstream `openai/codex` source tag/commit and supported 0.144.x client-version interval; vendor the complete `models.json` baseline as an embedded, read-only asset with provenance and license metadata.
- [x] 1.2 Add a reproducible catalog-refresh/check script that fetches a named Codex tag, validates required entries and instruction sources, reports schema/key drift, and refuses an unreviewed client-version interval.
- [x] 1.3 Capture the live GitHub Copilot `/models` response for all bridge Responses profiles and re-probe `gpt-5.4`, `gpt-5.5`, and `gpt-5.6-{luna,sol,terra}` with real Codex-shaped bytes beyond the old 272k boundary; record total-context, prompt, and output results in the protocol research/contract snapshot.
- [x] 1.4 Write contract-first tests from the delta specs for version selection, complete-entry preservation, exact-slug joining, route availability, limit validation, compaction reserve, invalid auto-review targets, and unsupported versions; mutation-check the product guards once implemented.

## 2. Build the catalog model and projection

- [x] 2.1 Add the minimal Codex catalog envelope/overlay DTOs and every required `[JsonSerializable]` registration to `Models/JsonContext.cs`; preserve fast-moving catalog entries as opaque `JsonElement` data without reflection serialization.
- [x] 2.2 Implement a version parser and catalog-baseline selector that accepts only declared reviewed intervals and surfaces missing, repeated, malformed, or unsupported `client_version` values as safe metadata errors.
- [x] 2.3 Implement baseline validation: unique slugs, complete instruction source, required structural fields, valid review-override references, and provenance/version metadata; fail the build/test suite for a corrupt embedded snapshot.
- [x] 2.4 Implement the exact-slug projection over `CodexModelProfileCatalog`/the explicit Responses route set and live Copilot `/models`; do not synthesize unknown live models, and make baseline-only or retired models unavailable to command-auth Codex.
- [x] 2.5 Implement the backend-field allow-list overlay, preserving every other baseline property byte-semantically; retain `auto_review_model_override` only when its target remains bridge-routable.
- [x] 2.6 Implement validated context mapping: total context → `context_window`/`max_context_window`; explicit auto-compact threshold ≤ 90% total and ≤ 97.5% prompt rounded down to 1,000; inconsistent or missing limits retain the baseline and emit a warning.
- [x] 2.7 Add a stable catalog ETag derived from the selected baseline and effective validated overlay.

## 3. Add bounded live model discovery

- [x] 3.1 Implement a singleton, single-flight catalog overlay service using the existing authenticated `ICopilotClient`, a bounded TTL, atomic last-known-good replacement, and no persistent cross-process capability cache.
- [x] 3.2 Implement the three failure paths with tests: fresh live overlay; failed refresh with last-known-good overlay; cold failed refresh with no uplift and only statically known routable models enabled.
- [x] 3.3 Confirm metadata refresh observes existing upstream cancellation/timeout policy and cannot indefinitely block Codex startup; log capacity degradation without logging catalog instructions, prompts, or credentials.

## 4. Serve the Codex catalog endpoint

- [x] 4.1 Add `Endpoints/Codex/CodexModelsEndpoint.cs` and map `GET /codex/models`; validate the query, resolve the compatible baseline, request the cached overlay, return Codex `ModelsResponse` plus `ETag`, and use a non-2xx Codex-readable error for unsupported versions.
- [x] 4.2 Register the endpoint and catalog services in the AOT-safe server composition root without changing `/cc` or `POST /codex/responses` behavior.
- [x] 4.3 Add endpoint contract tests proving exact route/query handling, status/error cases, Codex envelope shape, ETag stability/change, source-generated serialization, and absence of raw Copilot schema/credentials.
- [x] 4.4 Add a pinned consumer test that feeds the endpoint bytes to the real installed Codex version or the matching upstream Rust schema fixture and proves every returned entry deserializes with its instruction source intact.

## 5. Activate discovery in `config codex`

- [x] 5.1 Add a hidden noninteractive CLI command that prints only the stable non-secret provider sentinel and starts no web host, reads no token store, and makes no network call.
- [x] 5.2 Extend `CodexConfigurator` to resolve native-executable versus `dotnet <dll>` invocation and write the managed nested `[model_providers.copilot-bridge.auth]` command, arguments, timeout, and refresh policy alongside `name`, `base_url`, and `wire_api`.
- [x] 5.3 Extend the trivia-preserving merge so replacing the bridge provider plus nested auth table remains byte-idempotent and leaves every unrelated top-level key, rival provider, comments, literals, whitespace, user model/effort/context/auto-compact override, and dense TOML table byte-identical.
- [x] 5.4 Extend `config status`/`ConfigState` to report discovery-auth drift without printing the sentinel or any credential, while retaining tolerant behavior for malformed/unreadable TOML.
- [x] 5.5 Add config contract tests for native and JIT invocation, first write, upgrade from the legacy provider block, second-run byte identity, dry run, backup, malformed TOML refusal, rival nested tables, and preservation of explicit context overrides.
- [x] 5.6 Prove with request capture that Codex sends only the public sentinel to `/codex/models` and `/codex/responses`, while `AuthService` independently sends the real Copilot token upstream; add a regression assertion that the real token cannot enter config, logs, traces, or downstream catalog bytes.

## 6. Offline and live verification

- [x] 6.1 Run `dotnet test tests/CopilotBridge.UnitTests` and the solution-wide non-integration suite; fix all failures without weakening contract assertions.
- [x] 6.2 Add/run `Kind=ApiContract` tests against captured live Copilot model bytes, including 1,050,000 total / 922,000 prompt → approximately 898,000 auto-compact, smaller-window models, missing limits, inconsistent limits, retired models, and a future unknown model.
- [x] 6.3 Add a `Kind=ClientBehavior` scenario that starts `ServeProcess` on a non-8765 port, configures the real Codex client hermetically with command auth, asserts it requests `/codex/models?client_version=...`, carries active context beyond 272k, and completes a complex multi-step/multi-tool task through `POST /codex/responses`.
- [x] 6.4 Use the `real-client-verify` workflow to read the run manifest and Codex's own `logs_2.sqlite`; PASS requires the uplifted model context, executed tool output, and zero router/dispatch/incompatible-payload/aborted fatals. A bridge 200 or trace alone is explicitly inconclusive.
- [x] 6.5 Re-run the long-context client scenario after a forced Copilot `/models` failure to prove safe baseline fallback, and after restoring discovery to prove uplift recovery.

## 7. AOT, documentation, and handoff

- [x] 7.1 Publish win-x64 through the repository's verified AOT path, confirm zero trimming/AOT warnings, and record the binary size/delta in `docs/size-history.md` as informational history only; do not use size as a design constraint or acceptance gate.
- [x] 7.2 Update `docs/pipeline-design.md` first with the Codex metadata path and ownership split; update `docs/codex-implementation-design.md`, `docs/copilot-api-research.md`, `docs/routing.md` where the 272k workaround is stale, and add a catalog-refresh maintenance procedure.
- [x] 7.3 Update README setup/limitations so existing users know to re-run `config codex` and restart Codex, and state clearly that 1.05M total context currently maps to a safe roughly 900k working/auto-compact threshold rather than 1.05M prompt capacity.
- [x] 7.4 Run `openspec validate add-codex-model-catalog --strict` (or the current CLI equivalent), review the final diff for accidental scratch/capture files, and leave the change ready for implementation review without modifying or deleting the user's existing untracked artifacts.
- [x] 7.5 Add a default-on `Codex.ModelCatalog.Enabled` setting, conditionally map only `GET /codex/models`, and cover code-default, configuration binding, disabled-route, documentation, strict-validation, and regression behavior.
