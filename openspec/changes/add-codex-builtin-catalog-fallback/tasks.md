## 1. Bundled snapshot asset

- [ ] 1.1 Vendor the newest reviewed official catalog into `src/CopilotBridge.Cli/Catalogs/Codex/Bundled/` as `models.json` plus a `capture.json` carrying its captured version, canonical source URL, upstream ETag, SHA-256, and capture time (mirror `tests/Fixtures/Codex/rust-v0.147.0-alpha.1.2/capture.json`)
- [ ] 1.2 Verify the vendored bytes against the live canonical source (digest must equal the recorded SHA-256) so the snapshot is provably the official artifact, not a hand-edited copy
- [ ] 1.3 Declare both files as `<EmbeddedResource>` in `CopilotBridge.Cli.csproj` with a stable logical name, and confirm no `wwwroot/`-style loose file is introduced

## 2. Options and configuration

- [ ] 2.1 Add `BuiltinFallbackEnabled` (default `true`) and `AbsenceTtlHours` (default `6`) to `CodexModelCatalogOptions`
- [ ] 2.2 Extend `CodexModelCatalogOptionsValidator` to bound `AbsenceTtlHours` to a positive range strictly below `SourceTtlHours`, with a clear failure message
- [ ] 2.3 Add both keys with explanatory `_comment` text to the stock `src/CopilotBridge.Cli/appsettings.json`, matching the existing `Codex:ModelCatalog` comment style

## 3. Bundled baseline provider

- [ ] 3.1 Add a bundled-baseline provider that reads the embedded resources once, parses via `CodexCatalogBaseline.Parse`, and runs `CodexCatalogBaselineValidator.Validate`
- [ ] 3.2 Fail at process start (not first request) when the embedded snapshot is missing or invalid
- [ ] 3.3 Register any new DTO in `Models/JsonContext.cs`; assert no reflection-based `JsonSerializer` call is introduced
- [ ] 3.4 Expose the snapshot's captured version and digest so the projector's `source_version` / `source_digest` reflect the snapshot's own identity, never the requested version

## 4. Confirmed-absence resolution path

- [ ] 4.1 In `CodexCatalogSourceCache`, branch on `CodexCatalogSourceStatus.NotFound` with no last-known-good: return a bundled-baseline resolution with a distinct `builtin-fallback` outcome when the option is enabled, otherwise keep today's `Failure`
- [ ] 4.2 Keep every non-`NotFound` failure status on the existing path — no fallback on `Timeout`, `TransportFailure`, `Throttled`, `ServerError`, or `Failed`
- [ ] 4.3 Ensure a validated memory or disk entry (fresh or stale) always outranks the bundled snapshot
- [ ] 4.4 Add a process-local negative cache keyed by the exact canonical version recording the confirmed `404`, expiring per `AbsenceTtlHours`, never persisted to disk
- [ ] 4.5 Guarantee no bundled-derived resolution is ever published as an L1 entry — extend `RequireCacheable` so only the negative observation is cacheable
- [ ] 4.6 Add the `builtin-fallback` outcome to `LogResult`/`LogFailure` reporting both the requested version and the snapshot's captured version, and downgrade the current repeating `NotFound` warning accordingly

## 5. Endpoint and projection

- [ ] 5.1 Confirm `CodexModelsEndpoint` needs no envelope change — the bundled path must return the same `CodexModelsResponse` shape with only `models`
- [ ] 5.2 Verify the projected `ETag` differs between a bundled and an official response for the same requested client version
- [ ] 5.3 Confirm the projector applies the Copilot uplift to the bundled baseline with no bundled-specific special-casing

## 6. Unit tests (contract-first, mutation-checked)

- [ ] 6.1 Contract: a confirmed `404` with fallback enabled and no cache entry yields 200 with a projected catalog — assert the observable response, not internal state
- [ ] 6.2 Contract: each transient failure status yields a non-2xx and never the bundled body
- [ ] 6.3 Contract: `BuiltinFallbackEnabled=false` restores the non-2xx on confirmed absence
- [ ] 6.4 Contract: the option defaults to true when absent from configuration
- [ ] 6.5 Contract: after serving the bundled snapshot, no disk cache entry exists for that version
- [ ] 6.6 Contract: a second request within the absence TTL issues no further source request; after expiry a newly published tag is fetched and preferred
- [ ] 6.7 Contract: a validated stale disk entry is preferred over the bundled snapshot
- [ ] 6.8 Contract: bundled and official responses for the same client version carry different ETags
- [ ] 6.9 Contract: the bundled baseline receives the same 1M uplift as an official baseline given identical live Copilot facts
- [ ] 6.10 Contract: absence of one version does not suppress resolution of another
- [ ] 6.11 Mutation-check every new test — break the product code and confirm each goes red

## 7. Real-client verification (not optional)

- [ ] 7.1 Add a `Kind=ClientBehavior` playground scenario driving real `codex.exe` against a bridge whose requested version is confirmed-absent upstream
- [ ] 7.2 Run the `real-client-verify` skill; verdict comes from codex's own `~/.codex/logs_2.sqlite` — tool actually executed, no `incompatible payload` or router fatal
- [ ] 7.3 Confirm from the client side that the uplifted context window is in effect on the bundled path
- [ ] 7.4 Re-verify the originally broken case end to end: a client reporting the unpublished version now receives a catalog instead of the repeating 400

## 8. Build and documentation

- [ ] 8.1 Run `dotnet test --filter "Category!=Integration"` and confirm green
- [ ] 8.2 AOT-publish per the CLAUDE.md PowerShell block; confirm success by the published exe's advanced mtime and record the size delta from embedding the snapshot
- [ ] 8.3 Amend `docs/codex-implementation-design.md` §7.1: keep the no-neighbour/latest/branch prohibition, document the bundled fallback, its gating, its no-cache rule, and the negative cache
- [ ] 8.4 Document both new options in the operator-facing catalog documentation alongside `Enabled`
- [ ] 8.5 Note the snapshot-refresh step in the release checklist so the bundled catalog does not silently rot
