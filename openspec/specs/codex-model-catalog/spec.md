# codex-model-catalog Specification

## Purpose

Define how the bridge resolves the exact official Codex-native model catalog,
caches and revalidates it safely, overlays validated GitHub Copilot backend
capacity without leaking credentials, and preserves otherwise healthy Codex
inference when metadata sources are unavailable.
## Requirements
### Requirement: Exact official Codex source resolution

For every valid request, the bridge SHALL resolve the canonical catalog source from the exact complete version as `https://raw.githubusercontent.com/openai/codex/rust-v{complete-version}/codex-rs/models-manager/models.json`. The identity parser SHALL accept a stable or complete SemVer query and SHALL recognize the complete version in a leading `Codex Desktop/{version}`, `codex_cli_rs/{version}`, or headless `codex_exec/{version}` user-agent product. When Codex truncates a prerelease query to `major.minor.patch`, the bridge SHALL use the complete user-agent version only if its three-part core exactly matches the query. An explicit complete query and recognized Codex user-agent version SHALL match exactly. Malformed or contradictory recognized Codex identity SHALL be rejected before network or filesystem access. The bridge MUST NOT substitute a neighboring version, a stable version for a prerelease, a branch, or the newest available tag. When the exact tag is confirmed absent, the bridge MAY serve the single compile-time bundled snapshot under the separate bundled-fallback requirements; that snapshot is a fixed reviewed artifact and its use MUST NOT be implemented as a search over, or comparison between, available tags.

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
- **THEN** the bridge does not request or serve a catalog from an adjacent, stable, latest, or branch version, and serves only either the compile-time bundled snapshot under its own gating requirements or a non-2xx metadata error

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

Before a downloaded catalog becomes usable, the bridge SHALL require a successful HTTP response from the canonical HTTPS source, a bounded response size, a Codex `ModelsResponse` object with a `models` array, unique non-empty slugs, and every entry's required instruction and client-behavior fields. An entry's instruction source SHALL be satisfied by EITHER a non-empty top-level `base_instructions` string OR a non-empty `model_messages.instructions_template` string, because Codex relocated the model prompt between catalog versions; an entry carrying neither SHALL be rejected. The bridge SHALL compute a SHA-256 digest over the exact source bytes. Persistent metadata SHALL bind at least the canonical client version, canonical source URL, source ETag when present, content digest, fetch time, and validation time to those bytes. Disk publication SHALL use a same-directory temporary file and atomic replacement so interruption cannot expose a partial entry. Cache contents SHALL be treated as untrusted on every process start and MUST NOT use reflection-based JSON serialization.

#### Scenario: Valid source is durably promoted

- **WHEN** canonical source bytes pass size, envelope, entry, and uniqueness validation
- **THEN** the bridge records their digest and metadata, atomically publishes them to disk, and only then exposes the entry through memory

#### Scenario: Either instruction carrier satisfies validation

- **WHEN** a catalog entry carries its prompt as top-level `base_instructions`, or instead as `model_messages.instructions_template`
- **THEN** the bridge accepts the entry in both cases without requiring the other field

#### Scenario: Entry with no instruction source is rejected

- **WHEN** a catalog entry carries neither a non-empty `base_instructions` nor a non-empty `model_messages.instructions_template`
- **THEN** the bridge rejects that catalog rather than serving a model with no prompt

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

A transient GitHub timeout, DNS/TLS failure, throttling response, server error, missing conditional-response validator, or invalid replacement SHALL NOT discard an already validated exact-version cache entry. The bridge SHALL serve that stale last-known-good catalog and warn that source freshness could not be established. If no validated entry exists for the exact requested version, a transient source failure SHALL produce a non-2xx metadata response so Codex retains its own bundled catalog. If no validated entry exists and the canonical source definitively reports `404`, the bridge SHALL serve the compile-time bundled snapshot when that fallback is enabled, and otherwise SHALL produce a non-2xx metadata response. In every case `POST /codex/responses` inference SHALL remain available. A `404` for one exact tag MUST NOT poison other versions.

#### Scenario: Offline restart uses validated disk entry

- **WHEN** the bridge restarts offline with a stale validated disk entry for the requested exact version
- **THEN** it serves that entry, repopulates memory, and logs stale-source use

#### Scenario: Cold offline request falls back at the client

- **WHEN** the bridge is offline and has no validated exact-version entry
- **THEN** `/codex/models` returns a non-2xx metadata error while `/codex/responses` remains mapped, allowing Codex to retain its bundled catalog

#### Scenario: Confirmed-absent tag serves the bridge snapshot

- **WHEN** the canonical source returns `404` for the requested exact version, no validated entry exists, and the bundled fallback is enabled
- **THEN** `/codex/models` returns a projected catalog from the bundled snapshot rather than a metadata error

#### Scenario: Exact tag absence is version-local

- **WHEN** GitHub returns `404` for one requested version
- **THEN** the bridge does not delete or suppress valid cache entries for other versions

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

### Requirement: Codex-native model discovery endpoint

The bridge SHALL serve `GET /codex/models?client_version=<version>` at the path formed by Codex from the managed provider base URL when `Codex.ModelCatalog.Enabled` is true. That option SHALL default to true both in code and in the stock `appsettings.json`, so upgraded installations that do not yet carry the key retain discovery. The separate `Codex.ModelCatalog.BuiltinFallbackEnabled` option SHALL likewise default to true in both places and SHALL control only whether a confirmed-absent exact version is answered from the compile-time bundled snapshot. A successful response SHALL use Codex's `ModelsResponse` envelope with a `models` array of complete Codex `ModelInfo` entries obtained from a validated official-source catalog — the requested version's catalog, or the bundled snapshot under its gating requirements — SHALL be valid under Native AOT, and SHALL NOT expose GitHub Copilot's incompatible raw model envelope.

#### Scenario: Exact Codex client requests its catalog

- **WHEN** a Codex client requests `/codex/models` with one valid canonical `client_version` and the bridge resolves a validated exact-version source catalog
- **THEN** the bridge returns HTTP 200 with a Codex-compatible `{ "models": [...] }` body and a projected catalog ETag

#### Scenario: Raw Copilot schema is not leaked

- **WHEN** the bridge builds a Codex catalog from an official Codex source catalog and a Copilot `/models` response
- **THEN** the client response contains Codex fields such as `slug`, instruction metadata, and `context_window`, and does not substitute Copilot's `data`, `capabilities`, or `supported_endpoints` entry shape

#### Scenario: Invalid client version fails safely

- **WHEN** `client_version` is absent, repeated, or malformed
- **THEN** the bridge returns a non-2xx metadata error without changing inference state, allowing Codex to retain its bundled catalog

#### Scenario: Operator disables model catalog discovery

- **WHEN** `Codex.ModelCatalog.Enabled` is false at bridge startup
- **THEN** `/codex/models` is not mapped while `POST /codex/responses` remains available, so Codex safely retains its bundled catalog

### Requirement: Catalog baseline is client-version compatible

The bridge SHALL construct every successful response from a validated complete official Codex catalog. That baseline SHALL be the catalog whose exact source tag corresponds to the complete requested client version whenever such a catalog is reachable or validly cached. The source identity and digest SHALL be retained with the cache entry. The bridge MUST NOT serve the newest cached catalog, an arbitrary other version, or an unvalidated payload when the exact requested version is unavailable, and MUST NOT serve the compile-time bundled snapshot silently or unconditionally: doing so SHALL require confirmed upstream absence of the exact tag, the enabled bundled-fallback option, no validated exact-version entry, and a distinct logged outcome. Source entries SHALL remain opaque Codex-owned JSON except for the explicit backend-owned projection allow-list.

#### Scenario: New exact version needs no bridge release

- **WHEN** a valid Codex version not previously seen by this bridge has a valid catalog at its exact official source tag
- **THEN** the bridge resolves, validates, caches, projects, and serves that catalog without requiring an embedded baseline or bridge update

#### Scenario: Matching cache preserves exact official baseline

- **WHEN** a validated cache entry exists for the exact complete `client_version`
- **THEN** every returned model starts from that entry's complete official Codex model object for the same slug

#### Scenario: Unknown version is not guessed

- **WHEN** the exact requested version has neither a validated cache entry nor a valid canonical source catalog
- **THEN** the endpoint serves either the gated bundled snapshot or a metadata error, and never projects another cached version's instructions or model fields into the client

#### Scenario: Bundled snapshot requires confirmed absence

- **WHEN** the exact requested version's canonical source has not returned a definitive `404`
- **THEN** the bridge does not use the bundled snapshot as the baseline

### Requirement: Copilot facts overlay without changing Codex behavior

For an exact model-slug match, the bridge SHALL preserve all Codex-owned fields from the resolved official baseline and SHALL modify only the explicit backend-owned allow-list: provider availability, `context_window`, `max_context_window`, `auto_compact_token_limit`, and any safety adjustment required for those limits. A baseline `auto_review_model_override` SHALL be retained only when its target is also bridge-routable; otherwise it SHALL be cleared. The bridge MUST NOT synthesize a Codex entry for an unknown Copilot slug.

#### Scenario: Non-limit fields survive the overlay

- **WHEN** a live Copilot model exactly matches a baseline model and receives a context uplift
- **THEN** its instructions, supported reasoning descriptions, shell/tool mode, collaboration mode, picker metadata, priority, and compatibility fields remain semantically identical to the exact official baseline

#### Scenario: Live-only model is not guessed

- **WHEN** Copilot advertises a Responses model that has no entry in the exact official Codex baseline
- **THEN** the model is omitted from the effective catalog rather than receiving synthesized instructions or tool metadata

#### Scenario: Invalid auto-review target is cleared

- **WHEN** a baseline entry names an `auto_review_model_override` that the bridge cannot route for this catalog
- **THEN** the returned entry contains no override to that unsupported target

### Requirement: Catalog availability matches the bridge's executable routes

A returned model SHALL be API-supported and picker-visible only when the resolved official baseline contains it, the bridge has an exact Responses route/profile for it, and the effective Copilot model facts advertise `/responses` support. Baseline models that fail any condition SHALL be made unavailable to command-auth Codex even though their complete entry may remain in the merge response.

#### Scenario: Three-way supported model is selectable

- **WHEN** a slug exists in the baseline, has an exact bridge Responses profile, and is live in Copilot with `/responses`
- **THEN** the returned entry remains API-supported and may retain its baseline picker visibility

#### Scenario: Official but unsupported model is filtered

- **WHEN** Codex's baseline contains a model the bridge cannot route or Copilot no longer serves
- **THEN** the returned override marks it unavailable so the custom-provider picker does not offer a path that will fail at inference time

### Requirement: Context uplift respects total and prompt limits

The bridge SHALL treat Copilot `max_context_window_tokens` as the total context ceiling and `max_prompt_tokens` as the distinct input ceiling. A valid uplift SHALL set `context_window` and `max_context_window` no higher than the total ceiling and SHALL set an explicit `auto_compact_token_limit` no higher than 85 percent of total context, rounded down to a whole thousand tokens, and strictly below the maximum prompt with a non-zero safety reserve. Missing, non-positive, or internally inconsistent live limits MUST NOT raise the exact official Codex baseline.

#### Scenario: Current 1M-class model receives the 85-percent uplift

- **WHEN** Copilot reports a bridge-routable model with total context 1,050,000 and maximum prompt 922,000
- **THEN** Codex receives a total/max context no greater than 1,050,000
- **AND** its explicit auto-compaction threshold is 892,000 under the documented rounding policy.

#### Scenario: Prompt ceiling remains an independent guard

- **WHEN** 85 percent of total context is not strictly below the validated maximum prompt with a non-zero reserve
- **THEN** the bridge uses the lower prompt-derived safety limit rather than the total-context percentage.

#### Scenario: Total context is not mistaken for prompt capacity

- **WHEN** Copilot's maximum prompt is smaller than its maximum context
- **THEN** the bridge does not configure Codex to postpone compaction until the total-context ceiling.

#### Scenario: Inconsistent capability fails closed

- **WHEN** the live capability omits a required limit, contains a non-positive value, or reports maximum prompt greater than total context
- **THEN** the returned entry retains the exact official baseline limits and the bridge logs why no uplift was applied.

### Requirement: Metadata refresh is bounded and fail-open

The bridge SHALL coalesce concurrent Copilot model refreshes, cache only a
validated last-known-good overlay for a bounded process-local TTL, and atomically
replace it after successful validation. A Copilot metadata timeout or error SHALL
reduce catalog freshness or capacity but SHALL NOT prevent the bridge from
returning a version-compatible safe catalog.

Every failed shared refresh SHALL establish one process-local retry deadline from
`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds`. Before that deadline,
catalog calls SHALL return the stale last-known-good overlay, or the reviewed
official baseline on a cold failure, without another upstream `/models` request or
duplicate warning. After the deadline, concurrent callers SHALL again share at
most one refresh. A successful refresh SHALL clear prior failure state. Failure
state SHALL NOT be persisted and a process restart SHALL permit an immediate
attempt.

#### Scenario: Concurrent Codex starts share one refresh

- **WHEN** multiple supported clients request the catalog while no fresh overlay exists and no failure cooldown is active
- **THEN** the bridge performs at most one concurrent Copilot `/models` refresh and all callers receive a valid catalog result.

#### Scenario: Refresh failure uses last known good facts

- **WHEN** Copilot refresh fails after the process has cached a validated overlay
- **THEN** the endpoint serves that last-known-good overlay with one warning rather than returning malformed or partially updated limits.

#### Scenario: Cold overlay failure uses official baseline

- **WHEN** the first Copilot refresh fails and no validated overlay exists
- **THEN** the endpoint returns the resolved exact official Codex baseline with no live context uplift and only statically known bridge-routable models enabled.

#### Scenario: Periodic polls are suppressed during cooldown

- **WHEN** Codex requests `/codex/models` repeatedly before the failure retry deadline
- **THEN** every request receives the same safe stale/baseline catalog result
- **AND** no additional Copilot `/models` call or refresh-failure warning occurs.

#### Scenario: Cooldown expiry permits one shared retry

- **WHEN** the configured cooldown expires and multiple catalog callers arrive
- **THEN** exactly one new live-overlay refresh is attempted and the callers share its result.

#### Scenario: Successful retry restores normal freshness

- **WHEN** a post-cooldown refresh succeeds
- **THEN** its validated models atomically replace the prior overlay and clear the failure deadline
- **AND** normal successful-overlay TTL behavior resumes.

#### Scenario: Restart does not preserve a terminal failure

- **WHEN** a bridge process restarts during a live-overlay failure cooldown
- **THEN** no persisted failure entry prevents the new process from attempting `/models` immediately.

### Requirement: Catalog discovery never carries the real Copilot credential downstream

The bearer value used by Codex command auth SHALL be a non-secret sentinel. The `/codex/models` and `/codex/responses` paths MUST NOT use it as the upstream Copilot credential; upstream authentication SHALL continue exclusively through `AuthService`.

#### Scenario: Codex attaches provider sentinel

- **WHEN** Codex calls the bridge after obtaining the managed command-auth value
- **THEN** the bridge may receive the sentinel in `Authorization` but never forwards it as the Copilot credential

#### Scenario: Catalog and config contain no GitHub token

- **WHEN** the bridge configures Codex or returns a catalog
- **THEN** neither output contains the stored GitHub access token or refreshed Copilot token

### Requirement: Real Codex proves the larger window at the client boundary

Acceptance SHALL include real headless Codex behavior runs through bridge subprocesses on non-default ports. The primary task SHALL cause a Codex version not embedded in the bridge to fetch its exact official provider catalog, carry active context beyond the former 272,000-token catalog ceiling, and complete a multi-step tool workflow. A second run SHALL prove an offline bridge restart can serve the same version from validated disk cache. A third run SHALL prove a client whose exact tag is confirmed absent upstream receives the projected bundled snapshot with the same uplifted limits and completes the same workflow. The verdict MUST use Codex's own structured dispatch log, not only bridge status codes or traces.

#### Scenario: Unseen-version long-context tool task succeeds

- **WHEN** a real Codex client whose exact catalog was not embedded in the bridge runs the path-exercising long-context scenario through the configured bridge
- **THEN** its own log records the uplifted model context, successful tool execution output, and no router, incompatible-payload, aborted-dispatch, or equivalent fatal

#### Scenario: Offline restart retains client behavior

- **WHEN** the same real Codex scenario is repeated after restarting the bridge with GitHub source access unavailable and a validated disk entry present
- **THEN** Codex again observes the uplifted exact-version catalog and completes the tool workflow without a dispatch fatal

#### Scenario: Confirmed-absent version completes the workflow on the bundled snapshot

- **WHEN** a real Codex client requests a version whose canonical tag returns `404` and the bundled fallback is enabled
- **THEN** the client receives the uplifted catalog, completes the multi-step tool workflow, and its own log records no dispatch fatal

### Requirement: Bridge-bundled catalog fallback on confirmed source absence

The bridge SHALL embed exactly one reviewed official Codex catalog snapshot at compile time and SHALL serve it as the projection baseline only when all of the following hold: `Codex.ModelCatalog.BuiltinFallbackEnabled` is true, the canonical exact-version source returned a definitive `404 Not Found`, and no validated exact-version cache entry exists at any level. Any other source outcome — timeout, DNS/TLS failure, throttling, server error, oversized or invalid payload — MUST NOT activate the fallback and SHALL retain the existing degradation behaviour. The bundled snapshot SHALL ship with its captured provenance metadata (the version it was captured from, its canonical source URL, its upstream validator, and its content digest) and SHALL be subject to the same envelope, entry, and uniqueness validation as a downloaded catalog. A bundled snapshot that fails validation SHALL fail loudly at process start rather than at request time. The bridge MUST NOT select the snapshot by searching, comparing, or approximating tags.

#### Scenario: Unpublished client version receives the bundled baseline

- **WHEN** a Codex client requests a valid exact version whose canonical tag returns `404`, no validated cache entry exists, and the fallback option is enabled
- **THEN** the bridge returns HTTP 200 with a Codex-compatible `{ "models": [...] }` body projected from the bundled snapshot

#### Scenario: Transient source failure does not activate the fallback

- **WHEN** the canonical source request times out, fails at the transport layer, is throttled, or returns a server error, and no validated cache entry exists
- **THEN** the bridge returns a non-2xx metadata error and does not serve the bundled snapshot

#### Scenario: Validated cache outranks the bundled snapshot

- **WHEN** a validated exact-version entry exists in memory or on disk for the requested version
- **THEN** the bridge serves that entry and does not consult the bundled snapshot, whether or not the entry is stale

#### Scenario: Invalid bundled snapshot fails at startup

- **WHEN** the embedded snapshot does not parse as a complete valid Codex catalog
- **THEN** the bridge fails at process start rather than serving or silently skipping the snapshot

### Requirement: Bundled baseline is never cached and confirmed absence is

The bridge MUST NOT write the bundled snapshot, or any resolution derived from it, into the process-memory cache or the persistent disk cache under the requested client version. The bridge SHALL instead cache the definitive `404` observation for that exact version in process memory only, keyed by the complete canonical version, so that repeated requests short-circuit to the bundled baseline without further source I/O. That negative entry SHALL expire after its own bounded configurable TTL, defaulting to six hours and never applied for longer than the source freshness TTL, so a tag published upstream after the observation is discovered without operator action and a pre-existing shorter source TTL is not rejected at startup. Retained absence state SHALL be bounded, because the client version is request-controlled, and SHALL NOT be retained at all when the fallback is disabled. A validated exact-version entry that becomes available during the absence window — including one published by another bridge process into the shared persistent cache — SHALL still outrank the bundled snapshot. The negative entry MUST NOT be persisted to disk and MUST NOT survive a process restart. Caching a confirmed absence MUST NOT publish any fallback bytes as a validated cache entry.

#### Scenario: Repeated unpublished-version requests stop re-fetching

- **WHEN** a second request for the same confirmed-absent version arrives while its negative entry is live
- **THEN** the bridge serves the projected bundled baseline without issuing another canonical source request

#### Scenario: Later-published tag supersedes the bundled baseline

- **WHEN** the canonical tag for a previously absent version is published upstream and that version's negative entry has expired
- **THEN** the next request fetches, validates, caches, and serves the real official catalog instead of the bundled snapshot

#### Scenario: Bundled baseline never becomes a last known good

- **WHEN** the bridge has served the bundled snapshot for a version and is then restarted
- **THEN** no disk cache entry exists for that version and the bridge re-resolves from the canonical source

#### Scenario: Absence of one tag does not poison other versions

- **WHEN** one version's negative entry is live
- **THEN** other versions resolve, cache, and revalidate through the canonical source unaffected

### Requirement: Bundled fallback is observable without changing the client envelope

A response projected from the bundled snapshot SHALL use the same Codex `ModelsResponse` envelope as an official-source response and MUST NOT add, remove, or rename response fields to signal its provenance. The bridge SHALL instead record the bundled outcome as a distinct value in its structured resolution log, identifying the requested client version and the snapshot's captured version, without logging catalog contents or credentials. The projected `ETag` SHALL incorporate the snapshot's own captured identity, so a bundled response and an official response for the same requested client version cannot produce the same validator.

#### Scenario: Client parses a bundled response identically

- **WHEN** Codex receives a catalog projected from the bundled snapshot
- **THEN** the body is a valid Codex `ModelsResponse` whose entry shape is indistinguishable in structure from an official-source response

#### Scenario: Operator can identify a bundled response

- **WHEN** the bridge serves the bundled snapshot for a requested version
- **THEN** the structured log records a distinct bundled-fallback outcome naming both the requested version and the snapshot's captured version

#### Scenario: Bundled and official responses never share a validator

- **WHEN** the same client version is served first from the bundled snapshot and later from its published official catalog
- **THEN** the two responses carry different `ETag` values

### Requirement: Operator controls the bundled fallback independently
`Codex.ModelCatalog.BuiltinFallbackEnabled` SHALL default to true both in code and in the stock `appsettings.json`, so an upgraded installation whose configuration predates the key gains the fallback. When it is false, a confirmed-absent exact version SHALL return a non-2xx metadata error exactly as before this change. The flag SHALL be independent of `Codex.ModelCatalog.Enabled`, which continues to control whether the route is mapped at all. Neither flag SHALL affect `POST /codex/responses`.

#### Scenario: Upgraded installation gains the fallback by default

- **WHEN** the bridge starts with an `appsettings.json` that does not contain the new key
- **THEN** the bundled fallback is active

#### Scenario: Operator restores strict fail-closed behaviour

- **WHEN** `Codex.ModelCatalog.BuiltinFallbackEnabled` is false and a requested exact tag returns `404` with no validated cache entry
- **THEN** the bridge returns a non-2xx metadata error and does not serve the bundled snapshot

#### Scenario: Disabling discovery still unmaps the route

- **WHEN** `Codex.ModelCatalog.Enabled` is false
- **THEN** `/codex/models` is not mapped regardless of the fallback flag, and `POST /codex/responses` remains available

### Requirement: Operator controls live-overlay failure cooldown

`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds` SHALL control the
process-local cooldown after a failed live Copilot `/models` overlay refresh. It
SHALL default to 300 seconds in both code and stock `appsettings.json`, so upgraded
installations whose existing file lacks the key receive the protection. The exact
positive configured value SHALL be used and startup SHALL reject values outside
1..3600 seconds with the full key and supported range.

This cooldown SHALL be independent of official Codex source freshness,
confirmed-absence caching, client request retries, and inference behavior.

#### Scenario: Upgraded configuration uses documented default

- **WHEN** an existing installation has no `LiveOverlayFailureCooldownSeconds` key
- **THEN** a failed live overlay refresh suppresses new attempts for 300 seconds.

#### Scenario: Explicit cooldown is exact

- **WHEN** the operator configures a valid positive cooldown
- **THEN** the next live overlay attempt is eligible after exactly that duration without a hidden margin, clamp, or substitute.

#### Scenario: Invalid cooldown fails before serving

- **WHEN** the configured value is below 1 or above 3600 seconds
- **THEN** startup fails with an actionable validation error naming the key, value, and accepted range.

