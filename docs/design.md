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

The single hard non-functional goal is a **single-file, small-footprint `.exe`** with no .NET runtime dependency. AOT isn't picked for cold-start speed — it's picked for deploy simplicity and binary size. Every dependency choice (no reflection-based JSON, no `IHttpClientFactory`, no `wwwroot/` static files) follows from that.

### 2.1 Required project settings

- `<PublishAot>true</PublishAot>` + `<OptimizationPreference>Size</OptimizationPreference>`
- `<InvariantGlobalization>true</InvariantGlobalization>`, `<UseSystemResourceKeys>true</UseSystemResourceKeys>`, `<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>`
- Release build: disable `DebuggerSupport`, `StackTraceSupport`, `MetricsSupport`, `EventSourceSupport`, `HttpActivityPropagationSupport`
- All JSON serialization through `JsonSerializerContext` — every DTO has a `[JsonSerializable(typeof(...))]` entry on `Models/JsonContext.cs`

### 2.2 Forbidden

- `JsonSerializer.Serialize(obj)` without a `JsonTypeInfo` overload — silently breaks at runtime (output becomes `{}`)
- `Activator.CreateInstance(Type)` / `Type.GetType(string)` / dynamic loading
- `[FromBody] dynamic` or `object` parameters on minimal API delegates
- `IHttpClientFactory` (default impl is AOT-friendly but adds size; a singleton `HttpClient` is enough)
- `wwwroot/` static files (use `<EmbeddedResource>` for HTML/JS/CSS)

### 2.3 Size monitoring

After every dependency change record the published `.exe` size in [`size-history.md`](size-history.md). Budget: under 25 MB. Each dependency PR notes its incremental cost.

### 2.4 Dependency allowlist

Currently in the bridge runtime (`src/CopilotBridge.Cli/`):

- `System.Net.Http`, `System.Text.Json`, `Microsoft.AspNetCore.App` (BCL / shared framework)
- `System.CommandLine` 3.0-preview.3 (AOT-optimized; auto `--help`/`--version`, typed handlers)
- `System.Security.Cryptography.ProtectedData` (DPAPI for GitHub-token-at-rest encryption)
- `Serilog` + `Sinks.Console` + `Sinks.File` (AOT-clean since Serilog PR #2175; +1.5 MB accepted)

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
