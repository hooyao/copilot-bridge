## 1. Resolve final storage policy and test contracts

- [x] 1.1 Resolve the two design open questions (default cache root and whether first publication requires durable promotion), record the decisions in `design.md`, and define bounded defaults for source TTL, fetch timeout, maximum source bytes, retention age, and retained version count.
- [x] 1.2 Write contract-first unit tests for canonical stable/prerelease query plus Codex User-Agent identity resolution and exact tag/source resolution, including mutation cases that reject mismatched identity, traversal, encoded separators, URL syntax, whitespace/control characters, non-canonical versions, and any adjacent-version fallback.
- [x] 1.3 Write contract-first tests for cache option binding/validation and safe deterministic cache paths that always resolve beneath the configured root.

## 2. Exact-version source resolver

- [x] 2.1 Implement the canonical complete-version value/parser and fixed official source URI builder without reflection or untrusted string-to-path composition.
- [x] 2.2 Register a dedicated anonymous GitHub raw-content `HttpClient` with bounded connect/request behavior and no Copilot/GitHub auth headers, redirects to untrusted origins, or dependency on `AuthService`.
- [x] 2.3 Implement bounded streaming source download, ETag capture, `If-None-Match` conditional requests, `304` handling, SHA-256 calculation over exact bytes, and distinct outcomes for 200/304/404/429/5xx/timeout/transport failure.
- [x] 2.4 Add deterministic resolver tests covering exact stable and prerelease URLs, request headers, response-size enforcement, source status classification, cancellation, and absence of credentials; mutation-check every security/fallback assertion.

## 3. Complete catalog validation and cache record

- [x] 3.1 Generalize `CodexCatalogBaseline` provenance from embedded interval metadata to exact client version, canonical source URL, source ETag, digest, fetch time, and validation time while preserving opaque complete model JSON.
- [x] 3.2 Implement complete catalog validation for envelope shape, unique non-empty slugs, instruction sources, required behavior/context/API fields, and review-override referential integrity while allowing unknown future properties.
- [x] 3.3 Define the versioned single-record disk framing and source-generated cache metadata DTOs; add every new JSON type to `Models/JsonContext.cs` and verify the implementation is Native AOT safe.
- [x] 3.4 Implement bounded disk-record parsing that revalidates magic/version/lengths/version/source identity/SHA-256/catalog schema and treats every persisted record as untrusted.
- [x] 3.5 Implement same-directory temporary writes, flush, and atomic destination promotion/replacement on Windows, Linux, and macOS, preserving an older last-known-good record on every failed or interrupted candidate write.
- [x] 3.6 Add corruption and crash-safety tests for truncated framing, malicious lengths, metadata/body mismatch, digest mismatch, wrong version/source, duplicate slugs, missing behavior fields, invalid review targets, leftover temporary files, disk-full/permission failures, and interrupted promotion; mutation-check that no invalid candidate becomes observable.

## 4. Three-level cache orchestration

- [x] 4.1 Implement immutable process-memory entries keyed by complete canonical client version and fresh-memory/fresh-disk/source resolution ordering.
- [x] 4.2 Implement per-version single-flight fetch/revalidation with independent concurrency across versions and guaranteed removal of completed/failed in-flight state, plus one process-wide asynchronous writer lock that serializes every persistent record promotion, freshness update, cleanup deletion, and temporary-file mutation across versions.
- [x] 4.3 Implement the source TTL state machine: fresh return, stale ETag revalidation, 304 metadata refresh, same-digest 200 refresh, changed valid 200 atomic promotion, and changed invalid 200 last-known-good preservation.
- [x] 4.4 Implement exact-version stale-if-error for transient source failures and invalid replacements; implement cold non-2xx metadata failure for missing tag/no validated entry without affecting `/codex/responses`.
- [x] 4.5 Implement bounded retention cleanup for recognized inactive records and temporary remnants, excluding current memory/in-flight entries and treating enumeration/deletion failure as non-fatal.
- [x] 4.6 Add deterministic clock/concurrency tests for TTL boundaries, memory and restart disk hits, 304, changed source, stale offline fallback, cold offline fallback, 404 isolation, same-version coalescing, cross-version network independence, global single-writer disk mutation, post-lock destination rechecks, retention, and cleanup failures; mutation-check hit ordering, lock release, and stale semantics.

## 5. Endpoint and projector integration

- [x] 5.1 Replace `CodexCatalogBaselineStore` interval selection in `CodexModelsEndpoint` with asynchronous exact-version source-cache resolution while retaining exactly-one-query validation, AOT serialization, projected response ETag, cancellation, and catalog-disable behavior.
- [x] 5.2 Adapt `CodexCatalogProjector` to derive its response ETag from the exact official source identity/digest plus the effective Copilot overlay, preserving every Codex-owned unknown field and changing only the existing allow-list.
- [x] 5.3 Keep official-source freshness independent from `CodexCatalogOverlayService`'s live per-account Copilot TTL and prove cold/stale failures in either layer degrade according to their separate contracts.
- [x] 5.4 Add structured diagnostics for version, cache level, freshness, source/revalidation outcome, validation outcome, elapsed time, and abbreviated digest/ETag; add tests proving catalog bodies, authorization values, GitHub tokens, and Copilot tokens never appear.
- [x] 5.5 Update DI/options/appsettings configuration and validation, keeping `Codex.ModelCatalog.Enabled` default-on and ensuring metadata services do not enter the request inference path.

## 6. Contract and real-client verification

- [x] 6.1 Replace interval-bound endpoint/unit fixtures with captured official stable and prerelease catalogs and add tests that an unseen exact version succeeds without any embedded baseline.
- [x] 6.2 Add an API-contract source-cache test covering first online fetch, disk publication, memory hit, conditional revalidation, source change, bridge restart, and deliberately unavailable GitHub with exact-version disk fallback.
- [x] 6.3 Add a pinned live-source probe for an official Codex tag to verify raw URL availability and ETag/conditional behavior; tag it `Category=Integration` and `Kind=ApiContract` so CI remains network-independent.
- [x] 6.4 Run the pure unit suite and solution-wide non-integration suite, fixing product code rather than weakening contract assertions.
- [x] 6.5 Run the real `Kind=ClientBehavior` Codex scenario with a client version not embedded in the bridge, force active context past 272k, execute a complex multi-step/multi-tool task that exercises catalog discovery, and verify success from Codex's own `logs_2.sqlite` dispatch evidence.
- [x] 6.6 Restart the bridge with GitHub source access deliberately unavailable and rerun the same real-client path from validated disk cache; verify the uplifted effective context, actual tool output, and absence of router/dispatch/incompatible-payload/aborted fatals from Codex's own log.

## 7. Migration, documentation, and release fitness

- [x] 7.1 Remove runtime embedded `Catalogs/Codex/<minor>` resources, interval provenance selection, and obsolete catalog-refresh maintenance once all fixtures use explicit captured test assets.
- [x] 7.2 Update `docs/pipeline-design.md` first, then `docs/codex-implementation-design.md`, `docs/codex-protocol-research.md`, `README.md`, appsettings comments, and troubleshooting/configuration text with the exact-source three-level cache, defaults, stale behavior, privacy, and operational diagnostics.
- [x] 7.3 Update or retire `scripts/update-codex-catalog.ps1`, document any retained fixture-refresh use, and remove prose that tells maintainers to release a new bridge for each Codex client interval.
- [x] 7.4 Publish the win-x64 Native AOT bridge with the verified Windows linker environment, require zero trim/AOT warnings, record the executable-size delta in `docs/size-history.md`, and verify release packaging remains complete.
- [x] 7.5 Run `openspec validate make-codex-catalog-version-independent --strict`, reconcile the completed delta spec into durable architecture/docs as appropriate, and leave the change ready for archive only after every real-client acceptance item is evidenced.

## 8. Simplify the process cache with Microsoft HybridCache

- [x] 8.1 Add contract-first tests proving HybridCache owns fresh-memory hits and same-version single-flight, stale source timestamps force a coordinated factory refresh, different versions remain concurrent, failed resolutions are not retained, and no distributed cache participates.
- [x] 8.2 Replace the private memory/in-flight dictionaries and memory-retention state in `CodexCatalogSourceCache` with Microsoft `HybridCache`, leaving the factory as the straight-line disk → GitHub resolver and retaining the separate process-wide persistent-writer lock.
- [x] 8.3 Update durable architecture text, run unit/non-integration and real online/offline Codex acceptance, publish win-x64 Native AOT with zero trim/AOT warnings, record the size delta, and re-run strict OpenSpec validation.

## 9. PR review follow-ups

- [x] 9.1 Keep stale-if-error inside HybridCache's shared factory outcome so coalesced waiters do not launch duplicate revalidation, and add the failure-path concurrency contract test.
- [x] 9.2 Preserve the client-owned verdict contract by migrating every Codex `Kind=ClientBehavior` actuator to app-server with an isolated `logs_2.sqlite` and exact thread id.
- [x] 9.3 Remove untrusted catalog identifiers from validation exceptions, restore the README configuration table, use OS-appropriate fixture path containment, and validate fixture bytes against recorded capture provenance.
- [x] 9.4 Preserve captured official `models.json` as byte-exact Git content across Windows/Linux checkouts so recorded SHA-256 validation is platform-independent.
