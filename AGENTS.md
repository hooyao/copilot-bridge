# AGENTS.md

Guidance for AI coding agents (Antigravity, Gemini CLI, Claude Code, …) working
in this repo. Antigravity / Gemini load this file automatically; Claude Code
reads `CLAUDE.md`, which points here. **This file is the single source of truth
for how to build, run, and contribute** — `docs/` holds the deep references.

## What this is

A .NET 10 **Native AOT** reverse proxy that re-exposes the **GitHub Copilot LLM
API** under per-client URL prefixes, so Claude Code / Codex / Gemini CLI can use
Copilot as their model backend. Ships as a single **~12 MB `.exe`** with no .NET
runtime dependency — Native AOT is chosen specifically to keep the binary small,
so trimming + source-generated JSON are mandatory, not optional.

**As-built (M1, done):** Claude Code (Anthropic Messages shape) →
`POST /cc/v1/messages` → **byte-level passthrough** to Copilot's *native*
`/v1/messages` endpoint. There is **no** Anthropic↔OpenAI translation on this
path: Copilot turned out to expose a native Anthropic endpoint (discovered after
the first design; see `docs/design.md` v0.2). Translating to OpenAI (Codex) and
Gemini shapes is future work (M3/M4).

> ⚠️ **Watch for stale "planned" prose.** Parts of `CLAUDE.md` ("Architecture
> (planned)", "first milestone … translated to Copilot OpenAI Chat Completions")
> and the original design doc describe an OpenAI-Chat-Completions translation
> path. That was the **pre-research plan**; the shipped M1 is Anthropic
> passthrough. When two docs disagree, trust **`docs/pipeline-design.md`** (the
> architectural contract) and **`README.md`** (as-built status).

## Environment

- **Windows** + PowerShell as the default shell (Bash also available for POSIX scripts).
- **.NET 10 SDK**.
- **Visual Studio C++ Build Tools** workload + **Windows SDK** — required only for the AOT native linker (JIT build/run/test work without them).

## Setup

```pwsh
git clone https://github.com/hooyao/copilot-bridge
cd copilot-bridge
```

**`references/` is not in the repo** (gitignored — it's a separate, read-only
checkout). Several docs and code comments cite it as the canonical Copilot
protocol reference. To get it:

```pwsh
git clone https://github.com/ericc-ch/copilot-api references/copilot-api
```

Read it before touching protocol-level code (headers, auth flow, model ids).
**Never edit anything under `references/`.**

## Build / run / publish / test

```pwsh
# Develop (JIT, fast iteration)
dotnet run --project src/CopilotBridge.Cli -- serve      # default port 8765

# Debug build (whole solution)
dotnet build CopilotBridge.slnx

# Unit tests — pure logic, no network/Copilot. This is what CI runs.
dotnet test tests/CopilotBridge.UnitTests
# Solution-wide, skipping the live-Copilot integration harness:
dotnet test CopilotBridge.slnx --filter "Category!=Integration"
# Integration harness — real claude.exe + live Copilot login (tests/harness/
# README.md). Tagged [Trait("Category","Integration")]; run explicitly.
dotnet test tests/CopilotBridge.Playground
dotnet test --filter FullyQualifiedName~<TestName>       # single test
```

**Publish the single-file AOT exe (the release artifact):**

```pwsh
.\build-aot.bat
#  → .\publish\copilot-bridge.exe
```

`build-aot.bat` handles the Windows AOT-linker prerequisites for you: it adds
`vswhere.exe` to PATH, uses it to locate the VS install, runs `VsDevCmd.bat` to
put MSVC `link.exe` on PATH, then calls `dotnet publish`.

⚠️ **Why the script (don't skip it):** a bare `dotnet publish -c Release` fails
the AOT native-link step on most setups. The compiler (ILC) shells out to
`vswhere.exe` to find `link.exe`, but `vswhere`
(`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`) isn't on PATH by
default — not even inside a VS Developer prompt — so it fails with
`'vswhere.exe' is not recognized`. The script fixes exactly that. JIT
`run`/`build`/`test` need none of this.

After any dependency change, eyeball the published `.exe` size — a
non-trim-friendly package can easily double it (`docs/size-history.md` tracks it).

## First run + pointing a client at the bridge

The first `serve` with no token on disk runs the GitHub **device-code flow**: it
prints a verification URL + user code, blocks until you authorize in the browser,
then DPAPI-encrypts the token next to the exe (`CurrentUser` scope — Windows owns
the key). Subsequent starts are silent. `auth login` runs just the handshake;
`debug list-models` confirms Copilot is reachable and dumps the available models.

Point Claude Code at the bridge via `.claude/settings.local.json`:

```jsonc
{ "env": {
  "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
  "ANTHROPIC_AUTH_TOKEN": "dummy",
  "ANTHROPIC_MODEL": "claude-sonnet-4.6"
}}
```

The `/cc` suffix is the Claude Code URL prefix — don't omit it.

## Architecture map — where to read

- **`docs/pipeline-design.md`** — the architectural contract. Typed pipeline,
  stages, strategies, adapters; the routing system (§7: per-model profiles +
  nginx-style `Routing.Locations`); logging/tracing (§9). **Update this doc
  first when changing the framework.**
- **`docs/routing.md`** — config-author's reference for `Routing.Locations`
  (the nginx-style match/rewrite layer): When/Use syntax, examples, validation.
  Read before editing routing config in `appsettings.json`.
- **`docs/copilot-api-research.md`** — protocol-level facts: every header,
  model quirk, what Copilot's gateway accepts/rejects. Read before any
  protocol change.
- **`docs/design.md`** — dated decision log (including the M1
  OpenAI→Anthropic-passthrough pivot and the routing redesign).
- **`docs/size-history.md`** — AOT binary size per change.
- **`README.md`** — as-built status, quick start, limitations, roadmap.
- **`tests/harness/README.md`** — end-to-end headless harness instructions.

Source layout (outer depends on inner only):
`Hosting/` → `Endpoints/` → services (`Auth/`, …) → infra (`Copilot/` HTTP
client) → `Models/` (DTOs + the single `JsonContext`). Request/response
transformation lives in `Pipeline/` (`Stages/`, `Strategies/`, `Adapters/`,
`Routing/`).

## Hard invariants — don't fight these

- **Native AOT is non-negotiable.** `<PublishAot>true</PublishAot>`. No
  reflection-based serialization, no `Activator.CreateInstance`, no
  runtime-loaded assemblies, no `System.Reflection.Emit`.
- **All JSON goes through `Models/JsonContext.cs`** (source-generated
  `JsonSerializerContext`). Every new DTO needs a `[JsonSerializable(typeof(...))]`
  entry. Reflection-based `JsonSerializer.Serialize(obj)` silently emits `{}`
  under AOT — treat it as a build-time mistake, not a runtime one.
- **ASP.NET minimal APIs must use strongly-typed delegate params** (no
  `[FromBody] dynamic`, no `object`) to stay AOT-friendly.
- **`AuthService` is a sealed facade.** Callers only call
  `GetCopilotTokenAsync()`; they never touch device-code flows, token files, or
  refresh timers. Don't pierce the abstraction.
- **Match the official VS Code Copilot client on the wire.** For any
  header / version / beta detail, mirror
  `references/copilot-api/src/lib/api-config.ts` and the VS Code `chatEndpoint.ts`
  snippets cited in code comments. Missing/mismatched values cause *silent*
  Copilot rejections.
- **Model profiles are playground-derived facts, not guesses.**
  `Pipeline/Routing/ModelProfileCatalog.cs` is sourced from
  `tests/CopilotBridge.Playground/ModelProfileProbe.cs`. Re-run that probe after
  Copilot ships or changes a model; don't extrapolate from family names (sibling
  models surprise you — haiku-4.5 ≠ sonnet-4.6 on thinking).

## Conventions

- **Repo files are English** (docs, code comments, commit messages) regardless
  of chat language. Match the *user's* language in chat replies.
- Default everything to `internal` (single-project codebase; don't deliberate
  accessibility modifiers).
- PowerShell-shaped commands by default.
- Keep local-only scratch out of commits (all gitignored already): `references/`,
  `.mcp.json`, `github_token.dat`, `log/`, `request-traces/`, session-handoff
  `.txt` dumps.
- **New tests:** pure logic → `tests/CopilotBridge.UnitTests` (CI runs these —
  fast, no deps). Anything needing live Copilot or `claude.exe` →
  `tests/CopilotBridge.Playground`, tagged `[Trait("Category", "Integration")]`
  so CI skips it. Forgetting the trait makes CI try to run it (and fail).
- **Logging/tracing:** the text log at `<exe-dir>/log/bridge-<stamp>.log` is
  always on (startup banner, stage debug, errors); per-request JSON audit is
  **opt-in** via `"Tracing": { "Enabled": true }` in `appsettings.json` (off by
  default — traces contain full prompts), written to `request-traces/`. Log
  levels are per-category in `appsettings.json` `Logging:LogLevel`.

## Routing config (`appsettings.json` → `Routing.Locations`)

nginx-style: each location is a `When` (match) → `Use` (change-set) closure,
first-match-wins, no chain. `When` is a `MatchExpression` tree (`AllOf`/`AnyOf`
+ `Model`/`Effort`/`Header` leaves; top-level fields are implicitly AND-ed).
`Use` may swap `Model`, remap effort per-target (`EffortMap`), and `Set`/`Remove`
a whitelisted header (`anthropic-beta`, `Editor-Version`, `Editor-Plugin-Version`,
`Copilot-Integration-Id`, `X-GitHub-Api-Version`). After a location fires, the
target model's `ModelProfile` still coerces the body to what Copilot's gateway
actually accepts. **Full reference: [`docs/routing.md`](docs/routing.md)**
(syntax, examples, validation, testing); framework contract in
`docs/pipeline-design.md` §7.
