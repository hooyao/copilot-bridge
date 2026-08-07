## Why

Codex ships client builds before OpenAI tags the matching release on GitHub, so
the exact canonical catalog source can legitimately not exist yet. Codex desktop
currently reports `client_version=0.147.0` while tag `rust-v0.147.0` returns
`404` (its neighbours `rust-v0.146.0`, `rust-v0.147.0-alpha.1`, and
`rust-v0.147.0-alpha.1.2` all resolve `200`). With no disk last-known-good for a
version that never existed, `GET /codex/models` fails closed and returns `400
"Exact Codex catalog source is unavailable."` roughly every three minutes.

The result is that Codex silently keeps its own bundled catalog and therefore
never learns the bridge's Copilot-calibrated capacity — the 1M-class uplift to
1,050,000 context with an explicit 898,000 auto-compaction threshold. Inference
is unaffected, but the whole point of catalog discovery is lost for precisely
the newest client, and this skew is structural rather than a one-off.

## What Changes

- Add `Codex.ModelCatalog.BuiltinFallbackEnabled`, defaulting to **true** in code
  and in the stock `appsettings.json`, so upgraded installations gain the
  behaviour without editing configuration.
- Embed one Codex catalog snapshot in the bridge at compile time (the newest
  vendored official catalog) as an `EmbeddedResource`, and serve it as the
  projection baseline **only** when the exact canonical tag is confirmed absent
  upstream (`404`) and no validated exact-version cache entry exists.
- Run the bundled baseline through the existing `CodexCatalogProjector`, so it
  receives the same Copilot-facts overlay — availability filtering plus the
  `context_window` / `max_context_window` / `auto_compact_token_limit` uplift —
  as an official baseline. No second uplift mechanism is introduced.
- **Never cache the bundled baseline.** It is not a last-known-good and must not
  shadow a tag that appears upstream later. Instead cache the upstream *absence*
  (the `404`) for that exact version under a bounded, deliberately short TTL, so
  the bridge stops re-fetching a tag that does not exist while still discovering
  the real tag once it is published.
- Mark bundled-baseline responses as such in the projected envelope and logs, so
  a served fallback is never mistaken for the client's exact official catalog.
- **BREAKING (spec-level, not wire-level):** this reverses a deliberate existing
  invariant. Three requirements currently forbid serving an embedded snapshot
  when the exact version is unavailable, and one scenario names the embedded
  case explicitly. Those requirements are amended rather than contradicted: the
  prohibition on *guessing a neighbouring or newest tag* is retained in full,
  while a clearly-labelled, operator-disableable bundled fallback becomes
  permitted on confirmed upstream absence.

## Capabilities

### New Capabilities

<!-- None. This extends an existing capability rather than introducing one. -->

### Modified Capabilities

- `codex-model-catalog`: four requirements change.
  - *Exact official Codex source resolution* — its "missing exact tag is not
    guessed" scenario currently forbids serving an `embedded` version; it is
    narrowed to forbid neighbour/stable/latest/branch substitution while
    permitting the labelled bundled fallback.
  - *Exact-version last-known-good degradation* — a confirmed `404` with the
    fallback enabled now yields a projected bundled catalog instead of a
    non-2xx; transport failures, throttling, and server errors still fail
    closed, and `404` remains version-local.
  - *Catalog baseline is client-version compatible* — "MUST NOT silently serve
    an embedded snapshot" becomes MUST NOT serve one *silently or
    unconditionally*; serving it requires confirmed absence, the enabled option,
    and explicit labelling.
  - *Codex-native model discovery endpoint* — adds the new option's default-true
    semantics and the operator's ability to restore strict fail-closed
    behaviour.

## Impact

- **Code**: `src/CopilotBridge.Cli/Catalogs/Codex/` — `CodexCatalogSourceCache`
  (the `NotFound` branch and a negative-cache entry that must fit HybridCache's
  existing `RequireCacheable` fail-closed design), `CodexCatalogSource` /
  `CodexCatalogSourceClient` (surface confirmed absence distinctly),
  `CodexCatalogBaseline` (parse an embedded byte source), and a new bundled
  baseline provider. `CodexModelCatalogOptions` gains the flag and its
  validation.
- **Endpoint**: `CodexModelsEndpoint` keeps returning a Codex `ModelsResponse`;
  `ETag`, `source_version`, and `source_digest` semantics must stay coherent
  when the baseline is bundled rather than fetched.
- **Build**: `CopilotBridge.Cli.csproj` gains an `EmbeddedResource` for the
  snapshot (AOT requires embedding over `wwwroot/`); binary size grows by
  roughly the catalog's compressed size and should be checked against the
  documented publish-size expectation.
- **Docs**: `docs/codex-implementation-design.md` §7.1 states "It never tries a
  neighboring or latest tag" and must be amended to distinguish that retained
  prohibition from the new bundled fallback.
- **Tests**: unit coverage for the option default, the confirmed-absence path,
  the no-cache/negative-cache split, and projector parity between bundled and
  official baselines; plus a real headless Codex behaviour run, since a bridge
  200 does not prove the client accepted the catalog.
- **Not affected**: `POST /codex/responses` inference, Copilot credential
  handling, and the Claude Code routes.
