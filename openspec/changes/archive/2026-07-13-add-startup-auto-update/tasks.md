# Implementation Tasks

## 1. Contract fixtures and baselines

- [x] 1.1 Translate every scenario in `startup-update-discovery` and `transactional-self-update` into a named contract-test matrix before adding product behavior; record which observable each test proves and mutation-check each new test against an intentional product break.
- [x] 1.2 Add deterministic test doubles for console interaction, time, HTTP responses, process launch/identity, local control messages, and filesystem operations so discovery and transaction-state tests never touch a real installation.
- [x] 1.3 Add disposable-install test helpers that create an isolated non-8765 installation containing versioned bridge/updater/config fixtures, preserve original arguments and working directory, and always clean only paths they created.
- [x] 1.4 Capture the current Windows AOT bridge executable mtime/size and current release archive contents as the before-change baseline for the second executable and packaging work.

## 2. Configuration and shared update contracts

- [x] 2.1 Add `AutoUpdateOptions` with code defaults `EnableAutoUpdate=true` and `AllowBetaUpdates=false`, bind it from `AutoUpdate`, and add the two documented properties to stock `appsettings.json`.
- [x] 2.2 Add binding tests proving absent section/property defaults, explicit false/true overrides, and source-generated AOT configuration binding.
- [x] 2.3 Define the versioned immutable update-plan, control-message, readiness-message, archive-kind, managed-file, and transaction-phase wire types in shared source compiled into both executables without introducing a third runtime assembly; freeze every JSON property name explicitly and represent wire-facing roles/states/kinds with validated stable literals.
- [x] 2.4 Register every bridge-owned GitHub/update DTO in `Models/JsonContext.cs`, add a separate source-generated JSON context for the updater executable, and assert reflection serialization is not used; application-wide serializer naming changes must not change the update wire bytes.
- [x] 2.5 Capture the original argument vector, process path, canonical installation directory, current PID/start identity, working directory, and finite timeout constants before System.CommandLine dispatch loses startup context.
- [x] 2.6 Implement one-launch internal update context parsing from child-only environment values, including attempt ID, role, pipe endpoint/token, expected version, and update-check suppression; accept only the independently generated capability for that exact target-Ready or rollback-Ready role and reject incomplete or malformed context without persisting it.
- [x] 2.7 Add golden-byte, Native AOT round-trip, and secret-scan tests proving CLI plan bytes are consumed by the same-release updater, updater N readiness context is accepted by candidate bridge N+1, candidate Ready bytes are consumed by updater N, application serializer-policy changes leave the frozen wire unchanged, and no plan/message contains `github_token.dat` contents, GitHub OAuth tokens, Copilot bearer tokens, or full configuration contents.

## 3. Semantic version and release policy

- [x] 3.1 Write contract tests for SemVer core/prerelease/build-metadata precedence, stable-over-prerelease ordering, invalid/overflowing tags, publication-order independence, no reinstall, and no downgrade.
- [x] 3.2 Implement the small immutable SemVer 2.0 parser/comparer and release-tag `v` normalization required by those tests, without adding a package solely for version comparison.
- [x] 3.3 Implement stable-only and stable-plus-prerelease candidate filtering, draft exclusion, highest-version selection, and `dev` build suppression.
- [x] 3.4 Mutation-check release selection by intentionally reversing one precedence comparison and proving the contract suite fails.

## 4. Anonymous GitHub release discovery

- [x] 4.1 Add source-generated DTOs for public GitHub releases/assets, including title/body/tag/prerelease/draft/published URL and asset name/state/size/digest/download URL.
- [x] 4.2 Implement an AOT-compatible anonymous `HttpClient` release client with the required User-Agent/Accept/API-version headers, `per_page=100`, finite per-request timeouts, one monotonic deadline for the complete traversal, repeated-next-target/page detection, and a defensive maximum page count; use results only after exhaustion is proven within all bounds.
- [x] 4.3 Preserve cancellation semantics: user/application shutdown cancellation propagates, while DNS/TLS/HTTP/schema/rate-limit/request-timeout/overall-deadline/pagination-cycle-or-limit failures discard partial results and become fail-open update outcomes.
- [x] 4.4 Add captured-response tests for one page, multiple pages, empty releases, malformed JSON, partial schema, rate-limit response, request timeout, caller cancellation, repeated pagination target, endless full pages, and overall-deadline exhaustion; prove no `gh` executable or authentication header is used.
- [x] 4.5 Map OS plus process architecture to `win-x64`, `win-arm64`, `linux-x64`, or `osx-arm64`; select exactly one deterministic ZIP/TAR.GZ archive and reject `.pkg`, unsupported runtimes, duplicate/missing assets, non-uploaded state, non-HTTPS URL, non-positive size, or missing/unsupported digest.
- [x] 4.6 Add asset-selection tests using real current GitHub Release Asset response shapes, including `sha256:<64-hex>` metadata and absence of a checksum sidecar.

## 5. Release presentation and consent

- [x] 5.1 Implement safe console rendering for installed/available versions, stable/prerelease channel, title, publication time, complete release body, empty-notes text, and release URL.
- [x] 5.2 Strip or visibly encode terminal escape/control characters from untrusted release title/body while preserving normal multi-line formatting, and add malicious-title/body tests.
- [x] 5.3 Implement `Install this update now? [y/N]` so only trimmed case-insensitive `y` or `yes` accepts; all other input declines without changing files.
- [x] 5.4 Detect redirected/unavailable/failing stdin before reading, never block or install noninteractively, and log that the current version will continue.
- [x] 5.5 Add tests proving release notes precede the prompt, empty/default/negative answers decline, explicit yes accepts, and noninteractive startup never launches the updater.

## 6. Minimal Native AOT updater project

- [x] 6.1 Add `src/CopilotBridge.Updater/CopilotBridge.Updater.csproj` as a self-contained size-optimized .NET 10 Native AOT console executable named `copilot-updater`, using framework APIs only unless a separately measured AOT-safe dependency is justified.
- [x] 6.2 Add the updater project to `CopilotBridge.slnx` and configure its release trimming/AOT settings without an ASP.NET framework reference.
- [x] 6.3 Implement the updater entry shell: source-generated plan load, strict schema/version validation, bounded top-level error reporting, cancellation handling, and stable exit outcomes for preflight failure, committed update, recovered rollback, and unrecovered rollback.
- [x] 6.4 Add build-graph tests/checks proving the updater does not require `gh`, PowerShell, a shell, curl, tar, unzip, or an installed .NET runtime.
- [x] 6.5 Implement bridge-side location, private-copy, plan flush, and process launch of the RID-matched installed updater, ensuring Windows executes the helper from the private attempt directory.

## 7. Private transaction root, plan validation, and locking

- [x] 7.1 Resolve an owner-private per-user update root and create cryptographically random per-attempt plan/archive/staging/backup/log paths with owner-only permissions where supported.
- [x] 7.2 Canonicalize and validate plan paths, require the exact managed allowlist (`copilot-bridge`, `copilot-updater`, and transactional `appsettings.json`), and reject every target or temporary sibling outside the installation root/attempt root.
- [x] 7.3 Implement an exclusive process-owned installation lock keyed by canonical install path and hold it through commit or rollback.
- [x] 7.4 Capture and validate parent process start identity plus executable/install path immediately before cutover; never select or terminate a process only by name.
- [x] 7.5 Add tests for malformed/incomplete plans, traversal paths, symlink/reparse install paths, lock contention, PID reuse, wrong executable identity, and another bridge running from a different installation.

## 8. HTTPS download and secure archive staging

- [x] 8.1 Implement bounded HTTPS download from exactly the planned Release Asset URL, manually following only a small finite HTTPS-only redirect chain while streaming to the private archive file.
- [x] 8.2 Count bytes and compute SHA-256 during download; require both the GitHub-reported positive size and normalized digest to match before extraction.
- [x] 8.3 Implement framework-only ZIP extraction for Windows archives and GZip/TAR entry-by-entry extraction for Unix archives into a new staging subtree.
- [x] 8.4 Validate exact required root files and reject absolute paths, `..` traversal, mixed-separator escape, duplicate normalized names, links/hard links/reparse entries, devices/special entries, unexpected required-file types, oversized content, and any write outside staging.
- [x] 8.5 Build malicious ZIP/TAR corpus tests for every rejected entry class plus digest/size mismatch, redirect downgrade, timeout, truncated download, and duplicate managed files; mutation-check at least the traversal and digest guards.

## 9. Template-based `appsettings.json` migration

- [x] 9.1 Write contract tests first for surviving scalar values, new defaults, old-only deletion at object/object merge frontiers, recursive object merge, complete `Routing.Locations` array replacement, empty old arrays, old `null`, atomic type-mismatched old subtrees (including nested old-only content), property spelling from the new template, and case-insensitive duplicate rejection.
- [x] 9.2 Read the installed configuration once into an immutable byte snapshot, hash it, and parse only those bytes; parse the new configuration with the same accepted JSON allowances while retaining object property lists long enough to reject case-insensitive ambiguity at every nesting level.
- [x] 9.3 Implement the pure merge by walking only the new template: recurse only for object/object; otherwise structurally clone the complete old value; omit old-only keys only at object/object merge frontiers and never merge/append/deduplicate/sort array elements.
- [x] 9.4 Serialize the merged tree to a distinct staging file, flush it, reparse it, and leave the installed original untouched throughout preparation.
- [x] 9.5 Make the verified private rollback copy from the exact immutable snapshot used for parsing, record its hash, and prove the copy reads back byte-for-byte before preparation can complete.
- [x] 9.6 Mutation-check migration by temporarily retaining an old-only key at an object/object frontier, pruning a type-mismatched old subtree, and merging an array element-wise; prove the corresponding contract tests fail.

## 10. Complete pre-cutover preparation

- [x] 10.1 Implement the ordered preflight coordinator: validate/lock, download/hash, extract/validate, snapshot/hash/migrate config, verify install writability, snapshot/hash/back up every existing managed binary plus the exact config snapshot, prepare same-volume replacement temporaries, then revalidate parent and every installed snapshot immediately before reporting Prepared.
- [x] 10.2 Verify backups and replacement temporaries are fully written/flushed and hash-equivalent to their recorded inputs before Prepared; retain identity-relevant metadata and Unix executable modes needed for cutover and rollback.
- [x] 10.3 Prove install-directory writability without overwriting or renaming a managed target; never request UAC, sudo, or package-manager elevation.
- [x] 10.4 Ensure every preflight failure or snapshot drift reports a concise phase/reason, discards stale merged output, cleans private temporary data best-effort, leaves every installed path unchanged, and allows the initiating bridge to continue its normal startup.
- [x] 10.5 Add state-machine tests for failure at every preflight step, including malformed config, unwritable installation, disk/write failure, unverifiable backup, parent identity change, config edit during preparation, managed-binary drift, and updater timeout.

## 11. Authenticated parent/updater handoff

- [x] 11.1 Implement the frozen, versioned, length-bounded named-pipe wire protocol with explicit JSON property names, current-user-only access where supported, and three independently generated 256-bit capabilities: CLI-created parent/updater handoff, updater-created target-Ready, and distinct updater-created rollback-Ready; bind validation to protocol, attempt, token, PID, role, and expected version.
- [x] 11.2 Implement updater `Prepared` and parent `CutoverAuthorized` messages using only the handoff capability so the updater cannot cut over merely because it sent Prepared or because the parent later starts serving.
- [x] 11.3 Implement bridge-side bounded waiting: updater start/preflight failure logs a Warning and continues the current proxy; authenticated Prepared sends authorization and returns from `ServeCommand` without constructing Kestrel.
- [x] 11.4 Wire the gate only into explicit/default `serve` before `WebApplication.CreateSlimBuilder`; add tests proving help/version/auth/debug/config, disabled updates, dev builds, and valid one-launch recovery context perform zero update HTTP/process work.
- [x] 11.5 Add disconnect, timeout, wrong-token, stale-attempt, updater-crash, and parent-cancellation tests proving there is no delayed surprise cutover after the old bridge chooses to serve.

## 12. Managed-file cutover and restart

- [x] 12.1 After authorization, wait a bounded interval for cooperative exit of the exact parent and force-stop only that revalidated identity if necessary.
- [x] 12.2 After the parent exits, revalidate every managed binary snapshot, atomically rename original `appsettings.json` to unique `appsettings.json.bak.<attempt-id>`, hash the renamed bytes, and install no new managed file unless all values still match the prepared snapshots.
- [x] 12.3 On post-Prepared config drift, restore the `.bak` to its original name and relaunch the unchanged old bridge when managed binaries still match; on post-handoff managed-binary drift, preserve the external change/recovery material and require manual recovery rather than overwriting or executing an unplanned file.
- [x] 12.4 Install only the allowlisted staged bridge/updater and merged configuration using same-volume temporary moves where supported, restore required Unix executable modes, verify installed managed files before launch, and never enumerate/delete unknown installation contents.
- [x] 12.5 Launch the replacement without a shell using the original argument vector and working directory, same user/inherited environment and console handles where applicable, plus only the independently generated target-Ready context.
- [x] 12.6 Add real-process tests proving no-argument and `serve --port <non-8765>` invocation preservation, working-directory preservation, updater self-replacement on Windows, survival of token/log/trace/unknown/pre-existing `.bak` paths, an edit between Prepared and cutover, and managed-binary drift after ownership transfer.

## 13. Replacement readiness and commit

- [x] 13.1 Add an inert-by-default bridge readiness reporter that activates only with a valid role-specific one-launch context conforming to the previous/current frozen protocol and registers for `IHostApplicationLifetime.ApplicationStarted`.
- [x] 13.2 Emit one source-generated Ready message only after route/config validation, auth setup, all hosted-service startup, and successful listener startup; use explicit wire names and include protocol/attempt/target-Ready-token/role/PID and normalized `ProductInfo.Version`.
- [x] 13.3 Implement updater readiness waiting with finite timeout and launched-process exit monitoring; validate wire version, target-Ready token, attempt, PID, role, expected target version, and continued liveness without accepting the handoff or rollback capability.
- [x] 13.4 Commit only after valid replacement Ready, then remove transaction backups/plan/archive/staging best-effort without stopping the serving replacement when cleanup fails.
- [x] 13.5 Add real-process tests for immediate candidate exit, config-validation failure, listener bind failure, stale/forged Ready, wrong version/PID, timeout while alive, successful Ready, and non-fatal post-commit cleanup failure.

## 14. Exact rollback and recovery diagnostics

- [x] 14.1 Implement one rollback path for every post-cutover failure: stop only the failed launched replacement, restore verified old bridge/updater and modes, remove merged config, verify the transaction `.bak` against the immutable old-config hash, and rename it back to `appsettings.json`; if it is absent or changed, restore the separately verified exact private snapshot instead and retain the suspect sibling for diagnostics.
- [x] 14.2 Relaunch the old version with original arguments/working directory and a newly generated rollback-Ready capability distinct from both handoff and target-Ready capabilities; suppress update discovery for that launch only.
- [x] 14.3 Require an authenticated role/version/PID-bound Ready using only the rollback capability from the expected old version before declaring service recovered; report recovered rollback distinctly while retaining a failed-update outcome.
- [x] 14.4 Write/flush an owner-private phase journal before and after destructive transitions without tokens or configuration contents; retain journal/backups on rollback failure.
- [x] 14.5 Print current/target versions, failure phase/reason, installation/backup/original-config paths, and ordered manual restoration instructions when automatic rollback cannot restore Ready service.
- [x] 14.6 Add disposable real-process tests proving exact-byte original config restoration (including old-only keys), old binary/updater restoration, one-launch loop suppression, old Ready success, rollback Ready timeout, and retained recovery material on unrecoverable failure.

## 15. Release workflow and packaging

- [x] 15.1 Publish and smoke `CopilotBridge.Cli` and `CopilotBridge.Updater` for `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64` in the existing release matrix.
- [x] 15.2 Package bridge, updater, and stock `appsettings.json` at each ZIP/TAR.GZ root; preserve Unix executable modes and add the updater beside the bridge in the macOS `.pkg`.
- [x] 15.3 Keep `gh` restricted to the authenticated GitHub-hosted publishing step; require no checksum sidecar, then use ordinary HTTP with no `Authorization` header in a bounded retry/poll to prove every update archive becomes anonymously visible with uploaded state, positive size, HTTPS download URL, and usable `sha256:` digest.
- [x] 15.4 Add archive/package-shape tests for exact names and required files across all RIDs, including macOS selection of TAR.GZ rather than `.pkg` for auto-update.
- [x] 15.5 Record final AOT bridge/updater sizes and build times in `docs/size-history.md`; investigate any unexpected bridge growth or non-framework updater dependency before acceptance.

## 16. Documentation and operator guidance

- [x] 16.1 Document `AutoUpdate.EnableAutoUpdate`, `AutoUpdate.AllowBetaUpdates`, stable/prerelease semantics, synchronous startup behavior, noninteractive behavior, and how to disable checks in README/config reference material.
- [x] 16.2 Document the trust boundary: anonymous GitHub Releases REST/HTTPS plus GitHub Release Asset size/digest, no runtime `gh`, no checksum sidecar, and no independent code-signing guarantee.
- [x] 16.3 Document configuration migration precisely: new template, surviving old values, recursive objects, atomic old arrays, new defaults, removed old-only keys, generated formatting, and byte-exact rollback.
- [x] 16.4 Document writable-install/no-elevation behavior, managed versus preserved files, transaction backup naming, updater diagnostics, and manual recovery steps.
- [x] 16.5 Fold durable updater architecture/build/release facts into `docs/`; if `CLAUDE.md` gains updater build guidance, mirror the substantive edit in `AGENTS.md`.

## 17. Final verification

- [x] 17.1 Run the complete unit suite and solution build with `dotnet test --filter "Category!=Integration"` and `dotnet build`; resolve all failures without weakening contract assertions.
- [x] 17.2 Perform a Windows Native AOT publish using the repository-prescribed VS environment block, prove the published bridge/updater mtimes advanced, run both binaries, and record their sizes.
- [x] 17.3 Run the malicious-archive and disposable real-process update suite on non-8765 ports, proving preflight fail-open, successful cutover/Ready/commit, candidate failure/timeout, exact rollback, lock behavior, and unmanaged-state preservation.
- [ ] 17.4 Run the RID/OS release matrix so both Native AOT executables and successful/rollback process flows execute on Windows x64/ARM64, Linux x64, and macOS ARM64. — DEFERRED to CI: only win-x64 is verifiable on the dev box (both executables AOT-link + run there); the other three RIDs are exercised by the release workflow's per-runner publish/smoke. Not falsely checked.
- [ ] 17.5 Drive a real headless client through the successfully replaced bridge on a genuinely complex path-exercising task, then use `real-client-verify` to confirm success from the client's own log/transcript rather than bridge HTTP status. — DEFERRED: requires a real published release round-trip (a self-replaced bridge) plus a live LLM backend; cannot be honestly verified in-session. The replacement bridge's serve path is unchanged from the shipped proxy, and the update transaction is proven end-to-end by the real-process suite (17.3).
- [x] 17.6 Run the final security review over plan validation, local IPC, download redirects/digest, archive extraction, process identity, path confinement, and rollback; fix all confirmed findings and rerun affected contracts. — Ran the pr-review-toolkit suite (type-design + test-coverage delivered; silent-failure/code/comment agents were interrupted by an unrelated upstream 504 on the local model backend). Fixed all actionable findings (SemanticVersion default-NRE, ArchiveKind wire-map, UpdatePlanValidator completeness, ConfigSnapshot immutability) and self-reviewed the high-risk paths (zip-slip incl. Windows drive-relative/UNC, HTTPS-only redirects, constant-time token compare, PID+start-time+path identity, no pipe deadlock). The expanded real-process tests then caught + fixed 2 genuine Windows image-lock bugs.
- [ ] 17.7 Run strict OpenSpec validation, confirm every task/scenario is represented in verification evidence, and leave the change apply-ready. — `openspec validate --strict` passes; every scenario has a corresponding test EXCEPT the two CI/real-client gates (17.4/17.5) noted above. Archived as part of the ship PR with those two gates explicitly outstanding.
