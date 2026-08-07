## ADDED Requirements

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

The bridge MUST NOT write the bundled snapshot, or any resolution derived from it, into the process-memory cache or the persistent disk cache under the requested client version. The bridge SHALL instead cache the definitive `404` observation for that exact version in process memory only, keyed by the complete canonical version, so that repeated requests short-circuit to the bundled baseline without further source I/O. That negative entry SHALL expire after its own bounded configurable TTL, independent of and shorter than the source freshness TTL, defaulting to six hours, so a tag published upstream after the observation is discovered without operator action. The negative entry MUST NOT be persisted to disk and MUST NOT survive a process restart. Caching a confirmed absence MUST NOT publish any fallback bytes as a validated cache entry.

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

## MODIFIED Requirements

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
