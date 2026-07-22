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
  Codex runs on Copilot's gpt-5.x, including the newest **gpt-5.6** models
  (`gpt-5.6-luna` / `gpt-5.6-sol` / `gpt-5.6-terra`).
- **Run Claude Code on a GPT model — including gpt-5.6-sol.** Point Claude Code at
  one of Copilot's reasoning GPTs instead of a Claude model: a one-line
  `Routing.Locations` rule routes `claude-opus-4.8` traffic to **`gpt-5.6-sol`**
  (Copilot's newest Codex model). The bridge translates the full Anthropic
  tool-use protocol — tool calls, tool results, streaming — to and from the
  Responses API, so a complete agentic session runs end to end. See the
  `Routing.Locations` example under [Configuration](#configuration-appsettingsjson).
- **Works with Claude Code 4.8 out of the box.** Claude Code sends beta headers
  Copilot rejects (e.g. `advisor-tool-2026-03-01`, which 400s on every model).
  The bridge strips the ones Copilot refuses so your session doesn't error.
- **Every model's real limits are respected.** Which reasoning-effort levels,
  thinking shapes, and context windows each Copilot model actually accepts differs
  from what its docs claim. The bridge reshapes each request to what the target
  model accepts. A newer *Claude* model with no exact profile yet is still
  forwarded under the closest known one (real id on the wire), so it works before
  the bridge is updated; an id it can't relate to any known model gets a clear
  error. (A brand-new *Codex/GPT* model is the one case that needs a one-line
  allowlist update first — until then it isn't routed to Copilot's Responses API.)
- **Auto-repairs tool-call and control-envelope leaks.** Copilot-served models
  occasionally emit a tool call — or one of Claude Code's internal control markers
  — as literal text instead of a real structured block. The bridge detects this
  and makes the client retry the turn cleanly, so it doesn't get stuck. An opt-in
  airtight mode suppresses a leak before any leaked byte reaches the client.
- **Stops degenerate runaways before they hang your client.** A Copilot model can
  get stuck generating — an unbounded stream of tiny fragments, or one token
  repeated tens of thousands of times up to `max_tokens` — which would otherwise
  stream at you for minutes and burn quota for nothing. The bridge detects this on
  both streaming and one-shot responses and forces a clean retry the moment output
  goes degenerate.
- **Bounds the wait on an unresponsive Copilot.** Two independent *inactivity*
  budgets — one for the first response byte, one for the gap between streamed
  events — cap how long the bridge hangs on a stalled backend. They're idle
  timers, not a total-duration cap, so a legitimately slow-but-progressing request
  (a near-full-1M prompt whose first byte takes minutes) is never cut off. A
  stall before headers returns a real `504`; a mid-stream stall on Claude Code
  drives the same clean retry as the guards above (Codex gets a terminal
  `response.failed`, which its client understands).

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
```

This merges the bridge's keys into your existing settings **without touching any
other setting** — the port comes from `appsettings.json` (`Server.Port`). The
command removes the legacy `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` override so
Claude Code can recover from a streaming fault with its `stream:false` fallback;
the bridge translates that response on cross-model routes. Add `--dry-run` to
preview the change, or `--port N` to override. `--dry-run` prints only the keys the command changes;
add `--show-content` to also print the full merged file (which includes your
preserved settings, so avoid it in shared logs). Run `copilot-bridge config
status` to see where each client currently points and whether it has drifted from
`appsettings.json`.

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

The last two keys give native-1M models (opus-4.6/4.7/4.8, sonnet-4.6, sonnet-5)
their full 1M context window — including after `--resume`. Claude Code 2.1.216
gates the 1M capability on the base URL being first-party; these assert that for
the bridge and keep its error-reporting telemetry off (see
`docs/context-window.md` §5). `ANTHROPIC_AUTH_TOKEN` is unused (the bridge
authenticates to Copilot with your GitHub token) but Claude Code requires
*something* to be set. Pick any Claude model in Claude Code as usual — the bridge
maps it to the matching Copilot model. The `config claude-code` command writes all
four of these keys for you.

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

The file next to the executable. A few keys worth knowing:

- **`Server.Port`** — the port the bridge listens on (default `8765`). If you
  change it, update the `base_url` in your CLI config to match.
- **`AutoUpdate`** — startup self-update, **on by default**. When you run
  `copilot-bridge` (or `copilot-bridge serve`), the bridge checks the project's
  public GitHub Releases **once, synchronously, before it binds the port**; if a
  newer version exists it prints the release notes and asks
  `Install this update now? [y/N]`. Only an interactive `y`/`yes` installs — a
  redirected/non-interactive stdin never installs, and a failed check (offline,
  rate-limited, timeout) just logs a warning and starts the current version.
  `EnableAutoUpdate` (default `true`) is the master switch; `AllowBetaUpdates`
  (default `false`) also considers GitHub prereleases. Maintenance commands
  (`auth`, `config`, `debug`, `--help`, `--version`) never check, and a local
  `*-dev` build never self-updates. See
  [`docs/auto-update.md`](docs/auto-update.md) for the full design (trust
  boundary, config migration, rollback).
- **`Tracing.Enabled`** — per-request audit logging, **off by default**. Turn it
  on to dump every request/response as JSON under `request-traces/` when
  debugging — but note the files contain your full prompts, so turn it back off
  afterward.
- **`Pipeline:Detectors:ResponseLeakGuard`** — the response-leak auto-repair
  (catches a leaked tool call or a leaked Claude Code control envelope;
  `Enabled`, `Signal`). On by default; leave it unless you want to tune the
  retry signal. Individual leak signatures can be turned off under `Signatures`
  (`Invoke`, `TaskNotification`, `TeammateMessage`, `Channel`,
  `CrossSessionMessage`, `Tick`, `SystemReminder` — all on by default) to clear a
  false positive, e.g. when you're discussing this markup with the model and a
  sample reply gets caught. The retry error and the log both name the exact switch
  to flip. Set `BufferScannableBlocks` to `true` for airtight suppression — each
  text/thinking block is withheld until scanned and relayed only if clean, so a
  leak never reaches the client (default `false` relays until detection).
  A **restart** is required after changing any of these.
- **`Pipeline:Detectors:RunawayGuard`** — the degenerate-generation
  circuit-breaker. On by default; aborts a runaway on any of four signals and
  forces a retryable `overloaded_error`. Tune the thresholds only if a legitimate
  output trips one: `MaxDeltaBytes` (cumulative delta payload, default 12 MiB),
  `MaxDeltaCount` (per-block delta events, default 20000), `RepetitionWindow` +
  `RepetitionMinUniqueRatio` (sliding-window unique-token ratio, default 500 /
  0.05), and `RepetitionMaxConsecutiveRepeat` (same token in a row, default 50).
  A repetitive-but-legitimate output is fixed by **raising** the offending
  threshold, not by disabling the guard. A **restart** is required after changing a
  value.
- **`Pipeline:UpstreamTimeout`** — the two inactivity budgets on the upstream
  forward paths. `FirstByteTimeoutSeconds` (default 240) bounds the wait for
  response headers per send attempt; `StreamIdleTimeoutSeconds` (default 60)
  bounds the gap between streamed events (sits just below Claude Code's own 90 s
  watchdog so the bridge acts first). Each is an *idle* timer, not a total cap, and
  `<= 0` disables that phase. `StreamIdleAction` (`Retry` / `Truncate`) and
  `StreamIdleSignal` (`OverloadedError` / `ApiError`) govern the mid-stream
  surfacing. A **restart** is required after changing a value.
- **`Pipeline:Detectors:ToolInputValidation`** — validates real `tool_use`
  input JSON against the declared tool schema after the tool block closes and
  records `tool_input_invalid=true` on the summary line. **Observe-only by
  default** (it does *not* abort): Claude Code already recovers from an invalid
  tool call natively — it parses the input, falls back to an empty object on
  malformed JSON, and on a schema failure re-prompts the model with an error
  tool_result. Aborting was found to cut off that self-heal (e.g. an
  `AskUserQuestion` missing its required `question` field surfaced "Server error
  mid-response" instead of a clean retry). Set `MalformedJsonAction` /
  `SchemaViolationAction` to `AbortOverloaded` (or `AbortApiError`) only for a
  backend/tool where CC does *not* self-heal; `PreserveStream` then governs
  mid-stream vs real-HTTP-status delivery. A **restart** is required after
  changing it.
- **`Routing.Locations`** — optional nginx-style rules to remap a model or tweak
  headers per request. Ships **empty** (`"Locations": []`) — no rewrites by
  default. `appsettings.json` also carries a disabled example under
  `_Locations_disabled` (a key the config binder ignores) that you can enable by
  renaming it to `Locations`:

  ```jsonc
  {
    "When": { "Model": "claude-opus-4.8" },
    "Use":  { "Model": "gpt-5.6-sol", "EffortMap": { "max": "xhigh" } }
  }
  ```

  which routes Claude Code's `claude-opus-4.8` traffic to Copilot's newest Codex
  model, **`gpt-5.6-sol`**. The `EffortMap` here is a deliberate down-tier: unlike
  gpt-5.5, gpt-5.6-sol *does* accept `max`, so without the map Claude Code's `max`
  effort would pass through unchanged — the map caps it at `xhigh` instead (drop
  the `EffortMap` to send `max` through). See [`docs/routing.md`](docs/routing.md)
  for the full match/rewrite syntax.

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
