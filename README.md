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

It ships as a single ~14 MB native executable with **no .NET runtime to install**,
for win-x64, win-arm64, linux-x64, and osx-arm64.

## Why use it

- **One Copilot subscription, two agents.** Point Claude Code at `/cc` and Codex
  at `/codex`; both bill against your Copilot plan, not an Anthropic/OpenAI account.
- **The full Claude line-up, with native 1M context.** opus-4.6/4.7/4.8/**5**,
  sonnet-4.6/**5**, haiku-4.5 — 1M on everything except haiku-4.5. Codex runs on
  Copilot's gpt-5.x, up to the newest **gpt-5.6**
  (`gpt-5.6-luna` / `gpt-5.6-sol` / `gpt-5.6-terra`), with live model-catalog
  discovery instead of Codex's older bundled context ceiling.
- **Run Claude Code on a GPT model.** One `Routing.Locations` rule points
  `claude-opus-5` at `gpt-5.6-sol`; the bridge translates the full Anthropic
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

   Open that URL in your browser, enter the code, and approve. By default, fresh
   logins use the official GitHub Copilot Plugin App's public Device Flow **inside
   the bridge**—no `gh` executable or client secret is required. The resulting
   version-3 credential is exchanged through `/copilot_internal/v2/token`, matching
   the official Copilot client.

   To isolate logins under another public OAuth App, use the prominent top-level
   section in `appsettings.json`:

   ```jsonc
   "Authentication": {
     "UseCustomAppId": true,
     "CustomAppId": "Ov23liSD97ZYGfIEHAZE"
   }
   ```

   The stock switch is **false** and the shown custom ID is prefilled. A custom App
   must have GitHub Device Flow enabled; Marketplace publication is not required.
   The client ID is public, not a client secret. After changing it, restart and run
   `copilot-bridge auth login`: the new version-4 credential goes directly to
   `https://api.githubcopilot.com` and never enters the internal token-exchange
   endpoint. Its CAPI requests use GitHub Copilot SDK's `copilot-developer-cli`
   integration identity; older credential versions retain `vscode-chat`. The setting
   controls only the next login; it never silently rewrites an
   existing encrypted credential.

   The bridge saves one encrypted, versioned `github_credentials.dat` beside the
   executable. On upgrade it migrates the richer `github_credentials.v2.dat` or,
   when that cannot be read, `github_token.dat`; it verifies the new file before
   deleting both old files. A migrated version-1 Copilot Plugin credential keeps
   working and refreshing without login until GitHub genuinely rejects it. The next
   explicit `auth login` replaces it with the provider selected above: version 3 for
   the default official App, or refreshable direct version 4 for a custom App.
   Existing version-2 `gho_` direct credentials remain supported.
   (On Windows,
   double-clicking opens a console window that shows the URL and live log.)

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

The command is connection-only. It updates `ANTHROPIC_BASE_URL`, fills the
required non-secret `ANTHROPIC_AUTH_TOKEN` placeholder only when that key is
absent, and preserves every timeout, retry, watchdog, 1M/first-party, telemetry,
and fallback value. The port comes from `appsettings.json` (`Server.Port`) unless
you pass `--port`. Add `--show-content` to a `--dry-run` to print the full merged
file — it includes your other settings, so avoid it in shared logs.

**Or do it by hand** — add an `env` block to `.claude/settings.local.json` (or
your global `~/.claude/settings.json`). Claude Code reads this file as **strict
JSON**, so it must not contain comments:

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
    "ANTHROPIC_AUTH_TOKEN": "dummy"
  }
}
```

- `ANTHROPIC_AUTH_TOKEN` — unused (the bridge authenticates with your GitHub
  token), but Claude Code requires *something* set.
- Optional Claude Code behavior keys such as
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS`, `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS`, and
  `API_TIMEOUT_MS` remain your settings. The bridge reports them beside its own
  independent budgets but never derives or writes them. Claude Code reads `env`
  at process start, so restart it after editing those values. See
  [Long-thinking timeouts](#long-thinking-timeouts).
- `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` and `DISABLE_ERROR_REPORTING` are
  likewise user-owned. See [`docs/context-window.md`](docs/context-window.md)
  before opting into their context-window and telemetry effects.

Then pick any Claude model in Claude Code as usual — the bridge maps it to the
matching Copilot model.

## Point Codex at the bridge

**One step:**

```pwsh
copilot-bridge config codex
```

**Upgrading from an older bridge?** Run that command again, then fully restart
Codex. The current config adds command-backed provider auth, which is the signal
Codex uses to fetch `GET /codex/models?client_version=...`; an already-running
Codex process or a legacy provider block keeps the old bundled catalog.

This edits `$CODEX_HOME/config.toml` (default `~/.codex/config.toml`) in place,
preserving every unrelated table, comment, and literal — it only repoints
`model_provider` and surgically upserts the bridge provider's `name`, `base_url`,
`wire_api`, and nested command-auth fields. Existing provider timeout, retry,
WebSocket, query, and header fields remain untouched, as do rival providers and
explicit context/auto-compact overrides.
The auth command emits a stable public sentinel only; your GitHub/Copilot token
stays inside the bridge's `AuthService` and is never written to Codex config.
If an existing provider expresses `auth` as an inline table or dotted keys, the
command refuses to append a conflicting explicit auth table and names the
`[model_providers.copilot-bridge.auth]` shape to convert to; it never writes an
invalid TOML merge.
This bridge command intentionally targets the global file, so it has no `--scope`;
Codex project/profile/CLI layers can still affect a particular future process.
`--dry-run` and `--port` work the same as above.

For the current 1M-class gpt models (`gpt-5.4`, `gpt-5.5`, and the three
`gpt-5.6` codenames), Copilot reports **1,050,000 total context**, **922,000
maximum prompt**, and 128,000 maximum output. The bridge advertises the truthful
1.05M total window but tells Codex to auto-compact at **892,000 tokens** (85% of
total context, rounded down to a whole thousand). Think of it as a safe roughly
890k working threshold — not 1.05M tokens of prompt
capacity. Explicit user context overrides are preserved and may intentionally
select a smaller window.

**Or do it by hand** — edit `~/.codex/config.toml`, set the default model +
provider at the top and add the provider block:

```toml
model = "gpt-5.5"
model_provider = "copilot-bridge"

[model_providers.copilot-bridge]
name = "copilot-bridge"
base_url = "http://localhost:8765/codex"
wire_api = "responses"

[model_providers.copilot-bridge.auth]
command = "/absolute/path/to/copilot-bridge"
args = [ "auth", "provider-token" ]
timeout_ms = 5000
refresh_interval_ms = 0
```

The command path differs for native and `dotnet <dll>` development installs, so
`copilot-bridge config codex` is strongly preferred over hand-editing.

## Configuration (`appsettings.json`)

The file next to the executable. Everything below has a sensible default — you
only touch it to tune. Each detector row is toggled by its own `Enabled` flag
(default `true`); set `Enabled: false` to turn that detector off entirely.
**Changes take effect on restart.**

| Key | Default | What it does |
| --- | --- | --- |
| **`Server.Port`** | `8765` | Listen port. Change it and update `base_url` in your CLI config to match. |
| **`Server.MaxRequestBodySizeBytes`** | `104857600` (100 MiB) | Maximum inbound request body for every bridge endpoint. The finite default gives image-heavy Codex conversations room beyond Kestrel's 30 MB default. Raising it increases worst-case per-request memory use and does not compact client history; restart after changing it. |
| **`Codex.ModelCatalog.Enabled`** | `true` | Map Codex-native `GET /codex/models` discovery so command-auth Codex can learn reviewed live Copilot context limits. Set `false` to remove only the metadata route and fall back to Codex's bundled catalog; `/codex/responses` inference remains available. |
| **`Codex.ModelCatalog.LiveOverlayFailureCooldownSeconds`** | `300` | Exact process-local delay after a failed live Copilot `/models` overlay refresh. During it, catalog polls serve the reviewed baseline or stale last-known-good overlay without another upstream call or warning; at expiry one shared retry is allowed. Range 1–3600. This metadata control never delays or disables `/codex/responses` inference, and restart permits an immediate retry. |
| **`Codex.ModelCatalog.SourceTtlHours`** | `24` | How often an exact-version official Codex catalog is checked for source changes. Microsoft `HybridCache` supplies the process-memory level and per-version request coalescing; the validated file remains available stale when GitHub is unavailable. Range 1–168. |
| **`Codex.ModelCatalog.CacheDirectory`** | OS per-user cache | Optional absolute persistent-cache override. Records are keyed by the complete stable/prerelease client version, bounded by `RetentionDays` (90) and `MaxRetainedVersions` (32). |
| **`Codex.ModelCatalog.SourceTimeoutSeconds` / `MaxSourceBytes`** | `10` / `4194304` | Bound anonymous exact-tag GitHub raw downloads. A cold failure affects only `/codex/models`; inference remains available and Codex keeps its bundled catalog. |
| **`AutoUpdate.EnableAutoUpdate`** | `true` | Check GitHub Releases once, synchronously, before binding the port; prompts `Install this update now? [y/N]` and installs only on an interactive `y`. Offline/non-interactive just logs and starts current. Set `AllowBetaUpdates` to `true` (default `false`) to also consider prereleases. Maintenance commands and `*-dev` builds never check. → [`docs/auto-update.md`](docs/auto-update.md) |
| **`Codex.ModelCatalog.BuiltinFallbackEnabled`** | `true` | OpenAI ships Codex clients before tagging the matching release, so a brand-new client's exact tag can legitimately 404 (desktop reported `0.147.0` while `rust-v0.147.0` did not exist). When on, that **confirmed absence** is answered from a catalog snapshot bundled into the bridge at build time — still uplifted with live Copilot limits — instead of a metadata error. Only a definitive 404 qualifies: timeouts, throttling, and server errors still fail closed. The snapshot is never cached and never outranks a validated entry, so a tag published later always wins. Set `false` for strict exact-version-only behavior. |
| **`Codex.ModelCatalog.AbsenceTtlHours`** | `6` | How long one confirmed 404 is trusted before re-checking whether that tag has been published. Must be ≥ 1; it is clamped to `SourceTtlHours` when that is lower, so an existing shorter source TTL keeps working untouched. Lower adopts a newly published tag sooner at the cost of more requests; higher keeps a client on the bundled snapshot longer after its real catalog goes live. |
| **`Tracing.Enabled`** | `false` | Dump every request/response as JSON under `request-traces/`. Contains full prompts — turn back off after debugging. |
| **`Pipeline:Detectors:ResponseLeakGuard`** | on | Auto-repairs a leaked tool call / Claude Code control envelope by forcing a clean retry. Turn off individual `Signatures` (`Invoke`, `TaskNotification`, `TeammateMessage`, `Channel`, `CrossSessionMessage`, `Tick`, `SystemReminder`) to clear a false positive — the retry error names the exact switch. `Signal` (`OverloadedError`/`ApiError`) picks the retry error surface. `BufferScannableBlocks: true` withholds each `text`/`thinking` block until scanned so a leak in one never reaches the client (`tool_use` blocks still stream live; default relays until detection). |
| **`Pipeline:Detectors:RunawayGuard`** | on | Circuit-breaker for degenerate output; forces a retryable `overloaded_error`. Thresholds: `MaxDeltaBytes` (12 MiB), `MaxDeltaCount` (20000), `RepetitionWindow`/`RepetitionMinUniqueRatio` (500 / 0.05), `RepetitionMaxConsecutiveRepeat` (50). Fix a false trip by **raising** the threshold, not disabling. |
| **`Pipeline:UpstreamTimeout`** | on | Exact independent bridge values: `FirstByteTimeoutSeconds` (240) bounds response headers per send; `StreamIdleTimeoutSeconds` (240) bounds each parsed upstream SSE event gap; `KeepAliveIntervalSeconds` (15) schedules downstream pings after the first upstream event. `<= 0` disables that timer. No margin, clamp, fallback, coarse HTTP cap, or client-config rewrite is applied. `StreamIdleAction` (`Retry`/`Truncate`) and `StreamIdleSignal` (`OverloadedError`/`ApiError`) govern mid-stream surfacing. See [Long-thinking timeouts](#long-thinking-timeouts). |
| **`Pipeline:Detectors:ToolInputValidation`** | observe-only | Validates `tool_use` input against the tool schema and flags `tool_input_invalid=true`, but does **not** abort — Claude Code self-heals. Set `MalformedJsonAction` / `SchemaViolationAction` to `AbortOverloaded`/`AbortApiError` only for a backend that doesn't; `PreserveStream` then picks delta-before-error (`true`) vs buffer-for-a-real-HTTP-error (`false`). |
| **`Routing.Locations`** | `[]` | nginx-style per-request model/header rewrites. See below. |

Catalog resolution is visible in the always-on bridge log as one structured line
containing the exact version, `cache=memory|disk|source-200|source-304|stale`,
freshness, source/validation outcome, elapsed time, and abbreviated digest/ETag.
Catalog bodies and GitHub/Copilot authorization values are never logged.

**`Routing.Locations`** ships empty. `appsettings.json` carries a disabled example
under `_Locations_disabled` (a key the binder ignores). To enable it, rename
`_Locations_disabled` to `Locations` **and** rename the existing active
`"Locations": []` to something else (e.g. `_Locations_off`) — exactly one
`Locations` key may be active, or the config provider rejects the file:

```jsonc
{
  "When": { "Model": "claude-opus-5" },
  "Use":  { "Model": "gpt-5.6-sol", "EffortMap": { "max": "xhigh" } }
}
```

This routes Claude Code's `claude-opus-5` to Copilot's `gpt-5.6-sol`. The
`EffortMap` down-tiers `max` → `xhigh` (gpt-5.6-sol accepts `max`, so drop the map
to pass it through). Full match/rewrite syntax in
[`docs/routing.md`](docs/routing.md).

## Long-thinking timeouts

**Symptom:** a deep-thinking turn (`opus-5` at `effort=max`, a big analysis
prompt) dies part-way with `API Error: Stream idle timeout - no chunks received`,
or the bridge logs a `504` a few minutes later.

**Cause:** Copilot sends no keepalive while a model is thinking. Extended
reasoning can put *nothing* on the upstream wire for minutes. The bridge and each
client have independent clocks over different portions of that wait.

At startup, place these values side by side:

| Layer | Native setting | Scope |
|---|---|---|
| Bridge | `FirstByteTimeoutSeconds` | Upstream response headers per send attempt |
| Bridge | `StreamIdleTimeoutSeconds` | Each complete parsed upstream SSE event gap |
| Bridge | `KeepAliveIntervalSeconds` | Downstream ping interval after the first upstream event; not a timeout |
| Claude Code | `CLAUDE_STREAM_IDLE_TIMEOUT_MS` | Parsed SSE event idle |
| Claude Code | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS` | Byte-stream idle |
| Claude Code | `API_TIMEOUT_MS` | One normal or after-stream-error request attempt |
| Codex | provider `stream_idle_timeout_ms` | Parsed SSE event idle |
| Codex | provider request/stream retry keys | Additional retry counts, not durations |

The bridge uses every positive appsettings value exactly and treats `<= 0` as
disabled. It adds no hidden headroom and never copies a bridge number into either
client. Edit the native client settings yourself, then restart that client when
its configuration model requires it. Running `config claude-code` or
`config codex` later preserves those behavior values.

Keepalive helps only after the first genuine upstream event and only while a
complete ping reaches the downstream socket. It does not reset the bridge's
upstream idle deadline, and whole-response buffering prevents it from protecting
the client mid-turn. The first-event gap therefore remains an unprotected
comparison; equal client/bridge deadlines are a race.

The header wait, first event, later event gaps, buffered body, request attempts,
sampling attempts, and whole turn are different scopes. A retry starts a new
attempt, so there is deliberately no fabricated whole-turn timeout. Exact client
defaults, source links, phase composition, and diagnostic guidance:
[`docs/timeout-chain.md`](docs/timeout-chain.md).

## Limitations

The bridge forwards whatever Copilot's API accepts — a curated subset of the
native Anthropic surface. A few things differ from a paid Anthropic/OpenAI plan:

- **Claude Code's built-in WebSearch doesn't work.** It relies on Anthropic's
  server-side search, which Copilot exposes on no model; the bridge returns a
  friendly error. **Workaround:** use a search MCP server (via `--mcp-config` or
  `.mcp.json`) and disable the built-in WebSearch tool. Other MCP tools flow
  through transparently.
- **`max` / `xhigh` reasoning effort isn't universal.** Support is per-model and
  non-monotonic: opus-5 / opus-4.8 / opus-4.7 / sonnet-5 accept every tier
  (`low`–`max`, including `xhigh`); opus-4.6 / sonnet-4.6 accept `max` but reject
  `xhigh`; haiku-4.5 takes no effort field. opus-5 adds a cross-field rule: with
  `thinking` disabled it rejects `xhigh`/`max`, so the bridge clamps those to
  `high` on that path only. On the Codex side it's
  also per-model: most gpt-5.x models accept up to `xhigh` and the **gpt-5.6**
  models (`luna`/`sol`/`terra`) are the first to also accept `max`, while smaller
  ones like `gpt-5-mini` top out at `high` (no `xhigh`). The bridge strips (or
  clamps) an effort the target rejects instead of letting it fail.
- **Codex 1.05M is total context, not prompt capacity.** The current backend
  maximum prompt is 922k and the bridge's safe auto-compact threshold is 898k.
  Official Codex catalogs are resolved from the exact complete client version
  (corroborating Codex's three-part query with its complete prerelease User-Agent)
  through Microsoft `HybridCache` memory, persistent disk, then the matching
  `openai/codex` release tag.
  The 24-hour TTL only controls when the source is rechecked. If GitHub is
  temporarily unavailable but a validated exact-version disk record exists, the
  bridge serves it stale. On a cold miss, only `/codex/models` fails and Codex
  keeps its own bundled catalog; `/codex/responses` inference remains available.
- **Resume drops the `[1m]` flag back to 200k.** Claude Code stores the 1M toggle
  in the model string (`opus[1m]`), which isn't persisted across `--resume`. The
  backend still serves the larger window, but Claude Code's own auto-compaction
  triggers at 200k until you re-select `opus[1m]`. See
  [`docs/context-window.md`](docs/context-window.md).
- **Startup can observe only global client files.** It cannot know the repo/env
  values of a future Claude process or the project/profile/CLI context of a future
  Codex process. The report labels that boundary and never rewrites a timeout.
  Claude Code reads `env` at process start, so a running session keeps its prior
  user-selected values until restarted. See [Long-thinking
  timeouts](#long-thinking-timeouts).
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

### Authentication troubleshooting

Run these in order while the failure is present:

```pwsh
copilot-bridge auth status
copilot-bridge auth whoami
copilot-bridge auth copilot-status
copilot-bridge debug list-models --all
```

- `auth status` reports the single authoritative encrypted file, credential version,
  direct/exchanged mode, refreshability, generation, and deadlines—never token bytes.
- `auth whoami` validates (and when possible refreshes) the stored GitHub OAuth
  credential.
- `auth copilot-status` prints direct/exchanged mode, known deadlines, CAPI integration ID, and the API
  URL. Copilot Plugin credentials are exchanged for a short-lived bearer; an
  existing version-2 GitHub CLI OAuth credential and custom version-4 OAuth
  credential are direct CAPI bearers.
- `debug list-models --all` proves the resulting Copilot lease can reach CAPI and
  shows which models the current account/policy actually exposes.

Version 1 means a migrated Copilot Plugin credential and retains its full refresh
state. Version 2 means a GitHub CLI OAuth direct credential and has no separately
minted bearer deadline. Version 3 means a newly issued Copilot Plugin credential
with an explicit `oauth_client_id`. Version 4 means a configured custom OAuth App
credential used directly at CAPI; when GitHub token expiration is enabled it preserves
and rotates the eight-hour access token and refresh token using its recorded App ID.
A non-refreshable version 1 or 3 with unknown access expiry remains
valid until GitHub actually rejects it; its short-lived Copilot bearer still refreshes
in memory.

A first CAPI 403 is not treated as definitive account policy. The bridge rejects
only the bearer/endpoint generation used, obtains an already-newer or fresh lease,
and replays the same request once. Only a second 403 is classified as terminal
policy/entitlement; mixed 401/403 sequences still get one replay total. This is why
restarting could repair the reported `forbidden` incident: restart discarded the
stale process-local bearer. A catalog warning saying capacity is degraded refers
to `/codex/models` metadata only; the endpoint serves a safe baseline/stale overlay
during its configured cooldown, and inference remains independent.

If `whoami` and the token exchange/direct CAPI both report GitHub `401 Bad credentials`, the
failure occurs before model inference and therefore is not specific to gpt-5.6
or any request body. A refreshable persisted OAuth credential is rotated once automatically;
if its refresh token is missing, expired, or rejected, GitHub requires a new
interactive authorization. Check the account security log for
`oauth_access.destroy`; `explanation=max_for_app` means another login exceeded
GitHub's ten-token user/application/scope limit and evicted this credential. Then run:

```pwsh
copilot-bridge auth login
```

If GitHub auth succeeds but a gpt-5.6 id is absent from `/models`, check the
Copilot plan and organization model policy instead. Sol requires Pro+/Max or an
enabled Business/Enterprise policy; Terra and Luna require a paid Copilot plan,
and Business/Enterprise administrators must explicitly enable the new-model
policy during rollout.

## Roadmap

| Milestone | Scope |
| --- | --- |
| ✅ M1 | Claude Code → Copilot Anthropic; identity adapters; full preprocessing pipeline |
| ✅ M2 | Cross-platform publish (win-x64, win-arm64, linux-x64, osx-arm64) |
| ✅ M3 | Codex (Responses shape) → `/codex/responses`; T1–T4 translators through the shared IR; command-auth `/codex/models` discovery with safe large-context limits; live `codex.exe` end-to-end |
| M4 | Gemini CLI client + IR↔Gemini translators |

## References

- [`docs/pipeline-design.md`](docs/pipeline-design.md) — pipeline architecture spec
- [`docs/routing.md`](docs/routing.md) — `Routing.Locations` config reference
- [`docs/timeout-chain.md`](docs/timeout-chain.md) — timeouts along the Claude Code → bridge → Copilot chain
- [`docs/copilot-api-research.md`](docs/copilot-api-research.md) — Copilot API protocol notes
- [`docs/codex-implementation-design.md`](docs/codex-implementation-design.md) — Codex inference + model-metadata paths
- [`docs/token-storage.md`](docs/token-storage.md) — token-at-rest threat model
- [`docs/design.md`](docs/design.md) — original design doc
