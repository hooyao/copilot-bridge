## Context

Codex ships a complete model catalog in `codex-rs/models-manager/models.json`. For a normal custom provider it loads that bundled catalog and does not call the provider's `/models` route. A user-level `model_context_window` override is then clamped by the selected entry's `max_context_window`; Codex 0.144.1 therefore reduces an attempted 1,000,000-token override to its bundled 272,000-token maximum.

Codex 0.144.1 and current main refresh a provider-owned catalog only when the active authentication uses the first-party Codex backend or the custom provider declares command-backed auth. That refresh is `GET {base_url}/models?client_version={CodexVersion}` and expects `{ "models": [ModelInfo...] }`, not OpenAI's conventional `{ "data": [...] }` and not GitHub Copilot's model schema. Under command auth, returned entries replace bundled entries with the same slug and new entries are appended; omitted bundled entries remain.

GitHub Copilot's live `/models` response currently reports, for `gpt-5.4`, `gpt-5.5`, and the `gpt-5.6` codename models, a 1,050,000-token total context window, a 922,000-token maximum prompt, and a 128,000-token maximum output. Those are backend facts. Codex-specific fields such as instruction templates, tool mode, collaboration mode, picker visibility, compaction compatibility, and `auto_review_model_override` remain client facts and must come from the matching Codex release.

The bridge is a Native AOT binary. The implementation must remain source-generation/AOT safe, must not reflect over arbitrary DTOs, must not reveal the stored GitHub credential, and must not make a metadata outage prevent inference through an otherwise healthy bridge. Published binary size is observational for this change: it SHALL be measured and recorded for history, but no size threshold constrains the design or acceptance.

## Goals / Non-Goals

**Goals:**

- Make supported Codex versions discover bridge-served Copilot models with truthful, safe context limits beyond Codex's bundled 272k ceiling.
- Preserve the full Codex-owned behavior of each model entry while overlaying only backend-owned Copilot facts.
- Keep unsupported, retired, and not-yet-routable models out of the effective custom-provider picker.
- Activate remote discovery through `config codex` without exposing a real GitHub/Copilot token or weakening the config writer's preservation guarantees.
- Degrade to the Codex baseline limits when live Copilot discovery or version compatibility is unavailable.
- Verify the path with the real Codex client, including a request that crosses the former 272k boundary and executes tools successfully.

**Non-Goals:**

- Emulating OpenAI's generic `/v1/models` schema.
- Claiming that a 1,050,000-token total window permits a 1,050,000-token input; Copilot's separate maximum-prompt limit remains authoritative.
- Synthesizing Codex behavior for a Copilot model that has no compatible entry in a bundled Codex catalog.
- Downloading catalog source from GitHub at runtime or making bridge startup depend on GitHub availability.
- Enabling WebSocket Responses transport; the existing HTTP/SSE Codex path remains unchanged.
- Authenticating local bridge callers. The command token is a discovery trigger required by Codex, not an access-control boundary.

## Decisions

### 1. Add a Codex-native metadata endpoint at the existing provider base URL

The server will map `GET /codex/models`; Codex forms that path from the configured `base_url = http://localhost:{port}/codex`. The endpoint requires exactly one parseable `client_version` query value and returns the Codex `ModelsResponse` envelope. Missing, malformed, or unsupported versions return a non-2xx response so Codex retains its own bundled catalog.

The endpoint will emit an `ETag` derived from the selected baseline version plus the effective Copilot overlay. This supports Codex's model cache without coupling catalog refresh to inference responses. The bridge will not add `X-Models-Etag` to `/responses` in this change; Codex's normal startup/cache refresh remains sufficient.

Alternative considered: expose the existing Copilot `/models` response directly. Rejected because the envelopes and model entries are structurally incompatible and Codex requires instruction-bearing `ModelInfo` entries.

### 2. Vendor versioned, complete Codex catalog baselines

The bridge will carry reviewed snapshots of upstream `codex-rs/models-manager/models.json`, each annotated with its source commit/tag and a supported client-version interval. Initial support targets the installed/verified 0.144.x family. Selection uses the newest baseline whose declared interval contains `client_version`; it never silently selects a baseline for a newer unknown interval.

Catalog entries are complete because a remote entry replaces the entire same-slug bundled entry. Returning only context fields would discard instructions and tool behavior. The current full catalog is about 193 KB before compression. Its effect on the published AOT artifact will be measured as a reference only; size does not justify dropping required catalog fields or choosing a less faithful representation.

The baseline will be treated as opaque JSON entries plus a small validated overlay rather than re-modeling every fast-moving Codex field in C#. A source-generated response envelope containing cloned `JsonElement` entries, or an equivalent `Utf8JsonWriter` path approved by `Models/JsonContext`, preserves fields the bridge does not understand and stays AOT safe. Required fields and an instruction source are validated when a snapshot is added.

Alternative considered: write `model_catalog_json` into the user's Codex config. Rejected as the primary feature because it adds a managed sidecar file, bypasses live Copilot discovery, complicates single-binary lifecycle and rollback, and cannot adapt per request version. It remains a useful diagnostic fallback.

Alternative considered: construct small synthetic entries. Rejected because missing or stale instructions, tool mode, collaboration metadata, and future fields can silently change Codex behavior.

### 3. Join Codex client facts to Copilot backend facts by exact slug

For every baseline entry, the projection will determine three independent facts:

1. The bridge has an exact Responses route/profile for the slug.
2. Live Copilot `/models` advertises the exact slug with `/responses` support.
3. The live capability block contains internally consistent positive limits.

Only an exact three-way match receives live limit uplift and remains `supported_in_api=true`. Baseline entries that the bridge cannot serve are returned with `supported_in_api=false` and non-list visibility so command-auth Codex filters them out. A live Copilot model absent from the versioned Codex baseline is not synthesized; it becomes usable after a reviewed Codex baseline and bridge model profile are added.

All Codex-owned fields—including instructions, reasoning descriptions, shell/tool mode, collaboration mode, priority, compatibility hashes, and a valid `auto_review_model_override`—are preserved byte-semantically. An auto-review override is retained only when its target is also present and bridge-routable; otherwise it is cleared rather than pointing Codex at an unsupported model.

Alternative considered: derive model behavior from Copilot family names. Rejected by the repository's probe-grounded model-profile invariant and because Copilot capabilities do not encode Codex client semantics.

### 4. Publish total context truth while compacting below the prompt ceiling

For a valid uplift, the catalog projection sets:

- `context_window` and `max_context_window` to Copilot `max_context_window_tokens`;
- `effective_context_window_percent` to the Codex baseline value;
- `auto_compact_token_limit` to a conservative policy limit no greater than both 90% of total context and 97.5% of Copilot `max_prompt_tokens`, rounded down to a whole thousand tokens.

For the current 1,050,000 / 922,000 models, this yields an auto-compaction threshold of approximately 898,000 tokens while Codex knows the model's total window is 1,050,000. The prompt reserve protects the next turn and bridge/client-added material; Copilot's 128,000-token output allowance is already reflected by the difference between total context and maximum prompt.

If `max_prompt_tokens` is absent, non-positive, greater than total context, or otherwise inconsistent, the bridge does not uplift that entry. It keeps the reviewed baseline limits and logs the reason. The exact live boundary must be re-probed on real Codex request bytes before the projection is shipped, following the repository's model-profile evidence rule.

Alternative considered: set every relevant field to 1,050,000. Rejected because it falsely treats total context as maximum prompt and lets Codex delay compaction beyond the upstream input contract.

Alternative considered: advertise only 922,000 as `context_window`. Rejected because it erases the backend's distinct total-window fact; explicit auto-compaction is the correct place to enforce the lower safe working threshold.

### 5. Activate discovery with a non-secret command-auth helper

`config codex` will manage a nested `[model_providers.copilot-bridge.auth]` table whose command invokes the same bridge executable with a hidden, noninteractive token-printing subcommand. Native installs use the resolved bridge executable path; JIT development resolves `dotnet` plus the bridge DLL. The helper prints a stable, non-secret local sentinel and performs no network or token-store access.

Codex attaches that sentinel as `Authorization: Bearer ...` to both `/models` and `/responses`. Bridge endpoints continue to ignore inbound provider authorization and acquire the real Copilot token exclusively through `AuthService`. The GitHub token never enters Codex config, process arguments, stdout, or inbound request headers.

The command-auth declaration is necessary because Codex's remote-model refresh gate is based on the provider auth shape, not merely the existence of `/models`. `config status` will report drift when the command, arguments, or refresh policy no longer match what this bridge installation would write.

Alternative considered: `requires_openai_auth=true`. Rejected because it couples a Copilot custom provider to the user's OpenAI/ChatGPT login and still risks sending the wrong credential.

Alternative considered: an environment API key. Rejected because it does not satisfy the remote-refresh gate and creates unnecessary secret-management requirements.

### 6. Use a bounded, single-flight last-known-good overlay cache

A singleton catalog service will fetch Copilot models through the existing authenticated `ICopilotClient`, coalesce concurrent refreshes, and cache the last validated overlay for a short bounded TTL aligned with Codex's own metadata cache. A successful refresh atomically replaces the last-known-good value.

If refresh fails and a last-known-good overlay exists, the endpoint serves it with a warning and a stable ETag. If no validated overlay exists, it serves the version-compatible Codex baseline with only the bridge's statically known supported models enabled and with no context uplift. Metadata failure therefore reduces capacity but does not make normal inference unavailable.

The cache is process-local. Persisting Copilot model facts across bridge upgrades or account changes is intentionally avoided; a restart revalidates against the current account.

### 7. Verification is contract-first and client-observed

Unit tests will assert the protocol contract using upstream Codex fixture/catalog bytes and synthetic Copilot capability inputs, including mutation checks for each overlay guard. API-contract tests will run the endpoint and deserialize its response with a pinned Codex schema/real client. Native AOT publish must remain warning-free and the binary-size delta must be recorded for reference, but that delta is not a pass/fail gate.

Acceptance requires a `Kind=ClientBehavior` scenario that starts a bridge subprocess on a non-default port, drives the real Codex client with command auth, observes `GET /codex/models?client_version=...`, sends a prompt whose active context exceeds the former 272k catalog limit, and completes a multi-step tool task. The verdict must come from Codex's own structured log: the larger effective model context is recorded, tool execution output is present, and no router/dispatch/incompatible-payload fatal occurs. Bridge 200s and traces are supporting evidence only.

## Risks / Trade-offs

- **[Codex catalog schema changes]** → Version-gate every bundled snapshot; unknown client intervals receive non-2xx and fall back to the client's own catalog. Add a documented refresh procedure and pinned consumer test when updating Codex support.
- **[Copilot advertises a limit it does not enforce]** → Require live large-context probes on real Codex-shaped bytes before uplifting; fail closed to baseline limits when fields are missing or inconsistent.
- **[Remote entry replacement changes Codex behavior]** → Vendor complete upstream entries and mutate only an explicit allow-list of backend-owned fields; byte/semantic preservation tests cover every other property.
- **[Command auth adds a bearer header to inference]** → Use a public sentinel only, verify the bridge strips/ignores it, and contract-test that the real GitHub token never appears.
- **[Catalog increases AOT artifact size]** → Record the win-x64 published size and delta as informational history while retaining the single-file Native AOT executable. There is no size budget or acceptance threshold; protocol fidelity takes precedence over artifact size.
- **[Metadata fetch adds startup latency]** → Use the existing five-second-class upstream bounds, single-flight caching, and immediate safe fallback instead of blocking Codex indefinitely.
- **[A user retains manual `model_context_window` overrides]** → Preserve explicit user configuration; document that these overrides still take precedence within the raised `max_context_window` and can intentionally select a smaller working window.
- **[Older bridge after Codex upgrade loses uplift]** → Deliberate safe degradation: inference continues with Codex's bundled catalog until the bridge adds a reviewed baseline for the new client interval.

## Migration Plan

1. Add the endpoint, versioned baseline, projection, cache, DTO/context registrations, and offline tests without changing Codex configuration.
2. Extend `config codex` and `config status` to manage command auth; preserve backups and idempotence.
3. Run live Copilot contract probes and the real-client behavior scenario, then publish AOT and record size.
4. Update `docs/pipeline-design.md`, Codex setup/context-window documentation, README limitations, and the catalog-refresh procedure.
5. Existing users re-run `copilot-bridge config codex` and restart Codex. Until then, behavior remains the current 272k-capped custom provider.

Rollback is configuration-safe: an older bridge or `config codex` can replace the managed provider block from backup; manually removing the nested `auth` table stops remote discovery and returns Codex to its built-in catalog. No conversation data or token-store migration is involved.

## Open Questions

- Whether Codex's next schema interval remains wire-compatible with the 0.144.x snapshot must be answered by a pinned consumer test before widening the declared interval; it is intentionally not assumed here.
- Whether a later change should propagate the catalog ETag as `X-Models-Etag` on `/responses` for immediate mid-session refresh is left out of this initial feature.
