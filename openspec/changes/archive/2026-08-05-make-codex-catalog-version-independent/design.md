## Context

PR #59 added a Codex-native `GET /codex/models?client_version=...` control plane so the bridge can raise Codex's stale bundled context ceiling using live Copilot capacity facts. Its safety model vendors a complete upstream Codex `models.json` for a reviewed client-version interval, because Codex replaces an entire same-slug `ModelInfo`; returning only context fields would erase client-owned instructions, tool mode, collaboration metadata, and future behavior fields.

That preservation rule is sound, but embedding the baseline couples the bridge release train to Codex's faster one. The current bridge contains only a `rust-v0.144.1` snapshot declared for `[0.144.0, 0.145.0)`. Codex Desktop `0.147.0-alpha.1.2` sends `client_version=0.147.0` because `client_version_to_whole()` deliberately strips prerelease identifiers, while its `User-Agent` carries `Codex Desktop/0.147.0-alpha.1.2 (...)`. The bridge currently cannot use either signal and falls back to the 272k bundled catalog even though the installed bridge and Copilot backend both support the 1M-class models.

The official Codex repository tags releases as `rust-v{version}` and stores the complete client catalog at `codex-rs/models-manager/models.json`. For example, client `0.147.0-alpha.1.2` maps to:

```text
https://raw.githubusercontent.com/openai/codex/
  rust-v0.147.0-alpha.1.2/
  codex-rs/models-manager/models.json
```

The bridge is a portable Native AOT executable, must use source-generated JSON paths, and must keep inference working when metadata resolution fails. Catalog source I/O is therefore demand-driven by `/codex/models`, bounded, cached, and isolated from `POST /codex/responses`.

## Goals / Non-Goals

**Goals:**

- Remove routine bridge releases from the Codex catalog compatibility loop.
- Preserve complete client-version-specific Codex behavior while overlaying only live, probe-grounded Copilot backend facts.
- Resolve stable and prerelease clients by exact official tag without guessing compatible neighbors.
- Implement memory, persistent disk, and canonical GitHub source as a coherent three-level cache with TTL and ETag revalidation.
- Detect same-tag source changes and atomically promote only validated last-known-good bytes.
- Continue serving an exact-version stale validated cache during transient source failures and cold-fail only the metadata request.
- Bound network waits, source size, disk usage, and concurrency while retaining AOT safety and credential isolation.
- Prove the new path with a real unseen-version Codex client and with an offline restart from disk cache.

**Non-Goals:**

- Defining a sparse model-overlay protocol that Codex does not currently support.
- Predicting compatibility across a future breaking `ModelInfo` schema change. Such a change fails validation safely and may still require bridge work; normal release-number churn does not.
- Discovering or enabling a Copilot model that lacks an exact Codex catalog entry and a probe-grounded bridge Responses profile.
- Using a branch, GitHub's latest release, a nearby version, or an older embedded snapshot when the exact tag is unavailable.
- Making GitHub availability a prerequisite for normal inference or bridge startup.
- Forwarding the GitHub/Copilot credential to GitHub raw content; source requests are anonymous and separate from `AuthService`.
- Turning the cache into a general-purpose HTTP cache or package manager.

## Decisions

### 1. Resolve an exact canonical tag from a strict complete version

The query `client_version` and, when present, the leading Codex `User-Agent` product version are parsed as canonical ASCII semantic versions. Stable Codex sends the same three-part version in both places. Prerelease Codex deliberately sends only `major.minor.patch` in the query but retains the complete version in a `Codex Desktop/{version}`, `codex_cli_rs/{version}`, or headless `codex_exec/{version}` user agent. The bridge accepts that complete user-agent identity only when its three-part core exactly matches the query; an explicit complete query must equal the user-agent version exactly. A malformed or contradictory recognized Codex user agent is rejected before I/O. Non-Codex clients and older clients without a recognized user agent retain exact query-only behavior.

The resulting complete canonical version is retained for source identity, while unsafe characters and non-canonical alternatives are rejected before any I/O. The source URI is formed from the fixed HTTPS origin, repository, tag prefix, and file path; the version occupies one escaped tag segment only:

```text
https://raw.githubusercontent.com/openai/codex/rust-v{version}/codex-rs/models-manager/models.json
```

The cache identity is the complete version, not only major/minor/patch. `0.147.0-alpha.1.1`, `0.147.0-alpha.1.2`, and `0.147.0` are independent entries. A missing exact tag is an unavailable catalog, never permission to try another tag.

This makes source selection deterministic, auditable, and immune to path/URL injection. It also preserves Codex-owned behavior for the exact binary asking the question.

Alternative considered: widen each embedded minor interval or use the newest known baseline. Rejected because a remote same-slug entry replaces the whole client's entry and can silently downgrade new instructions or tool behavior.

Alternative considered: resolve the latest Codex tag from the GitHub Releases API. Rejected because latest is not necessarily the requesting binary, prerelease channels diverge, and the API adds rate-limit and ambiguity without solving compatibility.

Alternative considered: enumerate all tags sharing the query's three-part core and select the newest prerelease. Rejected because `0.147.0` has many alpha tags and the server cannot infer which full catalog belongs to the requesting binary. The corroborated user agent supplies the exact identity without tag search.

### 2. Use a three-level read-through, stale-if-error cache

Resolution order for a canonical version is:

```text
request
   │
   ├─ fresh validated memory entry ───────────────▶ return
   │
   ├─ fresh validated disk entry ──▶ memory ─────▶ return
   │
   └─ stale/missing
         │
         └─ exact GitHub source revalidation/fetch
               ├─ 304 ─▶ refresh metadata ───────▶ return cached bytes
               ├─ valid 200 ─▶ disk ─▶ memory ───▶ return new bytes
               └─ failure/invalid
                     ├─ exact stale LKG exists ──▶ return stale + warning
                     └─ no exact LKG ─────────────▶ metadata non-2xx
```

Memory holds parsed immutable `CodexCatalogBaseline` objects plus source metadata. Disk survives bridge restarts. GitHub remains the canonical source, not a replicated source of bridge-generated projections. Copilot capacity facts retain their existing separate five-minute process cache because they vary by account and time; official Codex source freshness defaults to 24 hours because release tags are expected to be stable.

Per-version resolution is single-flight. Microsoft `HybridCache` coalesces simultaneous requests for the same version key, but does not serialize unrelated client versions. Failed work is surfaced through an internal exception so HybridCache does not retain it and later requests can retry.

Persistent mutation has an additional, deliberately broader boundary: one process-wide asynchronous writer lock covers record promotion, 304 freshness-metadata replacement, retention deletion, and temporary-file cleanup for every version. Network downloads, parsing, hashing, and read-only disk validation stay outside this lock, so different versions can make useful progress concurrently; only the short disk mutation phase is serialized. After acquiring the lock, a request re-reads or re-checks the destination metadata before committing, because another waiter may have published newer state. Lock acquisition observes request cancellation, lock release is unconditional, and cleanup never recursively acquires the writer lock.

Alternative considered: memory-only caching. Rejected because every bridge restart would require GitHub and an offline restart would lose 1M discovery.

Alternative considered: disk-only caching. Rejected because `/models` can be requested concurrently and repeated parsing, hashing, and disk reads are unnecessary.

Alternative considered: prefetch at bridge startup. Rejected because the bridge does not know which CLI/Desktop versions will connect and startup/inference must not depend on GitHub.

### 3. Revalidate stale source entries with ETag and digest

The default `Codex.ModelCatalog.SourceTtlHours` is 24, bounded to 1–168 hours. The dedicated source fetch timeout defaults to 10 seconds and is bounded to 1–60 seconds. The source-body bound defaults to 4 MiB and is configurable from 64 KiB through 16 MiB. Before TTL expiry, memory or disk returns without network access. After expiry:

- If the stored source supplied an ETag, send `If-None-Match` to the same canonical URL.
- On `304 Not Modified`, preserve bytes/digest and atomically update the source ETag if returned plus fetch/validation timestamps.
- If no ETag exists, or the source returns `200`, download with a strict response-size bound and compute SHA-256 over the exact bytes.
- If a `200` digest equals the cached digest, retain the parsed catalog and refresh metadata.
- If the digest differs, fully validate and parse the candidate before promotion.

The projected client response keeps its current independent ETag, derived from the official source identity/digest and effective Copilot overlay. A source ETag is an upstream revalidation token and is never reused as the response ETag.

Although release tags are expected to be immutable, the design does not assume that operationally. Revalidation detects retagging or source correction; validation prevents a malformed change from replacing a known-good catalog.

Alternative considered: never revalidate a version tag. Rejected because it cannot detect source changes and gives no operator-controlled freshness policy.

Alternative considered: poll GitHub on every `/models`. Rejected because it adds latency, wastes bandwidth, and increases anonymous rate-limit exposure.

### 4. Persist one self-validating record with atomic replacement

Each version has one cache record beneath a configured directory. The default is a per-user OS cache location so installed binaries do not require write access beside the executable: `%LOCALAPPDATA%\copilot-bridge\codex-catalogs` on Windows, `$XDG_CACHE_HOME/copilot-bridge/codex-catalogs` (falling back to `~/.cache/...`) on Linux, and `~/Library/Caches/copilot-bridge/codex-catalogs` on macOS. `Codex.ModelCatalog.CacheDirectory` accepts an absolute operator override; relative override values are rejected to avoid dependence on the launch working directory. The filename is derived from a fixed prefix plus SHA-256 of the canonical version; the raw version is metadata, not a path component. On load, the resolved absolute path is verified to remain under the configured cache root.

The record is a small versioned binary framing:

```text
magic + format-version
metadata-length
source-byte-length
source-generated JSON metadata
exact upstream models.json bytes
```

Metadata contains the canonical client version, canonical source URL, optional source ETag, SHA-256, fetched-at UTC, and validated-at UTC. Lengths are bounded before allocation. The digest is recomputed and all identity/schema checks rerun on every process-start disk load; a locally writable cache is never trusted merely because it exists.

Writes create a uniquely named temporary record in the same directory, flush the completed record, then atomically replace/rename the destination while holding the process-wide writer lock. The old destination stays authoritative until the final promotion. Temporary remnants are never read as cache entries and may be cleaned later. First publication requires successful durable promotion before memory publication or an HTTP 200 catalog response, so memory, disk, and the response always identify restart-durable last-known-good source bytes. If disk persistence fails while an older LKG exists, that older entry remains the fallback; with no LKG, the metadata request fails safely and Codex retains its bundled catalog.

This single-record layout avoids the torn-pair problem of replacing `models.json` and `metadata.json` separately. It also preserves exact source bytes for hashing and future diagnostics without base64 expansion.

Alternative considered: two loose JSON files. Rejected because no cross-platform primitive atomically replaces a pair, so a crash can expose new source bytes with old metadata.

Alternative considered: serialize parsed `JsonElement` models back into a cache envelope. Rejected because it loses exact upstream bytes and makes digest/source-change interpretation depend on bridge serialization.

### 5. Validate a complete opaque Codex catalog before projection

The source response has a conservative maximum size (default 4 MiB) and a bounded fetch timeout. Validation requires:

- HTTP success from the exact canonical HTTPS source;
- a top-level object with a `models` array;
- a non-empty unique `slug` for every model;
- complete instruction source and the behavior fields the current projector/runtime requires;
- structurally valid context/visibility/API fields;
- referentially valid review overrides where applicable.

Unknown properties are retained byte-semantically through cloned `JsonElement` values and the existing writer-based projection. Validation deliberately does not reject new unknown fields. Only the existing explicit projection allow-list can change availability, `context_window`, `max_context_window`, `auto_compact_token_limit`, and an invalid review target. A catalog schema that removes/changes a required current field fails closed rather than receiving guessed defaults.

The DTOs for cache metadata and any source envelope are registered in `Models/JsonContext.cs`; arbitrary reflection serialization remains forbidden under Native AOT.

Alternative considered: construct minimal same-slug entries from Copilot data. Rejected because Copilot does not own Codex instructions, code-mode, shell-tool, collaboration, picker, or compatibility semantics.

Alternative considered: accept any parseable JSON to maximize forward compatibility. Rejected because a syntactically valid but incomplete remote replacement can silently disable client behavior.

### 6. Separate transient failure, exact absence, and invalid input

Malformed or repeated `client_version` remains a client error and causes no I/O. An exact-tag `404`, cold network failure, oversized source, or invalid candidate with no exact LKG returns a non-2xx metadata error; Codex then keeps its own bundled catalog. A transient timeout, DNS/TLS error, 429, 5xx, or invalid changed candidate with a stale exact LKG serves that stale entry and logs the failure. A 404 is version-local and never evicts other versions.

Serving stale is unbounded by age for request-time availability but bounded by disk retention. This distinction is intentional: a validated exact-version catalog is behaviorally safer than substituting another version, and tag content normally does not expire semantically. Operators can bound stored history separately without turning a temporary outage into immediate loss.

`POST /codex/responses`, authentication, routing, and the bridge process remain independent of every source-cache outcome.

Alternative considered: fail every stale revalidation error. Rejected because it makes an already validated exact client lose capacity during a GitHub incident.

Alternative considered: fall back to the old embedded 0.144 baseline. Rejected because it recreates the unsafe cross-version substitution this change removes.

### 7. Bound retention, cleanup, and observability

The disk cache defaults to a 90-day retention horizon (bounded to 1–365 days) and at most 32 completed version records (bounded to 1–256). Cleanup runs opportunistically after a successful resolution and never blocks the response. It recognizes only the bridge's fixed record/temp naming pattern, resolves each candidate beneath the cache root, and excludes the version whose successful resolution triggered cleanup. Age pruning happens before count pruning among other records. All deletions share the same process-wide writer lock as promotion and re-check current-version eligibility after acquiring it. HybridCache intentionally does not expose or mirror its internal L1 key set; evicting another inactive disk record may require GitHub again after that version's memory TTL or a restart, which is normal bounded-cache behavior. Failure to enumerate or delete is a warning, not a metadata failure.

Logs record version, `memory|disk|source|stale` outcome, fresh/stale state, HTTP outcome, validation decision, elapsed time, and abbreviated digest/ETag. They do not record catalog bodies, authorization headers, source response bodies, GitHub credentials, or Copilot credentials. Metrics are optional; stable structured log events are required.

Alternative considered: retain every version forever. Rejected because alpha channels can create many exact tags and the single-binary bridge should not grow unbounded local state.

### 8. Keep official-source and live-Copilot caches independently testable

The source resolver accepts injected clock, source HTTP client, cache root/filesystem seam, and limits so unit tests can deterministically cover TTL boundaries, ETag requests, 304, changed 200, corruption, interrupted promotion, concurrency, and cleanup without network access. The projector continues to accept a resolved official baseline plus the existing live Copilot overlay; it does not fetch either source itself.

Contract tests use captured official catalog bytes. A live source smoke confirms the exact raw GitHub URL for a pinned version, but routine unit/CI tests do not depend on GitHub. Acceptance drives a real installed Codex version that was not embedded in the bridge, verifies the catalog request and >272k effective window, and reads Codex's own log after a multi-tool task. A second run restarts the bridge with source access deliberately unavailable and verifies disk-cache behavior at the client boundary.

### 9. Delegate process memory and per-version single-flight to Microsoft HybridCache

The process-memory level uses the .NET `Microsoft.Extensions.Caching.Hybrid.HybridCache` implementation instead of maintaining a private `ConcurrentDictionary` plus an in-flight `Lazy<Task<...>>` table. The canonical complete client version is the HybridCache key. Its local cache supplies the first level and its built-in stampede protection ensures that only one factory executes for concurrent requests for the same version, while different version keys remain independent.

No `IDistributedCache` is registered for catalogs. The persistent file is intentionally inside the HybridCache factory rather than configured as its secondary cache: an expired but validated exact-version record must remain observable as stale-if-error during a GitHub outage, whereas ordinary cache expiration treats it as a miss. The factory therefore owns only the straight-line disk → GitHub resolution and returns an immutable resolution for HybridCache to retain in memory. Distributed-cache reads and writes are explicitly disabled for this call so a future unrelated `IDistributedCache` registration cannot alter catalog semantics. HybridCache serializes factory values before publishing them to L1 even when L2 is disabled, so the catalog registers a trim-safe local-only serializer whose deserialize path fails closed if this no-L2 contract is violated; the validated file remains the only persistent representation.

The HybridCache local expiration is the source TTL. The returned entry still carries its authoritative source timestamp, and the resolver checks that timestamp so a record loaded late in its disk lifetime is not granted a fresh full TTL. When an observed memory entry is stale, the resolver calls the same canonical version key with `DisableLocalCacheRead`; HybridCache coordinates that refresh by key and replaces the local value after the factory completes. A stale fallback retains its old source timestamp, so the next request bypasses it and retries GitHub rather than treating fallback as freshly validated. The process-wide disk-writer `SemaphoreSlim` remains separate and unchanged because it enforces the stronger operator requirement that persistent mutations for different versions never overlap.

HybridCache 10.8.0 still ships a reflection-based `DefaultJsonSerializerFactory` and suppresses its own source warnings. `AddHybridCache` registers that fallback with `TryAddSingleton`, so the bridge registers an explicit-only factory first and supplies a dedicated serializer for `CodexCatalogResolution`; runtime DI tests prove that the default factory is absent, the explicit serializer resolves, and no `IDistributedCache` exists. A detailed Native AOT audit reports three `IL2026`, three `IL3050`, and three `IL2070` diagnostics, all from that fallback (dotnet/extensions#5624; the proposed fix in #6475 was closed unmerged). NativeAOT 10.0 cannot consume ILLink's external member-level suppression attributes, so the project keeps every warning ID enabled and substitutes fail-closed throwing bodies only for the four diagnostic-producing fallback members in the AOT input. The package version and exact assembly SHA-256 are build guards; either changing forces a fresh audit. A negative mutation probe proves unrelated `IL2026`/`IL3050` diagnostics still fail the warnings-as-errors publish.

Alternative considered: expose the catalog file store as `IDistributedCache`. Rejected because its expiration contract would hide the exact stale last-known-good record needed for offline degradation.

Alternative considered: keep the private memory and in-flight dictionaries alongside HybridCache. Rejected because that duplicates the framework service and recreates the over-engineered state machine this decision removes.

## Risks / Trade-offs

- **[Official-source availability or anonymous throttling]** → Use a 24-hour TTL, ETag conditional requests, per-version single-flight, persistent LKG, bounded timeout, and client-side bundled fallback on a cold miss.
- **[Upstream tag is moved or compromised]** → Bind exact HTTPS source identity, record ETag and SHA-256, detect changes, fully revalidate before promotion, preserve the prior LKG on invalid changes, and log digest transitions. This does not cryptographically attest GitHub beyond TLS; adding signature verification requires upstream signed catalog artifacts and is out of scope.
- **[Future Codex schema breaks current required fields]** → Preserve unknown fields but reject missing/incompatible required behavior fields. This may require a bridge update for a genuinely breaking schema, but not for ordinary version releases.
- **[Cache directory is locally modified]** → Treat disk records as untrusted, verify framing/length/identity/digest/schema on every load, and never execute catalog data.
- **[Crash or disk-full during persistence]** → Same-directory temporary write plus atomic promotion keeps the old LKG authoritative; temporary files are ignored and cleanup is best effort.
- **[Many prerelease versions consume disk]** → Exact-version keying is required for correctness; age and count retention bound storage without guessing compatibility.
- **[Serving very old stale metadata after a prolonged outage]** → The exact official client catalog remains safer than a neighboring version; live Copilot route/availability is still re-evaluated separately, and retention provides the operator's bound.
- **[Metadata fetch latency on first unseen version]** → Fetch only on demand, apply a dedicated short timeout and size limit, then persist for subsequent requests/restarts. Inference remains independent.
- **[Default or configured cache directory is not writable]** → Use a per-user OS cache root by default and allow an absolute override. Because first publication requires durable promotion, a cold write failure returns a metadata error; an existing validated LKG remains usable.

## Migration Plan

1. Add the exact-version parser/source resolver, cache record format, options validation, dedicated anonymous source HTTP client, and deterministic unit-test seams without changing endpoint selection.
2. Add disk validation/atomic promotion, TTL/ETag revalidation, per-version single-flight, stale-if-error, retention cleanup, and structured diagnostics.
3. Change `CodexModelsEndpoint` to resolve an exact official baseline and remove the runtime dependency on embedded interval selection; retain the current embedded 0.144 asset temporarily only as a test fixture during migration.
4. Extend contract tests with stable and prerelease versions, captured source bytes, corruption and failure cases, and mutation-check all cache/source assertions.
5. Run the live exact-source contract probe and the required real Codex unseen-version + offline-restart behavior tests, reading Codex's own logs for the verdict.
6. Remove embedded catalog resources and the catalog-refresh script/workflow once no runtime or documentation dependency remains; update `docs/pipeline-design.md`, Codex design/protocol docs, README, configuration reference, and size history.
7. Publish Native AOT and verify all supported RIDs in CI. Existing Codex configuration remains valid because the provider URL and command-auth discovery trigger do not change.

Rollback is configuration-safe: disabling `Codex.ModelCatalog.Enabled` removes only `/codex/models`, causing Codex to use its own bundled catalog while inference continues. Rolling back the bridge leaves cache files inert; older versions do not read them. Cache deletion is optional and not part of rollback.

## Open Questions

- Confirm raw.githubusercontent.com's ETag and conditional-request behavior with a live pinned-tag probe on all supported CI operating systems; full-download digest comparison remains the fallback when no usable ETag is supplied.
