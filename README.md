# copilot-bridge

A .NET 10 Native AOT reverse proxy that exposes the **GitHub Copilot LLM API**
under a vendor-neutral URL prefix per client, so Claude Code, Codex, and
Gemini CLI can all use Copilot as their model backend.

```
Claude Code (Anthropic shape) ──► /cc/v1/messages              ┐
Codex       (OpenAI shape)    ──► /codex/v1/chat/completions   ├─► copilot-bridge ─► api.githubcopilot.com
Gemini CLI  (Gemini shape)    ──► /gemini/v1/...               ┘
```

Ships as a single ~12 MB `.exe` with no .NET runtime dependency.

## Status

**Beta / personal-use.** Claude Code → Copilot Anthropic is the working hot
path (text, tool round-trips, MCP tools, streaming, prompt-cache hits). OpenAI
(Codex) and Gemini paths are M3/M4 — see
[`docs/pipeline-design.md`](docs/pipeline-design.md).

Per-model wire behavior (which effort levels, thinking shapes, and context
windows each Copilot model actually accepts) is probed, not guessed — see the
matrix in `tests/CopilotBridge.Playground/ModelProfileProbe.cs` that feeds
`ModelProfileCatalog`. Unknown models surface a 400 + Anthropic-format error,
never a silent passthrough.

## Quick start

Requires Windows + .NET 10 SDK + Visual Studio C++ Build Tools (for the AOT
linker).

```pwsh
# 1. Clone + build
git clone https://github.com/hooyao/copilot-bridge
cd copilot-bridge
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
#  → produces .\publish\copilot-bridge.exe at the repo root

# 2. Start the server. If no GitHub OAuth token is on disk (first run,
#    fresh machine), it prints a device-code URL + user code to stdout
#    and blocks polling GitHub until you complete the browser handshake;
#    the resulting token is DPAPI-encrypted and saved next to the exe
#    (or ~\github_token.dat as fallback) so subsequent starts are silent.
.\publish\copilot-bridge.exe serve              # default port 8765

# Optional: run the device-code flow up front, without starting the server.
.\publish\copilot-bridge.exe auth login

# Optional: confirm Copilot is reachable + list the available Claude models.
.\publish\copilot-bridge.exe debug list-models
```

Point Claude Code at the bridge:

```jsonc
// .claude/settings.local.json
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
    "ANTHROPIC_AUTH_TOKEN": "dummy"
  }
}
```

## How it works

A typed pipeline framework runs every request through the same shape — the
bridge IR is the Anthropic Messages API. Each stage is a single-purpose
transformation; new clients/backends extend the pipeline without rewriting
the core. See [`docs/pipeline-design.md`](docs/pipeline-design.md) for the
architectural contract (pipeline + adapters + strategies + diag tracer) and
[`docs/copilot-api-research.md`](docs/copilot-api-research.md) for the
protocol-level facts driving each stage.

The request pipeline for `Pipeline<MessagesRequest>`:

```
ModelRouter → AssistantThinkingFilter → SystemSanitize → MessagesSanitize
            → ToolsSanitize → HeadersOutbound
            → CopilotMessagesPassthroughStrategy → DoneFilter (response side)
```

`ModelRouter` normalizes the requested id, applies the first matching
nginx-style location in `appsettings.json` (`Routing.Locations` — a `When`
match → `Use` change-set that can swap model, remap effort, set/remove a
whitelisted header), then looks the result up in
`ModelProfileCatalog` and runs `ProfileAdjuster` to coerce the body to the
target's wire contract (effort, thinking shape, mid-conversation `system`
handling, beta strips). See [`docs/pipeline-design.md §7`](docs/pipeline-design.md)
for the full flow and [`docs/copilot-api-research.md`](docs/copilot-api-research.md)
for the underlying protocol facts.

## Limitations

The bridge passes through whatever Copilot's `/v1/messages` accepts — a curated
subset of Anthropic's API surface. A few Claude Code features differ from a paid
Anthropic subscription:

- **WebSearch tool doesn't work.** Claude Code's built-in WebSearch uses
  Anthropic's server-side search, which Copilot exposes on no model. The bridge
  returns a friendly error. **Workaround:** use a search MCP server (via
  `--mcp-config` or `.mcp.json`) and disable the built-in WebSearch tool. MCP
  tools flow through transparently.

- **`max` / `xhigh` effort isn't universal.** Per-model effort support is probed,
  not guessed (`tests/CopilotBridge.Playground/ModelProfileProbe.cs`), and is
  non-monotonic: `opus-4.8` / `opus-4.7` accept `low`–`max`; `opus-4.6` /
  `sonnet-4.6` accept `max` but reject `xhigh`; `sonnet-4.5` / `haiku-4.5` /
  `opus-4.5` take no effort field at all. The bridge strips an effort the target
  rejects rather than letting it 400.

- **Resume reverts `[1m]` to 200k.** The 1M context flag lives in the model
  string (`opus[1m]`), which isn't persisted across `--resume`. The backend
  still serves the larger window, but Claude Code's own auto-compaction triggers
  at 200k until you re-select `opus[1m]`. See
  [`docs/context-window.md`](docs/context-window.md).

- **Cost / quota counts against your Copilot subscription**, not Anthropic
  billing. The bridge has no Anthropic API key and never falls back to
  `api.anthropic.com` at runtime.

## Roadmap

| Milestone | Scope |
| --- | --- |
| ✅ M1 | Claude Code → Copilot Anthropic; identity adapters; full preprocessing pipeline |
| M2 | HTML config page; cross-platform publish (linux-x64, osx-arm64) |
| M3 | Codex (OpenAI shape) client + IR↔OpenAI translators (request body + streaming SSE state machine) |
| M4 | Gemini CLI client + IR↔Gemini translators |

## Build from source

```pwsh
dotnet build CopilotBridge.slnx                     # Debug
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
dotnet test tests/CopilotBridge.Playground          # requires `auth login` first
```

CI runs Debug build + Release AOT publish on every push to `main` (no live
Copilot tests). Tagging `release-X.Y.Z` triggers the release workflow which
zips the AOT exe and publishes a GitHub Release.

## Diagnostics

Two log channels:

- **Runtime text log** (always-on) — Serilog with two sinks: console (stderr)
  and a per-startup file at `<exe-dir>/log/bridge-{YYYYMMDD-HHMMSS}.log`
  (startup banner, stage debug, errors). One file per process start makes a
  single run trivially greppable; old files accumulate until cleaned up.
  Levels are set per category in `appsettings.json`'s `Logging:LogLevel`
  section (default `Debug` for `CopilotBridge.Cli`).
- **Per-request audit trace** (opt-in, **off by default**) — set
  `"Tracing": { "Enabled": true }` in `appsettings.json` to capture four JSON
  files per request under `request-traces/`
  (`<utc>-<seq>-{inbound-req|inbound-resp|upstream-req|upstream-resp}.json`):
  inbound headers/body, upstream URL/headers/body, all SSE events (including
  the filtered `[DONE]`), duration. Off by default because traces contain full
  prompts; turn it on to debug a cache-hit or protocol mismatch, then off
  again. Useful for cache-hit verification and protocol-mismatch debugging.

## References

- [`docs/pipeline-design.md`](docs/pipeline-design.md) — pipeline architecture spec
- [`docs/routing.md`](docs/routing.md) — `Routing.Locations` config reference (nginx-style match/rewrite)
- [`docs/copilot-api-research.md`](docs/copilot-api-research.md) — Copilot API protocol notes
- [`docs/design.md`](docs/design.md) — original design doc
- [`docs/size-history.md`](docs/size-history.md) — AOT binary size record per change
- [`tests/harness/README.md`](tests/harness/README.md) — end-to-end harness instructions
