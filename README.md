# copilot-bridge

[![CI](https://github.com/hooyao/copilot-bridge/actions/workflows/ci.yml/badge.svg)](https://github.com/hooyao/copilot-bridge/actions/workflows/ci.yml)
[![Release](https://github.com/hooyao/copilot-bridge/actions/workflows/release.yml/badge.svg?event=push)](https://github.com/hooyao/copilot-bridge/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/hooyao/copilot-bridge)](https://github.com/hooyao/copilot-bridge/releases/latest)

Use your **GitHub Copilot** subscription as the model backend for **Claude Code**
and **Codex** (Gemini CLI is on the roadmap). copilot-bridge is a small reverse
proxy that exposes Copilot's LLM API under a vendor-neutral URL per client, so
each CLI talks to the bridge as if it were talking to its native provider.

```
Claude Code (Anthropic shape) ──► /cc/v1/messages       ┐
Codex       (Responses shape) ──► /codex/responses       ├─► copilot-bridge ─► GitHub Copilot
Gemini CLI  (Gemini shape)    ──► /gemini/v1/...  (soon)  ┘
```

It ships as a single ~12 MB native executable with **no .NET runtime to install**,
for win-x64, win-arm64, linux-x64, and osx-arm64.

## Why use it

- **One Copilot subscription, two coding agents.** Point Claude Code at `/cc`
  and Codex at `/codex` — both run on your existing Copilot plan. Cost counts
  against Copilot, not an Anthropic/OpenAI bill.
- **The full current Claude model set.** opus-4.6 / opus-4.7 / **opus-4.8**,
  sonnet-4.5 / sonnet-4.6 / **sonnet-5**, haiku-4.5 — with **native 1M context**
  on opus-4.6/4.7/4.8 and sonnet-4.6/sonnet-5 (all but sonnet-4.5 / haiku-4.5).
  Codex runs on Copilot's gpt-5.x.
- **Works with Claude Code 4.8 out of the box.** Claude Code sends beta headers
  Copilot rejects (e.g. `advisor-tool-2026-03-01`, which 400s on every model).
  The bridge strips the ones Copilot refuses so your session doesn't error.
- **Per-model behavior is probed, not guessed.** Which reasoning-effort levels,
  thinking shapes, and context windows each Copilot model actually accepts is
  measured against the live API and baked into a profile. The bridge reshapes
  each request to what the target model accepts, and returns a clear error for
  an unknown model instead of silently forwarding a request that will fail.
- **Auto-repairs tool-call leaks.** Copilot-served models occasionally emit a
  tool call as literal `<invoke …>` text instead of a real tool call. The bridge
  detects this and makes the client retry the turn cleanly, so it doesn't get
  stuck (new in 0.2.2-beta).

## Install & run

1. **Download** the archive for your OS from the
   [Releases page](https://github.com/hooyao/copilot-bridge/releases) — `.zip`
   for Windows, `.tar.gz` for Linux/macOS, plus an unsigned `.pkg` installer for
   macOS. **Extract it, keeping `copilot-bridge(.exe)` and `appsettings.json`
   together** — the bridge loads its config from its own folder.

   > **macOS only:** the binary is unsigned, so the first run is blocked by
   > Gatekeeper. Clear the quarantine flag once:
   > `xattr -dr com.apple.quarantine ./copilot-bridge` (or the install directory
   > for the `.pkg`), then run normally.

2. **Start it — just double-click `copilot-bridge.exe`** (or run it from a
   terminal). It starts the server on port **8765**. On the **first run** it
   prints a **GitHub device-code URL and a code**:

   ```
   To authorize, open https://github.com/login/device and enter code: ABCD-1234
   ```

   Open that URL in your browser, enter the code, and approve. The bridge saves
   an **encrypted** token next to the executable, so every later start is silent
   — no login prompt. (On Windows, double-clicking opens a console window that
   shows the URL and the live log.)

3. Leave it running. Now point your CLI at it.

## Point Claude Code at the bridge

Add an `env` block to `.claude/settings.local.json` (or your global
`~/.claude/settings.json`):

```jsonc
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
    "ANTHROPIC_AUTH_TOKEN": "dummy"
  }
}
```

`ANTHROPIC_AUTH_TOKEN` is unused (the bridge authenticates to Copilot with your
GitHub token) but Claude Code requires *something* to be set. Pick any Claude
model in Claude Code as usual — the bridge maps it to the matching Copilot model.

## Point Codex at the bridge

Edit `~/.codex/config.toml` — set the default model + provider at the top and add
the provider block:

```toml
model = "gpt-5.5"
model_provider = "copilot-bridge"

[model_providers.copilot-bridge]
name = "copilot-bridge"
base_url = "http://localhost:8765/codex"
wire_api = "responses"
```

## Configuration (`appsettings.json`)

The file next to the executable. A few keys worth knowing:

- **`Server.Port`** — the port the bridge listens on (default `8765`). If you
  change it, update the `base_url` in your CLI config to match.
- **`Tracing.Enabled`** — per-request audit logging, **off by default**. Turn it
  on to dump every request/response as JSON under `request-traces/` when
  debugging — but note the files contain your full prompts, so turn it back off
  afterward.
- **`Pipeline:Detectors:ToolLeakGuard`** — the tool-call-leak auto-repair
  (`Enabled`, `Signal`). On by default; leave it unless you want to tune the
  retry signal.
- **`Routing.Locations`** — optional nginx-style rules to remap a model or tweak
  headers per request. For example, the shipped rule:

  ```jsonc
  {
    "When": { "Model": "gpt-5.5-1m" },
    "Use":  { "Model": "gpt-5.5" }
  }
  ```

  rewrites a request for `gpt-5.5-1m` to the real `gpt-5.5` (Codex uses the
  `-1m` alias to unlock the 1M window client-side; the bridge maps it back). See
  [`docs/routing.md`](docs/routing.md) for the full match/rewrite syntax.

## Limitations

The bridge forwards whatever Copilot's API accepts — a curated subset of the
native Anthropic surface. A few things differ from a paid Anthropic/OpenAI plan:

- **Claude Code's built-in WebSearch doesn't work.** It relies on Anthropic's
  server-side search, which Copilot exposes on no model; the bridge returns a
  friendly error. **Workaround:** use a search MCP server (via `--mcp-config` or
  `.mcp.json`) and disable the built-in WebSearch tool. Other MCP tools flow
  through transparently.
- **`max` / `xhigh` reasoning effort isn't universal.** Support is per-model and
  non-monotonic: opus-4.8 / opus-4.7 / sonnet-5 accept every tier
  (`low`–`max`, including `xhigh`); opus-4.6 / sonnet-4.6 accept `max` but reject
  `xhigh`; sonnet-4.5 / haiku-4.5 take no effort field. The bridge strips an
  effort the target rejects instead of letting it fail.
- **Resume drops the `[1m]` flag back to 200k.** Claude Code stores the 1M toggle
  in the model string (`opus[1m]`), which isn't persisted across `--resume`. The
  backend still serves the larger window, but Claude Code's own auto-compaction
  triggers at 200k until you re-select `opus[1m]`. See
  [`docs/context-window.md`](docs/context-window.md).
- **Cost counts against your Copilot subscription.** The bridge has no Anthropic
  key and never falls back to `api.anthropic.com`.
- **Token storage is weaker off Windows.** Your GitHub token is always encrypted
  at rest, but Windows uses OS-owned **DPAPI** while Linux/macOS use
  **AES-256-CBC + HMAC** with a key derived from machine id + username (no OS
  keystore — so it works headless and stays AOT-clean). It protects the token
  file from being copied off the host, but a local attacker running as you on the
  same host could re-derive the key. Full threat model in
  [`docs/token-storage.md`](docs/token-storage.md).

---

# Development

## Architecture

Every request runs through one typed pipeline whose intermediate representation
is the **Anthropic Messages API**. Each stage is a single-purpose transformation;
new clients and backends extend the pipeline instead of rewriting the core. The
full architectural contract (pipeline + client adapters + upstream strategies +
diagnostic tracer) is in
[`docs/pipeline-design.md`](docs/pipeline-design.md), and the protocol facts
driving each stage are in
[`docs/copilot-api-research.md`](docs/copilot-api-research.md).

The request pipeline for `Pipeline<MessagesRequest>`:

```
ModelRouter → AssistantThinkingFilter → SystemSanitize → MessagesSanitize
            → ToolsSanitize → HeadersOutbound
            → CopilotMessagesPassthroughStrategy → ResponseInspection (response side)
```

- **`ModelRouter`** normalizes the requested id, applies the first matching
  `Routing.Locations` rule, looks the result up in `ModelProfileCatalog`, then
  runs `ProfileAdjuster` to coerce the body to the target's wire contract
  (effort, thinking shape, mid-conversation `system` handling, beta strips). See
  [`docs/pipeline-design.md §7`](docs/pipeline-design.md).
- **`ResponseInspection`** runs an ordered set of response detectors in a single
  pass over the SSE stream: the `[DONE]` filter, the model-id rewrite (restores
  the client-requested id for downstream accounting), and the tool-leak guard.
  New detectors register into the same stage. See
  [`docs/pipeline-design.md §6`](docs/pipeline-design.md).

Codex requests are translated into the same Anthropic-shape IR via the T1–T4
translators and routed to Copilot's `/responses` backend — see
[`docs/codex-implementation-design.md`](docs/codex-implementation-design.md).

Per-model wire behavior is probed, not guessed: the matrix in
`tests/CopilotBridge.Playground/ModelProfileProbe.cs` feeds `ModelProfileCatalog`.
Unknown models surface a 400 + Anthropic-format error, never a silent passthrough.

## Build from source

Requires the **.NET 10 SDK** plus a C/C++ toolchain for the AOT linker on the OS
you're building for (Windows: Visual Studio C++ Build Tools; Linux: `clang` +
`zlib1g-dev`; macOS: Xcode Command Line Tools). Native AOT **cannot** cross-compile
across operating systems — you build for the OS you're on.

```pwsh
# JIT build + run (no native toolchain needed) — the fast dev loop
dotnet run --project src/CopilotBridge.Cli -- serve --port 18765

# Debug build of the whole solution
dotnet build CopilotBridge.slnx

# Single-file AOT publish (swap the RID: win-arm64 / linux-x64 / osx-arm64)
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
```

> **Windows AOT caveat:** a bare `dotnet publish` can fail the native link
> because ILC shells out to `vswhere.exe`, which isn't on `PATH` even in a VS
> developer prompt. Use **`.\build-aot.bat`** (it adds `vswhere` to `PATH`, runs
> `VsDevCmd.bat`, then publishes), or the PowerShell block documented in
> [`CLAUDE.md`](CLAUDE.md) / [`AGENTS.md`](AGENTS.md). CI images expose the
> toolchain directly, so the workflow uses a bare `dotnet publish`.

## Testing

```pwsh
# Unit tests — CI-safe, no live Copilot needed
dotnet test tests/CopilotBridge.UnitTests

# Everything except the live-Copilot integration harness
dotnet test --filter "Category!=Integration"

# Integration harness — hits live Copilot; run `auth login` first
dotnet test tests/CopilotBridge.Playground
```

Playground tests carry `[Trait("Category","Integration")]` so CI skips them.
See [`docs/routing.md`](docs/routing.md) for the routing config reference and
[`tests/harness/README.md`](tests/harness/README.md) for the end-to-end harness.

## CI & releases

CI runs the Debug build + unit tests + a Release AOT publish on `windows-latest`,
plus a cross-platform AOT gate (`ubuntu-latest`/linux-x64, `macos-14`/osx-arm64,
`windows-11-arm`/win-arm64) that publishes and smoke-tests each binary on every
push to `main`.

Pushing a **`release-X.Y.Z`** tag triggers the release workflow, which builds all
four RIDs on their own runners and publishes a single GitHub Release with every
archive (and the macOS `.pkg`) attached. Release notes are the delta since the
previous release. The version comes entirely from the tag — no file to bump.

## Diagnostics

Two log channels:

- **Runtime text log** (always on) — Serilog to console (stderr) and a
  per-startup file at `<exe-dir>/log/bridge-{YYYYMMDD-HHMMSS}.log`. One file per
  process start makes a single run trivially greppable. Levels are per-category
  in `appsettings.json`'s `Logging:LogLevel` (default `Debug` for
  `CopilotBridge.Cli`). Every log line for a request carries the request's trace
  id, so you can pair a request's start and end and jump from any line to its
  trace even when concurrent requests interleave: the `endpoint … enter`/`exit`
  boundary lines and each pipeline stage are prefixed with it in brackets
  (`[20260702-032206-0001]`, coloured on the console), and the `req#…` summary
  line renders the same id inline after `req#` (no bracket — it self-labels). It
  is the same id that names the request's trace JSON files. (The summary's
  trace-id field is named `ReqTrace`, not `TraceId`, on purpose: the framework's
  default activity tracking injects an ambient `Activity.TraceId` scope that would
  otherwise shadow a `{TraceId}` template hole and make `req#` print a 32-hex
  framework id instead.) Notable events name their subject: a tool-call-leak
  detection logs one `Warning` naming the leaked tool, block type, and the retry
  signal (never the leaked content).
- **Per-request audit trace** (opt-in, off by default) — set
  `"Tracing": { "Enabled": true }` to capture four JSON files per request under
  `request-traces/` (`<utc>-<seq>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`):
  inbound headers/body, upstream URL/headers/body, all SSE events (including the
  filtered `[DONE]`), and duration. Off by default because traces contain full
  prompts — turn it on to debug a cache-hit or protocol mismatch, then off again.

## Roadmap

| Milestone | Scope |
| --- | --- |
| ✅ M1 | Claude Code → Copilot Anthropic; identity adapters; full preprocessing pipeline |
| ✅ M2 | Cross-platform publish (win-x64, win-arm64, linux-x64, osx-arm64) |
| ✅ M3 | Codex (Responses shape) → `/codex/responses`; T1–T4 translators through the shared IR; per-model effort profile catalog; live `codex.exe` end-to-end |
| M4 | Gemini CLI client + IR↔Gemini translators |

## References

- [`docs/pipeline-design.md`](docs/pipeline-design.md) — pipeline architecture spec
- [`docs/routing.md`](docs/routing.md) — `Routing.Locations` config reference
- [`docs/copilot-api-research.md`](docs/copilot-api-research.md) — Copilot API protocol notes
- [`docs/codex-implementation-design.md`](docs/codex-implementation-design.md) — Codex `/responses` path
- [`docs/token-storage.md`](docs/token-storage.md) — token-at-rest threat model
- [`docs/design.md`](docs/design.md) — original design doc
