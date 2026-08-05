## ADDED Requirements

### Requirement: Exact official Codex source resolution

For every valid request, the bridge SHALL resolve the canonical catalog source from the exact complete version as `https://raw.githubusercontent.com/openai/codex/rust-v{complete-version}/codex-rs/models-manager/models.json`. The identity parser SHALL accept a stable or complete SemVer query and SHALL recognize the complete version in a leading `Codex Desktop/{version}`, `codex_cli_rs/{version}`, or headless `codex_exec/{version}` user-agent product. When Codex truncates a prerelease query to `major.minor.patch`, the bridge SHALL use the complete user-agent version only if its three-part core exactly matches the query. An explicit complete query and recognized Codex user-agent version SHALL match exactly. Malformed or contradictory recognized Codex identity SHALL be rejected before network or filesystem access. The bridge MUST NOT substitute a neighboring version, a stable version for a prerelease, a branch, or the newest available tag.

#### Scenario: Stable version resolves exact tag

- **WHEN** Codex requests `client_version=0.147.0`
- **THEN** the bridge resolves only the catalog at tag `rust-v0.147.0`

#### Scenario: Prerelease version resolves exact tag

- **WHEN** Codex requests `client_version=0.147.0` with `User-Agent: Codex Desktop/0.147.0-alpha.1.2 (...)`
- **THEN** the bridge resolves only the catalog at tag `rust-v0.147.0-alpha.1.2` without truncating the prerelease suffix

#### Scenario: Contradictory request identity is rejected

- **WHEN** the query core and recognized Codex user-agent version do not match, or an explicit complete query differs from that user-agent version
- **THEN** the bridge returns a non-2xx metadata error without cache or source I/O

#### Scenario: Missing exact tag is not guessed

- **WHEN** the exact source tag does not exist
- **THEN** the bridge does not request or serve a catalog from an adjacent, stable, latest, branch, or embedded version

#### Scenario: Unsafe version is rejected before I/O

- **WHEN** `client_version` contains a path separator, encoded traversal, URL syntax, whitespace, control character, or otherwise fails the accepted canonical-version grammar
- **THEN** the bridge returns a non-2xx metadata error without issuing a source request or deriving a cache path from the untrusted text

### Requirement: Three-level Codex source cache

The bridge SHALL resolve an exact-version source catalog through three levels in order: a Microsoft `HybridCache` process-memory entry, a validated persistent disk entry, then the canonical GitHub source. Both caches SHALL be keyed by the complete canonical `client_version`; entries for different prerelease, patch, minor, major versions MUST NOT alias. HybridCache SHALL provide per-version stampede protection so concurrent misses or stale revalidations for the same version share one factory execution, while different versions MAY perform network and validation work independently. Every operation that mutates persistent cache state—including record promotion, freshness-metadata replacement, retention deletion, and temporary-file cleanup—SHALL acquire one process-wide asynchronous writer lock, so at most one request mutates the disk cache at any instant. Read-only memory and disk validation MAY proceed concurrently outside that lock. A request SHALL re-check the current destination state after acquiring the writer lock before replacing or deleting it. No `IDistributedCache` SHALL participate in catalog resolution.

#### Scenario: Fresh memory hit performs no lower-level I/O

- **WHEN** a validated exact-version memory entry is within its freshness TTL
- **THEN** the bridge serves it without reading disk or contacting GitHub

#### Scenario: Fresh disk hit repopulates memory

- **WHEN** no memory entry exists and a validated exact-version disk entry is within its freshness TTL
- **THEN** the bridge validates and serves the disk entry, populates memory, and does not contact GitHub

#### Scenario: Cold miss reaches canonical source

- **WHEN** neither memory nor disk contains a validated exact-version entry
- **THEN** the bridge fetches only that version's canonical GitHub source and publishes it only after validation

#### Scenario: Same-version requests are single-flight

- **WHEN** concurrent clients request the same uncached or stale version
- **THEN** the bridge performs at most one source fetch or revalidation for that version and all callers observe one atomically published result

#### Scenario: Versions remain isolated

- **WHEN** clients request `0.147.0-alpha.1.1` and `0.147.0-alpha.1.2`
- **THEN** each request resolves, caches, revalidates, and serves only its own exact-version catalog

#### Scenario: Persistent mutations have one writer

- **WHEN** different client versions finish source resolution or trigger cleanup concurrently
- **THEN** their persistent record writes, atomic replacements, and deletions never overlap in time, while their network fetches and read-only validation may overlap

#### Scenario: Writer rechecks state after waiting

- **WHEN** a request waits for the persistent writer lock while another request publishes or removes a candidate destination
- **THEN** the waiting request re-evaluates the destination under the lock and does not overwrite newer validated state or delete a now-active entry

### Requirement: TTL and conditional source revalidation

Every validated source entry SHALL record a freshness timestamp and source validator. The default source freshness TTL SHALL be 24 hours and SHALL be configurable as a positive bounded duration. When an exact-version entry is stale, the bridge SHALL revalidate the canonical source using `If-None-Match` when a source ETag is available. A `304 Not Modified` SHALL refresh freshness without replacing catalog bytes. A successful `200 OK` SHALL be treated as a possible source change and SHALL replace the last-known-good entry only after full validation. Source freshness and the existing live Copilot capability-overlay TTL SHALL remain separate policies.

#### Scenario: Fresh entry avoids source check

- **WHEN** an exact-version entry is younger than the configured source TTL
- **THEN** no GitHub request is made

#### Scenario: Stale entry is conditionally revalidated

- **WHEN** a stale exact-version entry records a source ETag
- **THEN** the bridge requests the same source URL with `If-None-Match` set to that ETag

#### Scenario: Unchanged source refreshes freshness

- **WHEN** conditional revalidation returns `304 Not Modified`
- **THEN** the bridge retains the existing catalog bytes and digest, updates the validation/freshness metadata atomically, and serves the catalog

#### Scenario: Changed source replaces validated bytes

- **WHEN** revalidation returns `200 OK` with different bytes that pass every source validation
- **THEN** the bridge atomically replaces the disk and memory entry, records the new ETag and SHA-256 digest, and projects the new catalog

#### Scenario: Invalid changed source preserves last known good

- **WHEN** revalidation returns new bytes that fail validation
- **THEN** the bridge does not replace either cache level and serves the stale validated last-known-good entry with a warning

### Requirement: Source catalog integrity and atomic persistence

Before a downloaded catalog becomes usable, the bridge SHALL require a successful HTTP response from the canonical HTTPS source, a bounded response size, a Codex `ModelsResponse` object with a `models` array, unique non-empty slugs, and every entry's required instruction and client-behavior fields. The bridge SHALL compute a SHA-256 digest over the exact source bytes. Persistent metadata SHALL bind at least the canonical client version, canonical source URL, source ETag when present, content digest, fetch time, and validation time to those bytes. Disk publication SHALL use a same-directory temporary file and atomic replacement so interruption cannot expose a partial entry. Cache contents SHALL be treated as untrusted on every process start and MUST NOT use reflection-based JSON serialization.

#### Scenario: Valid source is durably promoted

- **WHEN** canonical source bytes pass size, envelope, entry, and uniqueness validation
- **THEN** the bridge records their digest and metadata, atomically publishes them to disk, and only then exposes the entry through memory

#### Scenario: Oversized or malformed source is rejected

- **WHEN** the source response exceeds the configured bound or is not a valid complete Codex catalog
- **THEN** the bridge aborts that candidate without partially publishing it

#### Scenario: Digest mismatch isolates disk corruption

- **WHEN** a disk entry's bytes do not match its recorded SHA-256 digest or its metadata does not match the requested exact version and source URL
- **THEN** the bridge ignores that entry, logs the integrity failure without logging catalog contents, and continues resolution at the canonical source

#### Scenario: Interrupted write preserves prior entry

- **WHEN** the process terminates or a filesystem error occurs before atomic replacement completes
- **THEN** a previously validated cache entry remains readable and no partial candidate is treated as valid

### Requirement: Exact-version last-known-good degradation

A transient GitHub timeout, DNS/TLS failure, throttling response, server error, missing conditional-response validator, or invalid replacement SHALL NOT discard an already validated exact-version cache entry. The bridge SHALL serve that stale last-known-good catalog and warn that source freshness could not be established. If no validated entry exists for the exact requested version, a source miss or failure SHALL produce a non-2xx metadata response so Codex retains its own bundled catalog; `POST /codex/responses` inference SHALL remain available. A `404` for one exact tag MUST NOT poison other versions.

#### Scenario: Offline restart uses validated disk entry

- **WHEN** the bridge restarts offline with a stale validated disk entry for the requested exact version
- **THEN** it serves that entry, repopulates memory, and logs stale-source use

#### Scenario: Cold offline request falls back at the client

- **WHEN** the bridge is offline and has no validated exact-version entry
- **THEN** `/codex/models` returns a non-2xx metadata error while `/codex/responses` remains mapped, allowing Codex to retain its bundled catalog

#### Scenario: Exact tag absence is version-local

- **WHEN** GitHub returns `404` for one requested version
- **THEN** the bridge returns a non-2xx response for that version without deleting or suppressing valid cache entries for other versions

### Requirement: Persistent cache retention and diagnostics

The persistent source cache SHALL have a documented configurable directory, SHALL use safe deterministic filenames that cannot escape that directory, and SHALL be bounded by configurable retention policy. Cleanup SHALL remove only recognized cache artifacts eligible by age/count and SHALL protect the exact version whose successful resolution triggered cleanup. HybridCache's private memory-key set SHALL NOT be mirrored solely for retention. Cleanup failure SHALL be non-fatal. Logs SHALL identify the canonical client version, cache level, freshness state, source outcome, validation outcome, and abbreviated digest or ETag without logging catalog bodies, authorization headers, GitHub credentials, or Copilot credentials.

#### Scenario: Cache key cannot escape cache root

- **WHEN** the bridge derives storage for a valid client version
- **THEN** every resulting path resolves beneath the configured catalog-cache directory

#### Scenario: Old recognized entries are pruned safely

- **WHEN** cleanup observes recognized catalog artifacts beyond the configured retention horizon other than the current successfully resolved version
- **THEN** it may remove those artifacts without touching unrelated files or currently usable entries

#### Scenario: Cleanup failure does not fail discovery

- **WHEN** retention cleanup cannot delete an eligible file
- **THEN** the bridge logs a warning and continues serving or resolving the requested catalog

#### Scenario: Diagnostic log distinguishes cache outcomes

- **WHEN** resolution completes through memory, disk, GitHub 304, GitHub 200, or stale fallback
- **THEN** the log records that outcome and version without recording source contents or credentials

## MODIFIED Requirements

### Requirement: Codex-native model discovery endpoint

The bridge SHALL serve `GET /codex/models?client_version=<version>` at the path formed by Codex from the managed provider base URL when `Codex.ModelCatalog.Enabled` is true. The option SHALL default to true both in code and in the stock `appsettings.json`, so upgraded installations that do not yet carry the key retain discovery. A successful response SHALL use Codex's `ModelsResponse` envelope with a `models` array of complete Codex `ModelInfo` entries obtained from the requested version's validated official-source catalog, SHALL be valid under Native AOT, and SHALL NOT expose GitHub Copilot's incompatible raw model envelope.

#### Scenario: Exact Codex client requests its catalog

- **WHEN** a Codex client requests `/codex/models` with one valid canonical `client_version` and the bridge resolves a validated exact-version source catalog
- **THEN** the bridge returns HTTP 200 with a Codex-compatible `{ "models": [...] }` body and a projected catalog ETag

#### Scenario: Raw Copilot schema is not leaked

- **WHEN** the bridge builds a Codex catalog from an official Codex source catalog and a Copilot `/models` response
- **THEN** the client response contains Codex fields such as `slug`, instruction metadata, and `context_window`, and does not substitute Copilot's `data`, `capabilities`, or `supported_endpoints` entry shape

#### Scenario: Invalid client version fails safely

- **WHEN** `client_version` is absent, repeated, malformed, or cannot be resolved from either a validated exact-version cache entry or its exact canonical source
- **THEN** the bridge returns a non-2xx metadata error without changing inference state, allowing Codex to retain its bundled catalog

#### Scenario: Operator disables model catalog discovery

- **WHEN** `Codex.ModelCatalog.Enabled` is false at bridge startup
- **THEN** `/codex/models` is not mapped while `POST /codex/responses` remains available, so Codex safely retains its bundled catalog

### Requirement: Catalog baseline is client-version compatible

The bridge SHALL construct every successful response from the complete official Codex catalog whose exact source tag corresponds to the complete requested client version. The source identity and digest SHALL be retained with the cache entry. The bridge MUST NOT silently serve an embedded snapshot, the newest cached catalog, or any other version when the exact requested version is unavailable. Source entries SHALL remain opaque Codex-owned JSON except for the explicit backend-owned projection allow-list.

#### Scenario: New exact version needs no bridge release

- **WHEN** a valid Codex version not previously seen by this bridge has a valid catalog at its exact official source tag
- **THEN** the bridge resolves, validates, caches, projects, and serves that catalog without requiring an embedded baseline or bridge update

#### Scenario: Matching cache preserves exact official baseline

- **WHEN** a validated cache entry exists for the exact complete `client_version`
- **THEN** every returned model starts from that entry's complete official Codex model object for the same slug

#### Scenario: Unknown version is not guessed

- **WHEN** the exact requested version has neither a validated cache entry nor a valid canonical source catalog
- **THEN** the endpoint fails safely rather than projecting another version's instructions or model fields into the client

### Requirement: Real Codex proves the larger window at the client boundary

Acceptance SHALL include real headless Codex behavior runs through bridge subprocesses on non-default ports. The primary task SHALL cause a Codex version not embedded in the bridge to fetch its exact official provider catalog, carry active context beyond the former 272,000-token catalog ceiling, and complete a multi-step tool workflow. A second run SHALL prove an offline bridge restart can serve the same version from validated disk cache. The verdict MUST use Codex's own structured dispatch log, not only bridge status codes or traces.

#### Scenario: Unseen-version long-context tool task succeeds

- **WHEN** a real Codex client whose exact catalog was not embedded in the bridge runs the path-exercising long-context scenario through the configured bridge
- **THEN** its own log records the uplifted model context, successful tool execution output, and no router, incompatible-payload, aborted-dispatch, or equivalent fatal

#### Scenario: Offline restart retains client behavior

- **WHEN** the same real Codex scenario is repeated after restarting the bridge with GitHub source access unavailable and a validated disk entry present
- **THEN** Codex again observes the uplifted exact-version catalog and completes the tool workflow without a dispatch fatal
