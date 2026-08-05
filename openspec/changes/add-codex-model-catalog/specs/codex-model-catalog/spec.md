## ADDED Requirements

### Requirement: Codex-native model discovery endpoint

The bridge SHALL serve `GET /codex/models?client_version=<version>` at the path formed by Codex from the managed provider base URL when `Codex.ModelCatalog.Enabled` is true. The option SHALL default to true both in code and in the stock `appsettings.json`, so upgraded installations that do not yet carry the key retain discovery. A successful response SHALL use Codex's `ModelsResponse` envelope with a `models` array of complete Codex `ModelInfo` entries, SHALL be valid under Native AOT, and SHALL NOT expose GitHub Copilot's incompatible raw model envelope.

#### Scenario: Supported Codex client requests its catalog

- **WHEN** a supported Codex client requests `/codex/models` with its parseable `client_version`
- **THEN** the bridge returns HTTP 200 with a Codex-compatible `{ "models": [...] }` body and a catalog ETag

#### Scenario: Raw Copilot schema is not leaked

- **WHEN** the bridge builds a Codex catalog from a Copilot `/models` response
- **THEN** the client response contains Codex fields such as `slug`, instruction metadata, and `context_window`, and does not substitute Copilot's `data`, `capabilities`, or `supported_endpoints` entry shape

#### Scenario: Invalid client version fails safely

- **WHEN** `client_version` is absent, repeated, malformed, or outside every reviewed catalog interval
- **THEN** the bridge returns a non-2xx metadata error without changing inference state, allowing Codex to retain its bundled catalog

#### Scenario: Operator disables model catalog discovery

- **WHEN** `Codex.ModelCatalog.Enabled` is false at bridge startup
- **THEN** `/codex/models` is not mapped while `POST /codex/responses` remains available, so Codex safely retains its bundled catalog

### Requirement: Catalog baseline is client-version compatible

The bridge SHALL construct every response from a complete, reviewed Codex catalog snapshot whose declared version interval contains the requested client version. Each snapshot SHALL record its upstream Codex source tag or commit. The bridge MUST NOT silently serve the newest known snapshot to a newer unknown client interval.

#### Scenario: Matching version selects reviewed baseline

- **WHEN** `client_version` falls inside a bundled snapshot's declared interval
- **THEN** every returned model starts from that snapshot's complete entry for the same slug

#### Scenario: New Codex schema is not guessed

- **WHEN** Codex requests a version newer than the newest reviewed interval
- **THEN** the endpoint fails safely rather than projecting old instructions or model fields into the unknown client

### Requirement: Copilot facts overlay without changing Codex behavior

For an exact model-slug match, the bridge SHALL preserve all Codex-owned fields from the selected baseline and SHALL modify only the explicit backend-owned allow-list: provider availability, `context_window`, `max_context_window`, `auto_compact_token_limit`, and any safety adjustment required for those limits. A baseline `auto_review_model_override` SHALL be retained only when its target is also bridge-routable; otherwise it SHALL be cleared. The bridge MUST NOT synthesize a Codex entry for an unknown Copilot slug.

#### Scenario: Non-limit fields survive the overlay

- **WHEN** a live Copilot model exactly matches a baseline model and receives a context uplift
- **THEN** its instructions, supported reasoning descriptions, shell/tool mode, collaboration mode, picker metadata, priority, and compatibility fields remain semantically identical to the reviewed baseline

#### Scenario: Live-only model is not guessed

- **WHEN** Copilot advertises a Responses model that has no entry in the selected Codex baseline
- **THEN** the model is omitted from the effective catalog rather than receiving synthesized instructions or tool metadata

#### Scenario: Invalid auto-review target is cleared

- **WHEN** a baseline entry names an `auto_review_model_override` that the bridge cannot route for this catalog
- **THEN** the returned entry contains no override to that unsupported target

### Requirement: Catalog availability matches the bridge's executable routes

A returned model SHALL be API-supported and picker-visible only when the selected baseline contains it, the bridge has an exact Responses route/profile for it, and the effective Copilot model facts advertise `/responses` support. Baseline models that fail any condition SHALL be made unavailable to command-auth Codex even though their complete entry may remain in the merge response.

#### Scenario: Three-way supported model is selectable

- **WHEN** a slug exists in the baseline, has an exact bridge Responses profile, and is live in Copilot with `/responses`
- **THEN** the returned entry remains API-supported and may retain its baseline picker visibility

#### Scenario: Bundled but unsupported model is filtered

- **WHEN** Codex's baseline contains a model the bridge cannot route or Copilot no longer serves
- **THEN** the returned override marks it unavailable so the custom-provider picker does not offer a path that will fail at inference time

### Requirement: Context uplift respects total and prompt limits

The bridge SHALL treat Copilot `max_context_window_tokens` as the total context ceiling and `max_prompt_tokens` as the distinct input ceiling. A valid uplift SHALL set `context_window` and `max_context_window` no higher than the total ceiling and SHALL set an explicit `auto_compact_token_limit` no higher than 90 percent of total context and strictly below the maximum prompt with a non-zero safety reserve. Missing, non-positive, or internally inconsistent live limits MUST NOT raise the reviewed Codex baseline.

#### Scenario: Current 1M-class model receives a safe uplift

- **WHEN** Copilot reports a bridge-routable model with total context 1,050,000 and maximum prompt 922,000
- **THEN** Codex receives a total/max context no greater than 1,050,000 and an explicit auto-compaction threshold below 922,000, approximately 900,000 under the documented policy

#### Scenario: Total context is not mistaken for prompt capacity

- **WHEN** Copilot's maximum prompt is smaller than its maximum context
- **THEN** the bridge does not configure Codex to postpone compaction until the total-context ceiling

#### Scenario: Inconsistent capability fails closed

- **WHEN** the live capability omits a required limit, contains a non-positive value, or reports maximum prompt greater than total context
- **THEN** the returned entry retains the reviewed baseline limits and the bridge logs why no uplift was applied

### Requirement: Metadata refresh is bounded and fail-open

The bridge SHALL coalesce concurrent Copilot model refreshes, cache only a validated last-known-good overlay for a bounded process-local TTL, and atomically replace it after successful validation. A Copilot metadata timeout or error SHALL reduce catalog freshness or capacity but SHALL NOT prevent the bridge from returning a version-compatible safe catalog.

#### Scenario: Concurrent Codex starts share one refresh

- **WHEN** multiple supported clients request the catalog while no fresh overlay exists
- **THEN** the bridge performs at most one concurrent Copilot `/models` refresh and all callers receive a valid catalog result

#### Scenario: Refresh failure uses last known good facts

- **WHEN** Copilot refresh fails after the process has cached a validated overlay
- **THEN** the endpoint serves that last-known-good overlay with a warning rather than returning malformed or partially updated limits

#### Scenario: Cold failure uses safe baseline

- **WHEN** the first Copilot refresh fails and no validated overlay exists
- **THEN** the endpoint returns the compatible Codex baseline with no live context uplift and only statically known bridge-routable models enabled

### Requirement: Catalog discovery never carries the real Copilot credential downstream

The bearer value used by Codex command auth SHALL be a non-secret sentinel. The `/codex/models` and `/codex/responses` paths MUST NOT use it as the upstream Copilot credential; upstream authentication SHALL continue exclusively through `AuthService`.

#### Scenario: Codex attaches provider sentinel

- **WHEN** Codex calls the bridge after obtaining the managed command-auth value
- **THEN** the bridge may receive the sentinel in `Authorization` but never forwards it as the Copilot credential

#### Scenario: Catalog and config contain no GitHub token

- **WHEN** the bridge configures Codex or returns a catalog
- **THEN** neither output contains the stored GitHub access token or refreshed Copilot token

### Requirement: Real Codex proves the larger window at the client boundary

Acceptance SHALL include a real headless Codex behavior run through a bridge subprocess on a non-default port. The task SHALL cause Codex to fetch the provider catalog, carry active context beyond the former 272,000-token catalog ceiling, and complete a multi-step tool workflow. The verdict MUST use Codex's own structured dispatch log, not only bridge status codes or traces.

#### Scenario: Long-context tool task succeeds

- **WHEN** the real Codex client runs the path-exercising long-context scenario through the configured bridge
- **THEN** its own log records the uplifted model context, successful tool execution output, and no router, incompatible-payload, aborted-dispatch, or equivalent fatal
