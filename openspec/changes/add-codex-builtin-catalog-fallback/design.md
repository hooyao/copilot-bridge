## Context

`GET /codex/models` resolves a catalog strictly by exact tag:
`https://raw.githubusercontent.com/openai/codex/rust-v{version}/codex-rs/models-manager/models.json`.
When that tag does not exist and no validated disk entry exists for the same
exact version, `CodexCatalogSourceCache.ResolveDiskThenSourceAsync` reaches its
final `Failure(...)` and the endpoint answers `400`.

That is currently *correct by design*: `openspec/specs/codex-model-catalog/spec.md`
forbids substituting another version, and one scenario explicitly forbids an
`embedded` one. The fail-closed choice assumed a missing tag means "this client
is unknown, let it keep its own catalog".

Live evidence shows the assumption does not hold for new clients. Codex desktop
reports `client_version=0.147.0`; `rust-v0.147.0` is `404` while
`rust-v0.146.0`, `rust-v0.147.0-alpha.1`, and `rust-v0.147.0-alpha.1.2` are all
`200`. OpenAI ships clients before tagging, so the newest client — the one that
most needs the bridge's calibrated limits — is the one guaranteed to fail. The
bridge log shows the same triple every ~3 minutes (two versions resolved,
`0.147.0` `source=NotFound`), and Codex records the matching error in its own
`logs_2.sqlite`.

Constraints that shape the solution:

- **Native AOT.** The snapshot must be an `EmbeddedResource` (no `wwwroot/`),
  and all serialization must go through `JsonContext`. The current catalog is
  318 KB against a 14 MB binary — acceptable, and largely `base_instructions`
  prose.
- **`CodexModelsResponse` carries only `models`.** The projector's
  `source_version` / `source_digest` exist solely inside the ETag hash buffer
  and never reach the client. Labelling a fallback therefore cannot live in the
  response body without changing the shape Codex parses.
- **`RequireCacheable` deliberately throws** so failures and stale results are
  never published to HybridCache's L1. Any negative cache must fit that design
  rather than weaken it.
- The projector already performs the entire Copilot uplift
  (272,000 baseline → 1,050,000 / 898,000, confirmed live). No second uplift
  mechanism is needed or wanted.

## Goals / Non-Goals

**Goals:**

- Serve a useful, Copilot-calibrated catalog to a client whose exact tag is not
  yet published, instead of failing closed.
- Keep the bundled baseline strictly out of both cache levels, so a tag
  published later is always preferred once it appears.
- Stop the every-3-minute re-fetch of a tag confirmed absent, without making
  that suppression outlive the tag's eventual publication.
- Keep the reversal of the existing invariant explicit and auditable in both the
  spec and the design doc.
- Let an operator restore the strict fail-closed behaviour with one setting.

**Non-Goals:**

- Neighbour-tag or newest-tag guessing. The prohibition stays fully intact; the
  bundled snapshot is a fixed, reviewed artifact, not a search over tags.
- Any change to `POST /codex/responses` inference, credential handling, or the
  Claude Code routes.
- A second context-uplift mechanism, or bundling more than one snapshot.
- Auto-refreshing the bundled snapshot at runtime — it is a compile-time
  artifact, refreshed by a bridge release.

## Decisions

### D1. Fall back only on *confirmed absence*, never on failure

Only `CodexCatalogSourceStatus.NotFound` — an HTTP `404` from the canonical
source — enables the bundled baseline. `Timeout`, `TransportFailure`,
`Throttled`, `ServerError`, and `Failed` keep today's behaviour exactly:
stale-if-error when a validated entry exists, otherwise a non-2xx.

*Rationale:* a `404` is positive information ("this tag does not exist"),
whereas a timeout is absence of information. Falling back on the latter would
mask an outage and could serve a stale-shaped catalog to a client whose real
catalog is fine. *Alternative rejected:* falling back on any failure — simpler,
but it converts every transient network problem into a silent downgrade.

### D2. The bundled baseline is never cached; the 404 is

The bundled catalog is not a last-known-good. Caching it under the requested
version would make the bridge prefer a compile-time snapshot over the real tag
once OpenAI publishes it — exactly the shadowing the current spec was written to
prevent.

Instead the bridge caches the *absence*: a negative entry keyed by the exact
version recording that the canonical source returned `404`. While that entry is
live, resolution short-circuits to the bundled baseline without network I/O.

This composes with `RequireCacheable` rather than fighting it: a confirmed `404`
is a **definitive, cacheable observation**, categorically unlike the transient
failures and stale fallbacks that method refuses to publish. Only the negative
observation enters the cache — never a resolution carrying bundled bytes — so
the invariant "no fallback result is ever published as a fresh L1 entry" is
preserved verbatim.

*Alternative rejected:* caching the projected bundled response. Faster, but it
puts fallback bytes in a cache whose whole contract is exact-version validated
bytes, and it would need invalidation the moment the tag appears.

### D3. Negative-cache TTL is short and independent of the 24-hour source TTL

The absence TTL is its own bounded, configurable value defaulting to **6 hours**,
not the 24-hour source freshness TTL.

The two encode different bets. Source TTL asks "did validated content change?",
where being a day stale is harmless. Absence TTL asks "does this tag exist yet?",
where being stale means continuing to serve a compile-time snapshot after the
real catalog went live. Six hours bounds re-fetches to ~4/day per unknown version
(versus ~480/day today) while keeping the window to adopt a freshly published tag
well under a day. Being wrong is cheap in both directions: too short costs one
conditional GET, too long costs at most one TTL window of a labelled fallback.

*Alternative rejected:* reusing the 24-hour TTL — one fewer knob, but a client
could sit on a bundled catalog for a day after its real one was published.

### D4. Label the fallback in logs and the ETag, not the response body

`CodexModelsResponse` exposes only `models`; Codex parses that envelope.
Injecting a provenance field risks tripping a client-side schema check for no
gain, since Codex has no use for it.

Provenance therefore goes where it is observable without changing the contract:

- The structured resolve log gains a distinct outcome (e.g. `builtin-fallback`)
  alongside the existing `memory` / `disk` / `source-200` / `stale` values, so
  the every-3-minute `WRN source=NotFound` becomes one clear INFO line.
- The projector already folds `source_version` into the ETag hash. The bundled
  baseline reports its own snapshot identity there, so a bundled response and an
  official one for the same client version can never collide on ETag.

*Alternative rejected:* an `x-bridge-catalog-source` response header. Harmless
but unused; logging plus ETag distinctness already covers operator diagnosis.

### D5. Bundled snapshot identity is the snapshot's own version

`source_version` for a bundled response is the version the snapshot was
*captured from* (currently `0.147.0-alpha.1.2`), not the version the client
asked for. Recording the requested version would forge provenance and make two
different payloads indistinguishable in the ETag.

The snapshot ships with its captured metadata — source URL, upstream ETag,
SHA-256, capture time — mirroring `tests/Fixtures/Codex/*/capture.json`, so the
same validation the network path performs (`CodexCatalogBaseline.Parse` plus
`CodexCatalogBaselineValidator.Validate`) runs on the embedded bytes. A snapshot
that fails validation is a build-time defect, so it fails loudly at startup
rather than at first request.

### D6. Projector parity is the mechanism for the 1M uplift

The bundled baseline enters `CodexCatalogProjector.Project` exactly as an
official baseline does. Availability filtering, the `auto_review_model_override`
check, and the `context_window` / `max_context_window` /
`auto_compact_token_limit` uplift all apply unchanged. Nothing about the uplift
is special-cased for bundled input — verified as already sufficient, since the
projector derives limits solely from the live Copilot overlay.

One consequence is worth stating: a bundled snapshot may lack a model the
requested client knows, or carry one it dropped. That is inherent to serving a
different version's baseline and is bounded by the availability filter, which
already hides anything the bridge cannot route.

## Risks / Trade-offs

- **Reverses a deliberate, documented invariant** → The three affected
  requirements are amended explicitly in the delta spec rather than left to
  contradict the implementation, and `docs/codex-implementation-design.md` §7.1
  is updated in the same change. The retained half (no neighbour/latest/branch
  guessing) is restated so the narrowing is unambiguous.
- **A client receives a catalog from a different version than it asked for** →
  Confined to confirmed-absent tags, gated by a default-on but disableable
  option, filtered by the existing availability rules, and distinctly logged.
  The alternative today is no catalog at all, which is strictly worse for the
  client.
- **Snapshot ages between bridge releases** → It only ever applies to versions
  with no upstream tag, and any published tag immediately wins once the absence
  TTL lapses. Refreshing the snapshot stays a normal release-time task.
- **Embedded resource grows the AOT binary** (~318 KB source into ~14 MB) →
  Measured against the documented publish-size expectation after the AOT build;
  the payload is mostly compressible prose.
- **Negative cache could suppress a real catalog** → Bounded to six hours by
  default, never persisted to disk, and cleared by a process restart.
- **A bridge 200 does not prove Codex accepted the catalog** → Acceptance
  requires a real headless Codex run whose verdict comes from the client's own
  `logs_2.sqlite`, per the repo's testing directive.

## Migration Plan

1. Ship the option default-on; no configuration change is required for existing
   installations, whose `appsettings.json` may predate the key.
2. Operators wanting the previous strict behaviour set
   `Codex.ModelCatalog.BuiltinFallbackEnabled=false`; `Enabled=false` still
   unmaps the route entirely.
3. Rollback is the flag — no persisted state is created by this change, since
   the bundled baseline is never written to disk and the negative cache is
   process-local.

## Open Questions

- Should the negative cache also suppress logging after the first observation
  per version, or is one INFO line per resolve acceptable? (Leaning: the
  short-circuit already collapses the every-3-minute WRN storm, so per-resolve
  INFO is fine.)
- Should the build fail when the vendored snapshot falls more than N releases
  behind the newest known tag? Useful staleness pressure, but it makes builds
  depend on network state, so it is likely better as a release-checklist item.
