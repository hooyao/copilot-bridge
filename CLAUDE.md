# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **`CLAUDE.md` (this file, for Claude Code) and [`AGENTS.md`](AGENTS.md) (for other agents) are parallel and equivalent** — same project guidance for different agent harnesses. Keep them in sync: a substantive edit to one must be mirrored in the other. Both are as-built alongside `docs/pipeline-design.md`; some "planned" sections below predate the M1 OpenAI→Anthropic-passthrough pivot and are flagged as such.

## Project intent

A .NET 10 **Native AOT** reverse proxy that exposes the GitHub Copilot LLM API as Anthropic-compatible (`/v1/messages`) and OpenAI-compatible (`/v1/chat/completions`) endpoints, so Claude Code and similar tools can use Copilot as a backend. Ships as a **single small `.exe`** with no .NET runtime dependency — Native AOT is chosen specifically to keep the binary small; trimming and source-generated JSON are not optional.

A simple HTML config page is served from the same process for one-time GitHub auth (device-code flow) and runtime settings (port, account type, model selection).

**M1 (shipped):** Claude Code → `POST /cc/v1/messages` → **byte-level passthrough** to Copilot's *native* Anthropic `/v1/messages` endpoint. The original plan (translate to Copilot's OpenAI Chat Completions, the two flows listed below) was dropped once research found Copilot exposes a native Anthropic endpoint — see `docs/design.md` v0.2. OpenAI (Codex) and Gemini translation paths are M3/M4.

The original first-milestone sketch, kept for context:
- Claude Code → `POST /v1/messages` → translated to Copilot OpenAI Chat Completions
- Claude Code → `POST /v1/chat/completions` → forwarded to Copilot

## Reference implementation

`references/copilot-api/` is a clone of https://github.com/ericc-ch/copilot-api (Bun/TypeScript). It is the canonical reference for the Copilot auth flow and the Anthropic↔OpenAI translation logic. **Read it before designing or changing protocol-level code.** Key files:

- `src/lib/api-config.ts` — every header, URL, and version constant the Copilot endpoint expects. These have to match what VS Code sends or Copilot rejects the request.
- `src/services/github/get-device-code.ts`, `poll-access-token.ts`, `get-copilot-token.ts` — the GitHub OAuth device-code flow.
- `src/lib/token.ts` — auth orchestration (read token from disk → device flow if missing → fetch Copilot token → refresh on a `refresh_in - 60` timer).
- `src/routes/messages/non-stream-translation.ts` and `stream-translation.ts` — Anthropic↔OpenAI conversion. Streaming is significantly harder than non-streaming: it needs a state machine, and tool-call arguments arrive as JSON fragments that must be reassembled per block.
- `src/server.ts` — the route table to mirror.

The reference is in-tree for reading only. **Never edit files under `references/`** — and note `references/copilot-api/.git` exists, so don't `git add` that directory if you later init this repo (add `references/` to `.gitignore`).

## Architecture (original plan — see AGENTS.md / docs/pipeline-design.md for as-built)

> The diagram below is the *initial* sketch. As-built M1 uses per-client URL
> prefixes (`/cc/v1/messages`), passes through to Copilot's **native Anthropic**
> endpoint (not `/chat/completions`), and resolves the base URL from the Copilot
> token (enterprise → `api.enterprise.githubcopilot.com`). The HTML config page
> is M2 (not built yet).

```
Claude Code ──► /v1/messages (Anthropic)         ┐
Claude Code ──► /v1/chat/completions (OpenAI)    ├─► CopilotClient ─► https://api.githubcopilot.com/chat/completions
Browser     ──► / (HTML config + auth)            ┘
```

Critical protocol details — easy to get wrong:

- **Two tokens, different lifetimes.** A long-lived **GitHub OAuth token** (obtained via device code, persisted as `github_token.dat` **next to the .exe**, encrypted at rest — **Windows: DPAPI** in `CurrentUser` scope, Windows owns the key; **Linux/macOS: AES-256-CBC + HMAC-SHA256** with a key derived from machine id + username, since DPAPI is Windows-only — see `docs/token-storage.md`) and a **short-lived Copilot token** kept only in memory and refreshed on a `refresh_in - 60s` timer.
- **Account type changes the URL.** `individual` → `api.githubcopilot.com`; `business`/`enterprise` → `api.<type>.githubcopilot.com`. Make this configurable.
- **Copilot expects VS Code-shaped requests.** `editor-version`, `editor-plugin-version`, `user-agent`, `copilot-integration-id`, `x-request-id`, `x-github-api-version`, etc. Replicate the set in `references/copilot-api/src/lib/api-config.ts` exactly — missing or mismatched values cause silent rejections.
- **`X-Initiator` header**: set to `agent` if any incoming message has role `assistant` or `tool`, else `user`. Affects Copilot quota accounting.
- **GitHub Copilot client ID is `Iv1.b507a08c87ecfe98`** with scope `read:user`. This is GitHub's official Copilot OAuth app — do not change it.
- **Model name normalization.** Claude Code sends versioned model IDs (`claude-sonnet-4-20250514`, `claude-opus-4-20250101`, etc.) but Copilot only accepts unversioned `claude-sonnet-4` / `claude-opus-4`. Normalize before forwarding.
- **Anthropic message ordering.** When translating, `tool_result` blocks must come before any user content blocks in the OpenAI message list to maintain the `tool_use → tool_result → user` protocol order.
- **Streaming translation.** Copilot returns OpenAI SSE chunks; for `/v1/messages` translate them on-the-fly into the Anthropic event sequence (`message_start` → `content_block_start`/`_delta`/`_stop` per block → `message_delta` → `message_stop`). Tool-call argument deltas arrive as partial JSON strings and must be forwarded as `input_json_delta` per the originating tool index.

### Layering & encapsulation

Layers, outer depends on inner only:

`Hosting/` → `Endpoints/` → application services (`Translation/`, `Auth/`, …) → infrastructure (`Copilot/` HTTP client, file stores) → `Models/` (pure DTOs + `JsonSerializerContext`).

- **`AuthService` is self-contained.** It owns the GitHub OAuth token, the Copilot token, file persistence, in-memory caching, and the background refresh timer. Callers (`CopilotClient`, endpoints) only ever ask `authService.GetCopilotTokenAsync()` — they do not know about device-code flows, token files, or refresh windows. Anything auth-internal stays inside `Auth/`; do not pierce the abstraction from the outside.
- **`Endpoints/`** orchestrate but contain no business logic — one file per route group, each handler owns request/response shape and error mapping.
- **`Translation/`** is pure functions over DTOs. The streaming variant is a per-request state machine; do not share its state across requests.
- **`Models/JsonContext.cs`** is the single `JsonSerializerContext` for the entire app. Every new DTO gets a `[JsonSerializable(typeof(...))]` entry. Treat reflection-based `JsonSerializer.Serialize(obj)` calls as a build-time mistake, not a runtime one.

## AOT constraints — please don't fight them

- `<PublishAot>true</PublishAot>` and `<SelfContained>true</SelfContained>` are non-negotiable for this project.
- All JSON serialization MUST go through a `JsonSerializerContext` (source generator). Reflection-based `JsonSerializer.Serialize(obj)` is silently broken under AOT — output is often an empty `{}`. Every DTO needs a `[JsonSerializable(typeof(...))]` entry on the context.
- ASP.NET minimal APIs are AOT-friendly **only** when delegate parameters are strongly typed (no `[FromBody] dynamic`, no `object`).
- No `Activator.CreateInstance`, no runtime-loaded assemblies, no `System.Reflection.Emit`. When picking dependencies, prefer ones with explicit AOT support — `Microsoft.AspNetCore.OpenApi`, `System.Net.Http`, `System.Text.Json` are fine; many older NuGet packages are not.
- Embed the HTML config page via `<EmbeddedResource>`, not `wwwroot/`, so the publish output stays a single file.
- Track binary size. After any dependency change run `dotnet publish -c Release -r win-x64` and eyeball the published `.exe` — a non-trim-friendly package can easily double the size.

## Build / run / test

- Develop: `dotnet run --project src/CopilotBridge.Cli`
- Publish single-file AOT exe (Windows): **`.\build-aot.bat`** (wraps `dotnet publish src/CopilotBridge.Cli -c Release -r win-x64`; requires the VS C++ Build Tools workload + Windows SDK). The script exists because a bare `dotnet publish` fails the AOT native link: ILC shells out to `vswhere.exe` to find `link.exe`, but `vswhere` (`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`) isn't on PATH by default — not even in a VS Developer prompt. `build-aot.bat` adds it, runs `VsDevCmd.bat`, then publishes. JIT `dotnet run`/`build`/`test` need none of this.
  - **Agents: do NOT wrap `build-aot.bat` through the Bash tool.** Its internal `call VsDevCmd.bat` exits the parent script early when invoked as `cmd /c "build-aot.bat > log 2>&1"`, so `dotnet publish` silently never runs (you get a 3-line cmd banner and a stale exe); and `cmd /c build-aot.bat | tee log | tail` reports `tail`'s exit code, not the build's — both fake success. Instead run this **one PowerShell block** (verified working — imports the VS x64 env into the session, then publishes directly):
    ```powershell
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    $dir = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $envLines = & cmd /c "`"$dir\Common7\Tools\VsDevCmd.bat`" -arch=x64 -host_arch=x64 >nul 2>&1 && set"
    foreach ($l in $envLines) { if ($l -match '^([^=]+)=(.*)$') { Set-Item "Env:$($matches[1])" $matches[2] } }
    # REQUIRED: VsDevCmd does NOT put the VS Installer dir (vswhere.exe) on PATH,
    # but ILC's native-link step shells out to a bare `vswhere.exe` — without this
    # line the link fails with `'vswhere.exe' is not recognized` (exit 123) AFTER
    # `Generating native code`. This mirrors build-aot.bat's first line.
    $env:PATH = "$env:PATH;C:\Program Files (x86)\Microsoft Visual Studio\Installer\"
    dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
    ```
    Confirm success by the published exe's **mtime** (`publish\copilot-bridge.exe`, ≈11 MB), not the exit code — capture the mtime before building and prove it advanced. ILC native compile takes a few minutes; run it in the background and watch for `Generating native code` → `PUBLISH_EXIT: 0`. If you see `Generating native code` followed by `'vswhere.exe' is not recognized`, you skipped the PATH line above.
- Unit tests (CI-safe, no Copilot): `dotnet test tests/CopilotBridge.UnitTests`, or solution-wide skipping the integration harness: `dotnet test --filter "Category!=Integration"`.
- Integration harness (live Copilot + claude.exe): `dotnet test tests/CopilotBridge.Playground` — tagged `[Trait("Category","Integration")]`. New playground tests must carry that trait or CI will try to run them. Routing config reference: `docs/routing.md`.
- Single test: `dotnet test --filter FullyQualifiedName~<TestName>`
- **🔴 HIGHEST-PRIORITY TESTING DIRECTIVE — write tests from the CONTRACT, never by reading the implementation.** A test must assert *what the behaviour is required to be* — derived from the requirement/spec/case — not *what the code currently does*. Reading the implementation and asserting it back is forbidden: it freezes bugs into the suite (the test goes green on the buggy code, so it can never catch that bug) and gives false confidence. Concretely: (1) before writing a test, state the contract in words — given X, the system must do Y, *because* Z — and assert that; if you cannot articulate the contract without pointing at a line of code, you do not yet understand what to test. (2) Prefer asserting *observable behaviour and invariants* (bytes out, events emitted, error surfaced, idempotence, "zero allocation when off") over internal state. (3) **A new test that passes on the first run is suspect** — confirm it actually guards the contract by mutation-checking: temporarily break the product code and watch the test go red; if it stays green, it asserts nothing. (4) When a from-contract test fails, the default hypothesis is *the code (or your understanding of the contract) is wrong* — investigate before you touch the test; never weaken an assertion just to make it pass. This directive outranks convenience: a smaller suite of contract-true tests beats a large suite of implementation-mirrors.
- **NEVER bind the default port (8765) when running the bridge from a test or local script.** The user often has a real bridge running on 8765; collisions surface as a generic "address already in use" startup failure that's easy to misdiagnose. Always pass a non-8765 port: in-process fixtures use `WebHost.UseUrls("http://127.0.0.1:0")` (ephemeral); manual `dotnet run` for ad-hoc smoke tests should pass `serve --port 18765` (or any free high port). The Playground's `BridgeFixture` already follows this rule.
- **🔴 A FIX IS NOT DONE UNTIL A REAL HEADLESS CLIENT EXECUTED A COMPLEX TASK THROUGH IT — THIS STEP IS NEVER OPTIONAL AND NEVER SKIPPABLE.** Unit tests, offline round-trips, and "the bridge returned HTTP 200" are necessary but NOT sufficient, and treating any of them as proof is forbidden. They missed *four* production bugs in a row (`additional_tools` 400; custom-tool `exec` response-side arg loss; its request-side echo 400; and exec being sent as `function_call` when codex 0.144.1 requires `custom_tool_call`, so **every exec fataled with "incompatible payload" while every bridge response was still 200**). The last one is the cautionary tale: for weeks the fix was "verified" by bridge-side 200s and green unit tests while real exec was 100% broken, because **a bridge 200 only means the UPSTREAM accepted the request — it says NOTHING about whether the DOWNSTREAM client could parse and execute what the bridge sent back.**
  - **The acceptance standard is the CLIENT'S OWN EXECUTION RESULT, not the bridge's status code or your own tests.** You MUST drive a **real headless client** (`codex.exe` / `claude.exe`) through the running bridge on a genuinely complex, multi-step, multi-tool task, and confirm success from *the client's* evidence:
    - **Codex** writes structured logs to `~/.codex/logs_2.sqlite` (table `logs`; no `sqlite3` on this box — use a tiny `Microsoft.Data.Sqlite` reader, `Mode=ReadOnly`). Success = the tool actually executed (output present, not `aborted`) AND there is **no** `ERROR codex_core::tools::router` / `incompatible payload` / dispatch fatal. A green bridge audit is not enough — read the client's log.
    - **Claude Code** → confirm the real `claude.exe` turn completed and the tool calls executed, not just that the bridge streamed 200.
  - **You may NOT substitute** a unit test, an in-process fixture, a synthetic SSE replay, or a bridge-side 200 for this step. If the user asked for a headless-client test, run the headless client — do not report a fix as working off unit tests or status codes. Doing so once already cost the user days of a silently-broken exec loop.
  - **Codex-backend fix (`/codex/responses`, `gpt-*`)** → drive real `codex.exe` (the desktop app path exercises custom `exec`/grammar tools; `codex exec` CLI runs headless — `codex exec "<prompt>"` — and exercises function tools + multi-call loops). A single turn is not enough — key failures appear on the SECOND turn when the client echoes a prior tool call back (the request-side round-trip), or only when the client tries to EXECUTE a tool call (payload-shape mismatches). Reproduce execution, not just acceptance.
  - **Claude Code fix (`/cc`, `claude-*`)** → drive real `claude.exe` on a multi-tool task.
  - **Claude Code → gpt (`/cc` routed to a Codex model)** → configure `claude.exe` to point at the bridge with the route that maps `claude-*` → a `gpt-*` backend (see `docs/routing.md`), then run a complex `claude.exe` task so the CC→gpt translation is actually exercised end-to-end.
  - Bugs that only manifest across a multi-turn tool loop, or only when the client executes a tool, cannot be caught by a first-turn smoke, a synthetic fixture, or a status code. If you genuinely cannot run the real client, STOP and say so explicitly and mark the fix **UNVERIFIED** — never dress up unit-test or 200 evidence as a real-client pass.

One project for now (per RamDrive's pattern early on). If size or compile time pushes back, the natural seam is to split out `CopilotBridge.Core` containing `Translation/`, `Copilot/`, `Auth/`, `Configuration/`, `Models/`, `State/`, leaving `Cli/` as `Program.cs` + `Hosting/` + `Endpoints/` + `WebAssets/`. Subdomains live as folders inside the single project until then — extra `.csproj` files each pay an AOT cost.

## Working with the codebase

- **This project plans and tracks work with OpenSpec.** For *current* work — what's proposed, in progress, or pending implementation — read `openspec/changes/` (each change has `proposal.md` / `design.md` / `specs/` / `tasks.md`). Run `openspec list` to see active changes and `openspec status --change "<name>"` for remaining tasks; `/opsx:apply <name>` to implement, `/opsx:propose` to start a new change. **Do not record progress or status in this file** — `CLAUDE.md` / `AGENTS.md` are the project's stable constitution (how to build, the invariants, the conventions), not a status log. When a change introduces a durable architectural fact, fold *that fact* into the design docs under `docs/` once it's implemented — not the in-flight task state.
- **Language conventions.**
  - **Files in the repo** — `CLAUDE.md`, everything under `docs/`, code comments, commit messages, etc. — are always written in **English**, regardless of the user's chat language.
  - **Claude Code's chat replies to the user** match the user's language: if they write in Chinese, respond in Chinese; if English, respond in English. Don't mix unless the user does.
- The user is on Windows with PowerShell as the default shell. Default to PowerShell-shaped commands; the Bash tool is also available for POSIX scripts.
- This directory is a git repo (`origin` → GitHub `hooyao/copilot-bridge`). `references/` and `.claude/` are already gitignored — `references/` is a separate read-only checkout; never `git add` it. Also keep local-only scratch out of commits: `.mcp.json` (local MCP server config) and session-handoff `.txt` dumps.
- Build incrementally: `/v1/chat/completions` (mostly passthrough) end-to-end before `/v1/messages` (translation), and non-streaming before streaming. The reference's `src/routes/` layout reflects the same complexity gradient — follow it.
