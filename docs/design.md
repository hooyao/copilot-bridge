# copilot-bridge — design notes

This doc captures **what isn't covered elsewhere**: the scope statement, the AOT discipline that drives every dependency choice, and the durable decision log. Implementation specifics live alongside the code they describe:

- Pipeline architecture, stages, request/response flow → [`pipeline-design.md`](pipeline-design.md)
- Copilot API behavior, model routing, protocol research, empirical limitations → [`copilot-api-research.md`](copilot-api-research.md)
- User-facing roadmap and limitations → [`../README.md`](../README.md)

---

## 1. Scope

### 1.1 What we're building

A .NET 10 Native AOT reverse proxy that exposes the GitHub Copilot LLM API in vendor-neutral compatibility shapes (Anthropic Messages today, OpenAI Chat Completions / Gemini later) so existing CLIs can use Copilot as their model backend.

Ships as a single small `.exe` with no .NET runtime dependency.

### 1.2 Target user

**Someone with a GitHub Copilot subscription, no Anthropic / OpenAI API key.** The whole point is to use Copilot as the only paid service. This rules out designs that would require the bridge to authenticate against another vendor at runtime:

- No fall-back to `api.anthropic.com` when Copilot lacks a feature.
- No dual-backend cost-splitting.
- Anthropic / OpenAI keys *can* appear in `tests/CopilotBridge.Playground/appsettings.local.json` (gitignored) for wire-format diffing tests, but never in the bridge's deployment.

For Copilot-unsupported features the options reduce to: document the gap + suggest a client-side workaround (current `web_search_*` policy — see research doc §16.8); simulate in bridge using Copilot itself; or skip the feature.

### 1.3 Non-goals (M1)

- Anthropic↔OpenAI translation (only needed in M3 as a fallback for models whose `supported_endpoints` lacks `/v1/messages`)
- Rate limiting / manual approval flags (the `--rate-limit`, `--manual` features in `copilot-api`)
- Embeddings, usage dashboard, multi-user / multi-account
- HTTPS / auth on the bridge itself (listens on localhost; OS-level isolation is enough)
- Cross-platform (win-x64 only; Linux/macOS later)

---

## 2. AOT discipline

The single hard non-functional goal is a **single-file, small-footprint `.exe`** with no .NET runtime dependency. AOT isn't picked for cold-start speed — it's picked for deploy simplicity and binary size. Every dependency choice (no reflection-based JSON, no `wwwroot/` static files, and a measured size entry for anything new) follows from that.

### 2.1 Required project settings

- `<PublishAot>true</PublishAot>` + `<OptimizationPreference>Size</OptimizationPreference>`
- `<InvariantGlobalization>true</InvariantGlobalization>`, `<UseSystemResourceKeys>true</UseSystemResourceKeys>`, `<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>`
- Release build: disable `DebuggerSupport`, `StackTraceSupport`, `MetricsSupport`, `EventSourceSupport`, `HttpActivityPropagationSupport`
- All JSON serialization through `JsonSerializerContext` — every DTO has a `[JsonSerializable(typeof(...))]` entry on `Models/JsonContext.cs`

### 2.2 Forbidden

- `JsonSerializer.Serialize(obj)` without a `JsonTypeInfo` overload — silently breaks at runtime (output becomes `{}`)
- `Activator.CreateInstance(Type)` / `Type.GetType(string)` / dynamic loading
- `[FromBody] dynamic` or `object` parameters on minimal API delegates
- `wwwroot/` static files (use `<EmbeddedResource>` for HTML/JS/CSS)

#### Previously forbidden

- `IHttpClientFactory` — **allowed since 2026-07-27** (was forbidden). The ban
  assumed one upstream surface, where a singleton `HttpClient` really is enough.
  The bridge now has four (`/v1/messages`, `/responses`, metadata, GitHub auth),
  and the first three share a host: one pooled handler puts them in a single
  connection budget while the bridge holds connections open for *minutes* during a
  long turn, so a Codex burst could stall Claude Code. Cost: the whole span since
  the last recorded build grew **+199 KB (+1.55%)**, an aggregate upper bound that
  also covers eleven unrelated PRs — the factory's own share is smaller and was
  not isolated. No new package (it ships in the ASP.NET shared framework) — see
  [`size-history.md`](size-history.md) 2026-07-27. Consumers must lease per
  call (`CreateClient` at the send site); caching one pins a pooled handler and
  defeats the rotation that justifies the factory.

### 2.3 Size monitoring

After every dependency change record the published `.exe` size in [`size-history.md`](size-history.md). Budget: under 25 MB. Each dependency PR notes its incremental cost.

### 2.4 Dependency allowlist

Currently in the bridge runtime (`src/CopilotBridge.Cli/`):

- `System.Net.Http`, `System.Text.Json`, `Microsoft.AspNetCore.App` (BCL / shared framework)
- `System.CommandLine` 3.0-preview.3 (AOT-optimized; auto `--help`/`--version`, typed handlers)
- `System.Security.Cryptography.ProtectedData` (DPAPI for GitHub-token-at-rest encryption)
- `Serilog` + `Sinks.Console` + `Sinks.File` (AOT-clean since Serilog PR #2175; +1.5 MB accepted)
- `Tomlyn` 2.10.1 (TOML read/write for `config codex`; **syntax-tree API only** — `Tomlyn.Parsing.SyntaxParser` / `DocumentSyntax`, never the model DOM. AOT-clean: pure parsing, no reflection, and reflection serialization is auto-disabled under `PublishAot`. Referenced-but-trimmed until the config command uses it)

Anything new requires evaluating the size impact and recording it.

---

## 3. Decision log

Durable record of choices and the reason — kept here rather than scattered through commit messages so a single read tells the story.

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-05-06 | .NET 10 Native AOT | Single-file + small binary is the hard goal |
| 2026-05-06 | Single csproj, folder-based modules | Multiple csprojs each pay an AOT cost; keep it simple |
| 2026-05-06 | AuthService is a self-contained facade | Callers don't need to know about the token lifecycle; protocol details stay inside `Auth/` |
| 2026-05-06 | OptimizationPreference=Size (RamDrive uses Speed) | User prioritizes binary size over latency |
| 2026-05-06 | M1 uses Kestrel | Correctness first; revisit if size pushes back |
| 2026-05-06 (v0.2) | M1 switches to Anthropic native passthrough (no translation) | Research confirmed Copilot has an official `/v1/messages` endpoint (`@vscode/copilot-api` package source); translation can be avoided entirely. See [`copilot-api-research.md`](copilot-api-research.md) §3 |
| 2026-05-06 (v0.2) | Base URL comes from the Copilot token's `endpoints.api`, not user-configured accountType | Aligns with the official `DomainService._getCAPIUrl`; more robust (business/enterprise auto-resolved) |
| 2026-05-06 (v0.2) | Header set: official 7 + Authorization + Content-Type + anthropic-beta only | The official `_mixinHeaders` emits only 7; start small, add only when needed |
| 2026-05-06 (v0.2) | `anthropic-beta` is generated by us based on model capabilities, not forwarded from Claude Code | Mirrors `chatEndpoint.ts:182-215` |
| 2026-05-08 (v0.3) | Routing split: wire facts in C# `CopilotModelRegistry`; user preferences in `appsettings.json` `Routing.Rules` | Avoids cartesian (model × effort) explosion in JSON; capability table grows linearly |
| 2026-05-08 (v0.3) | Serilog 4.3.1 replaces bespoke `[Conditional]` `DiagTracer` | Standard logger API, AOT-clean, dual sinks. Cost: +1.5 MB accepted |
| 2026-05-08 (v0.3) | `System.CommandLine` 3.0-preview.3 replaces hand-rolled `string[]` arg parsing | Auto help/version, typed handlers, AOT-optimized preview |
| 2026-05-21 | Effort routing derived from live `/models` catalog, not a hardcoded `EffortAware` table | New models / variants pick up automatically on next bridge restart; one less thing to forget when Copilot ships an update. See research doc §16 |
| 2026-05-21 | `count_tokens` is real passthrough, not a `{input_tokens:1}` stub | Empirically verified Copilot supports `POST /v1/messages/count_tokens`. Bridge swap is one method on `ICopilotClient`. See research doc §15.4 |
| 2026-05-21 | `web_search_*` server tools rejected at bridge with friendly 400 + MCP guidance — **not** simulated, **not** routed to native Anthropic | Target user has Copilot subscription only (§1.2). The friendly error directs users to configure an MCP search server, the supported workaround. See research doc §16.8 |
| 2026-05-31 | Routing redesigned around hand-curated `ModelProfileCatalog` (playground-derived); the live-`/models` `CopilotModelCatalog` is gone, `Routing.Rules` slimmed to model-redirect only | Copilot's `/models` capability metadata is incomplete and sometimes wrong (haiku-4.5 advertises adaptive thinking but rejects it; opus-4.8 declares mid-conv `role:"system"` support but the gateway rejects it on every model). Trusting it produces silent 400s. Profiles are sourced from a probe matrix (`ModelProfileProbe.cs`) — re-run after Copilot ships a change. Unknown models throw `UnknownModelException` → 400 + Anthropic error body instead of silent forwarding. See pipeline-design.md §7 |
| 2026-06-01 | Routing config redesigned nginx-style: flat `Routing.Rules` (`Match` → `Rewrite.Model`) became `Routing.Locations`, each a self-contained `When` (`MatchExpression`: `AllOf`/`AnyOf` + `Model`/`Effort`/`Header`) → `Use` (model swap, per-target `EffortMap`, whitelisted header `Set`/`Remove`). Plus: per-request IO tracing made opt-in (`Tracing.Enabled`, default off) writing to `request-traces/` (renamed from `logs/`); global `advisor-tool-*` beta strip | A `When` → `Use` closure reads cleaner than match+rewrite rules that have to compose across the list — one location holds everything for "this kind of request" (e.g. opus-4.8 + 1M → 1m-internal **and** max→xhigh in one block). Header whitelist lets operators chase Copilot feature rollouts / billing buckets without clobbering bridge-internal auth headers. Tracing is off by default because traces contain full prompts. See pipeline-design.md §7, §9 |
| 2026-07 | Profile miss is now **best-effort, not fail-closed**: an inbound model id with no exact `ModelProfile` falls back to the *nearest known profile* via fuzzy match (`ModelNameMatcher`, Jaccard over character bigrams, family-then-version tie-break) instead of throwing. Only a below-floor / foreign-vendor id still 400s (`UnknownModelException`, now naming the nearest rejected candidate + score) | Reverses the 2026-05-31 "unknown models throw" stance. A Copilot model newer than the bridge build (e.g. selecting `claude-sonnet-5` on an older build) was hard-refused, forcing a manual `appsettings.json` edit just to use a model Copilot serves. Now it's forwarded under the closest known model's wire contract with the **real id on the wire** (Copilot is the final authority; a wrong borrowed shape surfaces as Copilot's own visible error, not a silent 400), WARN-logged so the operator knows to upgrade or add an explicit remap. The similarity floor preserves the crisp actionable error for genuine typos. See pipeline-design.md §7, §11.3 |
| 2026-07 | `config` command (`claude-code` / `codex` / `status`) runs in a **dedicated web-host-free composition root** (`Hosting/ClientConfig/ClientConfigServices`), structurally disjoint from `serve`'s `AddBridgeServer` graph, behind an `IClientConfigurator` seam (Plan/Apply/Read) | Auto-configuration must not boot Kestrel, hosted services, or any request-pipeline/auth/Copilot-client dependency — the config graph physically cannot resolve them because they were never registered, so isolation is enforced by the DI graph, not by convention. The `IClientConfigurator` seam makes a new client a one-class + one-registration add with zero change to `serve` or the dispatcher. Surgical, format-preserving merges (JSON via `JsonNode`; TOML via Tomlyn `DocumentSyntax`) touch only the bridge's own keys; the port is derived from the same bound `Options` `serve` reads. The Claude configurator removes the legacy `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` override: real Claude Code 2.1.207 uses that fallback request to recover from a mid-stream error, and buffered Responses success bodies now enter Anthropic IR before response detectors on cross-model routes. See `openspec/changes/archive/2026-07-13-add-client-config-command` and `openspec/changes/archive/2026-07-13-fix-cc-responses-stream-fault`. |
| 2026-07 | Added three `gpt-5.6` Codex codename slots (`gpt-5.6-luna`/`-sol`/`-terra`) with a new **"xlarge" effort profile** (large + `max`) | Copilot's `/models` surfaced the three ids (`endpoints=[/responses,ws:/responses]`, `ctx=1050000`) in the 2026-07 reconciliation. All three live-probed identically (`ResponsesProbe.Gpt56_Effort_ReProbe`/`_Tool_ReProbe`): accept `none/low/medium/high/xhigh/max`, reject `minimal`, accept custom tools. They are the **first Codex models to accept `max`**, so `max` passes through verbatim instead of being clamped to `xhigh` (the large-profile behavior). TRAP honored: the `minimal`-rejection 400 body omits `max` from its "supported values", yet `max` probes 200 — probe is ground truth, not the advertised list. Added to `ResponsesModelIds` (else they'd fall through to the unimplemented `/chat/completions` branch). Verified end-to-end by `CcOnGpt56HeadlessTests` (complex CC→gpt-5.6-sol multi-tool task → all `/responses` 200, tools on wire, canary round-trip). See `codex-implementation-design.md` §5 |
| 2026-08-28 | Added Copilot's internal-only `gpt-5.6-sol-fast` as an explicit Responses model and introduced Responses catalog-vs-live B3 checks | The authenticated Enterprise account exposed the exact id with `/responses`, 1.05M context and the xlarge effort set. Direct probes plus a real captured Codex request proved `none/low/medium/high/xhigh/max` accepted, `minimal` rejected, function/custom/web-search accepted, structured image tool output understood, and >272k input accepted. A real Codex app-server completed namespaced and custom-tool round-trips with zero router fatals. The official Codex catalog does not publish this internal slug, so the bridge routes explicit selection without fabricating model instructions into `/codex/models`. The new B3 check also detected that `mai-code-1-flash-picker` now accepts custom tools; its obsolete silent drop was removed. A per-model semantic image-output sweep guards the exact positive capability for all nine OpenAI profiles and the required fallback for MAI Flash. A MAI-targeted real Codex run exposed `apply_patch` through an isolated client-catalog alias while keeping MAI on the wire, completed matching custom and shell call/output pairs, returned the canary, and recorded 100 client-log rows with zero router fatals, errors, or retries. |
| 2026-07-25 | Added **`claude-opus-5`**; retired **`claude-sonnet-4.5`**. New `ModelProfile.EffortsRejectedWhenThinkingDisabled` models the catalog's **first cross-field constraint**, and a new `ThinkingPolicy.AdaptiveOrDisabled` preset | opus-5 appeared in `/models`; every field probed rather than inherited from opus-4.8 (`ModelProfileProbe.Opus5_*`) — and two axes genuinely differ from the family default. **(1) Cross-field:** with `thinking:disabled`, opus-5 rejects effort `xhigh`/`max` (*"…not supported when thinking is disabled on this model. Use effort 'high' or below, or enable thinking"*) while accepting each field alone — invisible to the single-axis probe matrix, and Claude Code emits exactly that pair at max effort. Modeled as a disabled-thinking-only clamp (`ProfileAdjuster.ClampEffortForDisabledThinking`, clamps `max`/`xhigh`→`high`) rather than by narrowing `AcceptedEfforts`, which would silently downgrade every *thinking-on* max request. Proven live: with the constraint removed, a real `claude.exe` run sent `disabled`+`max` upstream and Copilot 400'd. **(2) Thinking policy:** opus-5 accepts `disabled` (200), rejecting only `enabled`, so reusing opus-4.8's `AdaptiveOnly` would coerce a user's explicit thinking-off up to adaptive — silently re-enabling and billing reasoning they turned off — and would make the clamp unreachable. Also probed: all five effort tiers, mid-conv `role:"system"` under the 4.8 placement rule, and native 1M (677k-token prompt → 200, no `StripBetas`); client-side 1M confirmed independently from `claude.exe` 2.1.220's bundled `native_1m` capability entry. sonnet-4.5 deleted on a live 400 (`model_not_supported`), **not** on its absence from `/models` — the integrator allowlist still advertises it (plus opus-4.5, `claude-fable-5`, `claude-opus-4.8-fast`, all of which also 400), so neither list is authoritative and only a probe decides. Verified end-to-end by a real `claude.exe` multi-tool run at `CLAUDE_CODE_EFFORT_LEVEL=max` (clamp observed on the wire: `IN[disabled/max] → UP[disabled/high]`, all upstream 200) and banked as `OneMillionContextRoutingTests.Opus5_*` replays. See pipeline-design.md §7.2/§7.3, context-window.md |
| 2026-08-11 | Authentication is a two-tier lifecycle: encrypted versioned GitHub OAuth credential + compatibility mirror, then an immutable in-memory Copilot token/endpoint lease; GitHub and CAPI 401 recovery are single-replay and credential-instance/generation-aware | A reported “gpt-5.6 401” reproduced as GitHub REST `Bad credentials` on `/user`, Copilot-token exchange, and model listing. Restarting reused the rejected on-disk token; device login recovered temporarily. The old device DTO discarded optional `expires_in`/rotating refresh-token fields, and inference 401s never invalidated the Copilot bearer. The new store preserves refresh state when GitHub supplies it, with atomic path-locked rotation, fresh-login identity, and legacy rollback compatibility; `AuthService` uses receipt-relative Copilot deadlines, atomic leases, typed transient-vs-terminal refresh failures, and safe one-replay semantics. The affected account's fresh OAuth response was valid but non-refreshable, so recurrence is active revocation requiring interactive login—not a one-hour expiry the bridge can silently refresh. Tokens and token fragments are prohibited from diagnostics. See pipeline-design.md §4.10 and token-storage.md. |
| 2026-08-13 | Client connection commands own connection/auth fields only; timeout, retry, watchdog, first-party/1M, telemetry, and fallback policy are user-owned | Supersedes the July connection-command policy that removed `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` and derived Claude timeouts. `config claude-code` now changes only `ANTHROPIC_BASE_URL` plus an absent token placeholder; `config codex` surgically upserts provider identity/auth while preserving provider behavior fields. Startup prints the exact bridge budgets beside observed global Claude/Codex values, with source, scope, retry, keepalive, and visibility caveats—no hidden margin, clamp, fallback, or client rewrite. See `docs/timeout-chain.md` and the `complete-codex-timeout-parity` OpenSpec change. |
| 2026-08-14 | A first authenticated CAPI 401 or 403 rejects one immutable Copilot lease generation and receives one exact replay; a replayed 403 alone is terminal policy/entitlement. Failed live `/models` overlays use the explicit process-local `LiveOverlayFailureCooldownSeconds` (default 300). | A production Codex turn received five `forbidden` responses and repeated metadata 403s until bridge restart discarded the stale in-memory bearer. The official client invalidates its cache on both statuses. A shared replay flag bounds mixed sequences, generation comparison reuses a concurrent N+1, and the explicit cooldown prevents periodic catalog polling from hammering the same failure without conflating metadata degradation with inference. |
| 2026-08-24 | CredentialService exclusively owns one exe-local, encrypted `github_credentials.dat`: version 1 preserves migrated Copilot Plugin state until true terminal rejection; the next explicit login writes version 2 using built-in GitHub CLI OAuth and direct CAPI. | Security-log token identities proved repeated logins exceeded the old App's ten-token limit (`oauth_access.destroy`, `max_for_app`), but forcing every upgrade to reauthenticate would discard still-working credentials. The service transactionally prefers/migrates complete `github_credentials.v2.dat`, falls back to raw `github_token.dat`, verifies the new file, then deletes both old files. Runtime behavior is selected by the new file's version, never filename or token prefix. AuthService sees only an immutable credential lease. |
| 2026-08-24 | Native Codex catalog compaction defaults to 85% of total context, and the exact Copilot pre-stream context 400 becomes a Codex-native `response.failed/context_length_exceeded` terminal. | A production parent-thread compaction request exceeded Copilot's admission boundary. Copilot's HTTP 400 `invalid_request_body` shape was a generic bad request to Codex, so its built-in oldest-history trimming never ran. The client-edge adapter preserves raw upstream evidence, requires the exact bounded tuple, and was proven with real Codex: 400 → smaller compact retry → summary → custom exec/output → completed turn, zero router fatals. |
| 2026-08-25 | Updater-managed target and rollback launches prove local serving health without touching credentials or GitHub/Copilot authentication before `Ready`; after sending `Ready` they asynchronously resume the ordinary auth bootstrap, while ordinary launches keep the existing synchronous auth gate. | Token expiry, revocation, or a transient refresh failure is external recoverable state, not evidence that the new binary is unhealthy. Gating `Ready` on startup refresh caused a permanent update → auth failure → rollback → retry loop, while deferring until the first client request hid the first-run device-code prompt after an update. Post-Ready bootstrap preserves rollback inputs throughout readiness, restores the login UX, and treats later auth failure as non-fatal to the serving binary. |
| 2026-08-26 | Fresh login returns to the official GitHub Copilot Plugin App and writes credential version 3 with an explicit `oauth_client_id`; versions 1 and 2 remain compatible. | GitHub limits OAuth tokens to ten per user/application/scope tuple and revokes an existing token when another is minted beyond that bound. Reusing GitHub CLI's public App ID made bridge logins compete with every other `gh`-family login and increased `max_for_app` eviction. Version 3 keeps the unified encrypted credential service while binding refresh to the recorded issuer and restoring the official Copilot exchange path. |
| 2026-08-26 | A conspicuous top-level Authentication switch keeps official Copilot Plugin v3 as the default but can opt the next login into an explicitly configured OAuth App, persisted as refreshable direct credential version 4. | A project-owned public Device Flow client isolates its ten-token pool without requiring Marketplace publication or a shipped secret. The setting is deliberately false by default, affects only explicit future login, and records the actual issuer so config changes cannot break refresh. Live proof found custom OAuth plus `vscode-chat` authenticated but exposed only legacy models and rejected gpt-5.6; scoping GitHub Copilot SDK's `copilot-developer-cli` integration identity to v4 completed a real Codex multi-tool loop. Older versions retain `vscode-chat`; v4 never enters the App-identity-sensitive internal token exchange. |
