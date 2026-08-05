## Why

The bridge currently embeds a reviewed Codex model catalog for each supported client-version interval, so every Codex upgrade can silently lose the 1M context uplift until a matching bridge release is published. Codex identifies the release family in the `GET /codex/models` query and carries its complete stable/prerelease build identity in the same request's Codex `User-Agent`; the bridge can corroborate those values to obtain, validate, and cache the matching official catalog instead of coupling two independent release trains.

## What Changes

- Replace embedded, manually refreshed Codex catalog baselines with exact-version discovery from the official `openai/codex` release tag and `codex-rs/models-manager/models.json`.
- Introduce a three-level catalog cache: process memory managed by the Native AOT-compatible Microsoft `HybridCache`, persistent local disk, and the canonical GitHub source.
- Resolve the complete version from a strict query/`User-Agent` identity pair, key cached catalogs by that complete stable/prerelease value, and never substitute an adjacent or newest-known version.
- Add a configurable freshness TTL and conditionally revalidate stale entries with the source ETag so unchanged source bytes are not downloaded again.
- Validate source identity, JSON shape, required Codex behavior fields, and content digest before publishing a catalog; atomically promote only validated bytes to memory and disk.
- Serve a validated stale disk or memory entry when GitHub is temporarily unavailable. If no exact-version last-known-good entry exists, fail only the metadata request so Codex retains its bundled catalog and inference remains available.
- Preserve the existing exact-slug join with live Copilot capabilities and continue changing only the allow-listed backend-owned availability and capacity fields.
- Add bounded cache retention, safe path derivation, diagnostics, and real Codex coverage for first fetch, cache hits, source revalidation, source change, offline restart, and an unseen prerelease client.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-model-catalog`: Replace version-bundled catalog selection with exact-version official-source resolution, three-level caching, conditional revalidation, validated last-known-good fallback, and version-independent client support.

## Impact

- Affects the `/codex/models` metadata path, Codex catalog baseline/projector services, options, startup composition, logging, and model-catalog tests.
- Adds outbound HTTPS access from the metadata path to the canonical `openai/codex` source when no fresh exact-version cache entry exists; normal inference does not depend on that access.
- Adds a small persistent cache and metadata files next to the bridge's existing writable application data, with atomic replacement and bounded retention.
- Removes the recurring requirement to embed a new catalog and publish a bridge release for every compatible Codex version, while retaining safe fallback to Codex's own bundled catalog.
