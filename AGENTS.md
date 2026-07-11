# AGENTS.md

Guidance for AI coding agents (Antigravity, Gemini CLI, Claude Code, …) working
in this repo. Antigravity / Gemini load this file automatically; Claude Code
reads `CLAUDE.md`, which points here. **This file is the single source of truth
for how to build, run, and contribute** — `docs/` holds the deep references.

## What this is

A .NET 10 **Native AOT** reverse proxy that re-exposes the **GitHub Copilot LLM
API** under per-client URL prefixes, so Claude Code / Codex / Gemini CLI can use
Copilot as their model backend. Ships as a single **~12 MB native binary** with
no .NET runtime dependency — Native AOT is chosen specifically to keep the binary
small, so trimming + source-generated JSON are mandatory, not optional. Builds
for **win-x64, win-arm64, linux-x64, osx-arm64** (each on its own runner — Native
AOT cannot cross-OS compile).

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

- **Cross-platform**: builds + runs on Windows, Linux, and macOS. Development is
  primarily on **Windows** + PowerShell (Bash also available for POSIX scripts).
- **.NET 10 SDK**.
- **AOT linker toolchain** (only needed to *publish* the native binary, per OS;
  JIT build/run/test need none of it): Windows → **Visual Studio C++ Build Tools**
  + **Windows SDK**; Linux → **`clang` + `zlib1g-dev`**; macOS → **Xcode Command
  Line Tools**. You can only AOT-build for the OS you are on — Native AOT does not
  cross-OS compile, so the four release RIDs each build on their own CI runner.

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

**Publish the single-file AOT binary (the release artifact):**

```pwsh
# Windows (local convenience wrapper):
.\build-aot.bat
#  → .\publish\copilot-bridge.exe

# Any OS, explicit RID (win-x64 | win-arm64 | linux-x64 | osx-arm64):
dotnet publish src/CopilotBridge.Cli -c Release -r <rid> -o ./publish
```

`build-aot.bat` handles the **Windows-local** AOT-linker prerequisites for you:
it adds `vswhere.exe` to PATH, uses it to locate the VS install, runs
`VsDevCmd.bat` to put MSVC `link.exe` on PATH, then calls `dotnet publish`.

⚠️ **Why the script locally (don't skip it on a Windows dev box):** a bare
`dotnet publish -c Release` fails the AOT native-link step on most local setups.
The compiler (ILC) shells out to `vswhere.exe` to find `link.exe`, but `vswhere`
(`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`) isn't on PATH by
default — not even inside a VS Developer prompt — so it fails with
`'vswhere.exe' is not recognized`. The script fixes exactly that. JIT
`run`/`build`/`test` need none of this. **CI does not use the script**: the
GitHub-hosted images expose the toolchain (MSVC / clang / Xcode CLT) to the SDK's
AOT targets directly, so the workflows call a plain `dotnet publish -r <rid>`.
The release pipeline builds all four RIDs, each on its own runner
(`windows-latest`, `windows-11-arm`, `ubuntu-latest`, `macos-14`), and attaches
every archive (+ a macOS `.pkg`) to one GitHub Release.

> **Agents publishing manually (when you can't run `build-aot.bat` directly):**
> importing the VsDevCmd environment is **not enough** — VsDevCmd does not add the
> VS Installer dir (where `vswhere.exe` lives) to PATH, and ILC's link step calls a
> bare `vswhere.exe`. After importing the VS env you MUST also append
> `;C:\Program Files (x86)\Microsoft Visual Studio\Installer\` to `PATH`, or the
> link fails with `'vswhere.exe' is not recognized` (exit 123) right after
> `Generating native code`. The verified one-block recipe is in `CLAUDE.md`.

After any dependency change, eyeball the published binary size — a
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

> **This project plans and tracks work with OpenSpec.** For *current* work —
> what's proposed, in progress, or pending implementation — read
> `openspec/changes/` (each change has `proposal.md` / `design.md` / `specs/` /
> `tasks.md`). Run `openspec list` to see active changes and `openspec status
> --change "<name>"` for remaining tasks; `/opsx:apply <name>` to implement,
> `/opsx:propose` to start a new change. **Do not record progress or status in
> this file** — AGENTS.md / CLAUDE.md are the project's stable constitution
> (how to build, the invariants, the conventions), not a status log. When a
> change introduces a durable architectural fact, fold *that fact* into the
> design docs below once it's implemented — not the in-flight task state.

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

- **🔴 Tests are written from the CONTRACT, never by reading the implementation.**
  A test asserts *what the behaviour is required to be* (from the requirement /
  spec / case), not *what the code currently does*. Mirroring the implementation
  back into assertions is forbidden — it freezes bugs into the suite (green on
  buggy code → can never catch that bug) and manufactures false confidence.
  Discipline: (1) state the contract in words first — *given X, the system must
  do Y, because Z* — and assert that; if you can't articulate it without pointing
  at a code line, you don't yet understand what to test. (2) Assert observable
  behaviour and invariants (bytes out, events emitted, error surfaced,
  idempotence, "zero allocation when off") over internal state. (3) **A new test
  that passes on the first try is suspect** — mutation-check it: break the product
  code and watch it go red; if it stays green it guards nothing. (4) When a
  from-contract test fails, assume the *code* (or your understanding of the
  contract) is wrong — investigate before touching the test; never weaken an
  assertion just to get green. This outranks convenience: fewer contract-true
  tests beat many implementation-mirrors.
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
- **Token storage dispatches on the OS at runtime, not at compile time.**
  `TokenStore` keeps one public surface and picks `WindowsDpapiTokenProtector`
  (DPAPI) on Windows vs `DerivedKeyTokenProtector` (machine-derived
  AES-256-CBC+HMAC) on Linux/macOS via `OperatingSystem.IsWindows()`. The DPAPI
  type is the **only** `[SupportedOSPlatform("windows")]`-attributed surface in
  the assembly — **do not re-add an assembly-level platform attribute** (it
  re-breaks the non-Windows build) and do not call DPAPI outside that guarded
  type. See `docs/token-storage.md`.
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
- **🔴 A FIX IS NOT DONE UNTIL A REAL HEADLESS CLIENT EXECUTED A COMPLEX TASK
  THROUGH IT — THIS STEP IS NEVER OPTIONAL AND NEVER SKIPPABLE.** Unit tests,
  offline round-trips, and "the bridge returned HTTP 200" are necessary but NOT
  sufficient, and treating any of them as proof is forbidden. They missed *four*
  production bugs in a row (`additional_tools` 400; custom-tool `exec` response-side
  arg loss; its request-side echo 400; and exec being sent as `function_call` when
  codex 0.144.1 requires `custom_tool_call`, so **every exec fataled with
  "incompatible payload" while every bridge response was still 200**). That last one
  is the cautionary tale: the fix was "verified" for weeks by bridge-side 200s and
  green unit tests while real exec was 100% broken — because **a bridge 200 only
  means the UPSTREAM accepted the request; it says NOTHING about whether the
  DOWNSTREAM client could parse and execute what the bridge sent back.**
  - **The acceptance standard is the CLIENT'S OWN EXECUTION RESULT, not the
    bridge's status code or your own tests.** Drive a **real headless client**
    (`codex.exe` / `claude.exe`) through the running bridge on a genuinely complex,
    multi-step, multi-tool task, and confirm success from *the client's* evidence:
    - **Codex** writes structured logs to `~/.codex/logs_2.sqlite` (table `logs`;
      no `sqlite3` here — use a tiny `Microsoft.Data.Sqlite` reader, `Mode=ReadOnly`).
      Success = the tool actually executed (output present, not `aborted`) AND **no**
      `ERROR codex_core::tools::router` / `incompatible payload` / dispatch fatal.
      A green bridge audit is not enough — read the client's log.
    - **Claude Code** → confirm the real `claude.exe` turn completed and tool calls
      executed, not just that the bridge streamed 200.
  - **You may NOT substitute** a unit test, an in-process fixture, a synthetic SSE
    replay, or a bridge-side 200 for this step. If the user asked for a
    headless-client test, run the headless client. Doing otherwise once already
    cost the user days of a silently-broken exec loop.
  - **Codex (`/codex/responses`, `gpt-*`)** → real `codex.exe` (`codex exec
    "<prompt>"` runs headless; the desktop app path exercises custom `exec`/grammar
    tools). One turn is not enough — failures show on the SECOND turn (client echoes
    a prior call back) or only when the client EXECUTES a call (payload-shape
    mismatch). Reproduce execution, not just acceptance.
  - **Claude Code (`/cc`, `claude-*`)** → real `claude.exe`, multi-tool task.
  - **Claude Code → gpt** → point `claude.exe` at the bridge with the route
    mapping `claude-*` → a `gpt-*` backend (`docs/routing.md`) and run a complex
    task so the CC→gpt translation is exercised end-to-end.
  - If you genuinely cannot run the real client, STOP, say so, and mark the fix
    **UNVERIFIED** — never dress up unit-test or 200 evidence as a real-client pass.
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
