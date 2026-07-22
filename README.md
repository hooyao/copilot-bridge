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

- **One Copilot subscription, two agents.** Point Claude Code at `/cc` and Codex
  at `/codex`; both bill against your Copilot plan, not an Anthropic/OpenAI account.
- **The full Claude line-up, with native 1M context.** opus-4.6/4.7/**4.8**,
  sonnet-4.5/4.6/**5**, haiku-4.5 — 1M on everything except sonnet-4.5 and
  haiku-4.5. Codex runs on Copilot's gpt-5.x, up to the newest **gpt-5.6**
  (`gpt-5.6-luna` / `gpt-5.6-sol` / `gpt-5.6-terra`).
- **Run Claude Code on a GPT model.** One `Routing.Locations` rule points
  `claude-opus-4.8` at `gpt-5.6-sol`; the bridge translates the full Anthropic
  tool-use protocol to and from the Responses API, so an agentic session runs end
  to end. See [Configuration](#configuration-appsettingsjson).
- **Handles the wire-shape mismatches for you.** It strips beta headers Copilot
  rejects (e.g. `advisor-tool-2026-03-01`) and reshapes each request to the
  reasoning-effort, thinking, and context limits the *target* model actually
  accepts — which often differ from its docs. A new Claude model with no profile
  yet forwards under the closest known one if it's similar enough; a too-unfamiliar
  id gets a clear 400 instead.
- **Keeps a flaky backend from hanging your client.** The bridge auto-repairs
  leaked tool calls / control markers, breaks degenerate runaways (endless or
  repeated output), and caps the wait on a stalled Copilot with inactivity
  timeouts — surfacing each as a clean retry, a `504`, or a terminal error the
  client understands, rather than a hang. Tunable under `Pipeline` (below).

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

**One step — let the bridge write it for you:**

```pwsh
copilot-bridge config claude-code --scope global   # ~/.claude/settings.json
copilot-bridge config claude-code --scope repo     # ./.claude/settings.local.json (this repo only)

copilot-bridge config claude-code --dry-run        # preview only the keys it would change
copilot-bridge config claude-code --port 9000      # override the port from appsettings.json
copilot-bridge config status                       # show where each client points + any drift
```

It merges the bridge's keys into your existing settings, leaving your unrelated
settings intact; the port comes from `appsettings.json` (`Server.Port`) unless you
pass `--port`. (It does clear one legacy key — the old
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` override — so Claude Code can use its
`stream:false` recovery on a mid-stream fault, which the bridge translates on
cross-model routes.) Add `--show-content` to a `--dry-run` to print the full
merged file — it includes your other settings, so avoid it in shared logs.

**Or do it by hand** — add an `env` block to `.claude/settings.local.json` (or
your global `~/.claude/settings.json`). Claude Code reads this file as **strict
JSON**, so it must not contain comments:

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
    "ANTHROPIC_AUTH_TOKEN": "dummy",
    "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1",
    "DISABLE_ERROR_REPORTING": "1"
  }
}
```

- `ANTHROPIC_AUTH_TOKEN` — unused (the bridge authenticates with your GitHub
  token), but Claude Code requires *something* set.
- `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` + `DISABLE_ERROR_REPORTING` — unlock
  the full 1M window for native-1M models (opus-4.6/4.7/4.8, sonnet-4.6/5),
  including after `--resume`, and keep telemetry off. See
  `docs/context-window.md` §5.

Then pick any Claude model in Claude Code as usual — the bridge maps it to the
matching Copilot model.

## Point Codex at the bridge

**One step:**

```pwsh
copilot-bridge config codex
```

This edits `$CODEX_HOME/config.toml` (default `~/.codex/config.toml`) in place,
preserving every unrelated table, comment, and literal — it only repoints
`model_provider` and writes the `[model_providers.copilot-bridge]` block, leaving
any existing provider block intact so switching back is a one-line change. Codex
honors a single global config, so there is no `--scope` here. `--dry-run` and
`--port` work the same as above.

**Or do it by hand** — edit `~/.codex/config.toml`, set the default model +
provider at the top and add the provider block:

```toml
model = "gpt-5.5"
model_provider = "copilot-bridge"

[model_providers.copilot-bridge]
name = "copilot-bridge"
base_url = "http://localhost:8765/codex"
wire_api = "responses"
```

## Configuration (`appsettings.json`)

The file next to the executable. Everything below has a sensible default — you
only touch it to tune. Each detector row is toggled by its own `Enabled` flag
(default `true`); set `Enabled: false` to turn that detector off entirely.
**Changes take effect on restart.**

| Key | Default | What it does |
| --- | --- | --- |
| **`Server.Port`** | `8765` | Listen port. Change it and update `base_url` in your CLI config to match. |
| **`AutoUpdate.EnableAutoUpdate`** | `true` | Check GitHub Releases once, synchronously, before binding the port; prompts `Install this update now? [y/N]` and installs only on an interactive `y`. Offline/non-interactive just logs and starts current. `AllowBetaUpdates` (`false`) also considers prereleases. Maintenance commands and `*-dev` builds never check. → [`docs/auto-update.md`](docs/auto-update.md) |
| **`Tracing.Enabled`** | `false` | Dump every request/response as JSON under `request-traces/`. Contains full prompts — turn back off after debugging. |
| **`Pipeline:Detectors:ResponseLeakGuard`** | on | Auto-repairs a leaked tool call / Claude Code control envelope by forcing a clean retry. Turn off individual `Signatures` (`Invoke`, `TaskNotification`, `TeammateMessage`, `Channel`, `CrossSessionMessage`, `Tick`, `SystemReminder`) to clear a false positive — the retry error names the exact switch. `Signal` (`OverloadedError`/`ApiError`) picks the retry error surface. `BufferScannableBlocks: true` withholds each `text`/`thinking` block until scanned so a leak in one never reaches the client (`tool_use` blocks still stream live; default relays until detection). |
| **`Pipeline:Detectors:RunawayGuard`** | on | Circuit-breaker for degenerate output; forces a retryable `overloaded_error`. Thresholds: `MaxDeltaBytes` (12 MiB), `MaxDeltaCount` (20000), `RepetitionWindow`/`RepetitionMinUniqueRatio` (500 / 0.05), `RepetitionMaxConsecutiveRepeat` (50). Fix a false trip by **raising** the threshold, not disabling. |
| **`Pipeline:UpstreamTimeout`** | on | Two *idle* timers (not total caps; `<= 0` disables): `FirstByteTimeoutSeconds` (240) for response headers, `StreamIdleTimeoutSeconds` (60) for the gap between streamed events. `StreamIdleAction` (`Retry`/`Truncate`) and `StreamIdleSignal` (`OverloadedError`/`ApiError`) govern mid-stream surfacing. |
| **`Pipeline:Detectors:ToolInputValidation`** | observe-only | Validates `tool_use` input against the tool schema and flags `tool_input_invalid=true`, but does **not** abort — Claude Code self-heals. Set `MalformedJsonAction` / `SchemaViolationAction` to `AbortOverloaded`/`AbortApiError` only for a backend that doesn't; `PreserveStream` then picks delta-before-error (`true`) vs buffer-for-a-real-HTTP-error (`false`). |
| **`Routing.Locations`** | `[]` | nginx-style per-request model/header rewrites. See below. |

**`Routing.Locations`** ships empty. `appsettings.json` carries a disabled example
under `_Locations_disabled` (a key the binder ignores). To enable it, rename
`_Locations_disabled` to `Locations` **and** rename the existing active
`"Locations": []` to something else (e.g. `_Locations_off`) — exactly one
`Locations` key may be active, or the config provider rejects the file:

```jsonc
{
  "When": { "Model": "claude-opus-4.8" },
  "Use":  { "Model": "gpt-5.6-sol", "EffortMap": { "max": "xhigh" } }
}
```

This routes Claude Code's `claude-opus-4.8` to Copilot's `gpt-5.6-sol`. The
`EffortMap` down-tiers `max` → `xhigh` (gpt-5.6-sol accepts `max`, so drop the map
to pass it through). Full match/rewrite syntax in
[`docs/routing.md`](docs/routing.md).

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
  `xhigh`; sonnet-4.5 / haiku-4.5 take no effort field. On the Codex side it's
  also per-model: most gpt-5.x models accept up to `xhigh` and the **gpt-5.6**
  models (`luna`/`sol`/`terra`) are the first to also accept `max`, while smaller
  ones like `gpt-5-mini` top out at `high` (no `xhigh`). The bridge strips (or
  clamps) an effort the target rejects instead of letting it fail.
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
  pass over the response (streaming SSE *and* one-shot `application/json`): the
  `[DONE]` filter, the model-id rewrite (restores the client-requested id for
  downstream accounting), the response-leak guard, the runaway/degeneracy guard,
  and observe-only tool-input validation. New detectors register into the same
  stage. See [`docs/pipeline-design.md §6`](docs/pipeline-design.md).

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
  `CopilotBridge.Cli`). Each request's log lines carry a trace id in brackets
  (`[20260702-032206-0001]`, the same id that names the request's trace JSON
  files), so you can follow one request end-to-end and jump to its trace. Notable
  events name their subject: a leak detection logs one `Warning` naming the
  leaked signature and subject — a tool name or a control-envelope subject such as
  `task-notification` — plus the block type, the retry signal, and the exact
  config key to disable that signature (never the leaked content).
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
