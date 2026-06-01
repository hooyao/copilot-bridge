# copilot-bridge

A .NET 10 Native AOT reverse proxy that exposes the **GitHub Copilot LLM API**
under a vendor-neutral URL prefix per client, so Claude Code, Codex, and
Gemini CLI can all use Copilot as their model backend.

```
Claude Code (Anthropic shape) ──► /cc/v1/messages              ┐
Codex       (OpenAI shape)    ──► /codex/v1/chat/completions   ├─► copilot-bridge ─► api.githubcopilot.com
Gemini CLI  (Gemini shape)    ──► /gemini/v1/...               ┘
```

Ships as a single ~9.5 MB `.exe` with no .NET runtime dependency.

## Status

**Alpha / personal-use.** M1 MVP is functional for Claude Code → Copilot
Anthropic (the hot path; see [`docs/pipeline-design.md`](docs/pipeline-design.md)
for what's in M2/M3/M4). End-to-end verified:

- `claude -p` round-trips through the bridge succeed (text, tool round-trip,
  MCP tools, multi-effort).
- Identical-body streaming requests hit Copilot's prompt cache via the bridge
  (`cache_read_input_tokens > 0` on the second request) — proves the bridge's
  `DoneFilterStage` is cache-neutral. The `[DONE]` SSE terminator is dropped
  on the response side without affecting the next request's cache key.
- `SystemSanitizeStage` strips Claude Code's volatile `# currentDate`
  injection from request bodies so the cacheable prefix stays stable across
  days.
- **Hand-curated `ModelProfileCatalog`** (`Pipeline/Routing/`) describes what
  Copilot's variant of each model actually accepts on the wire, sourced from
  a playground probe matrix in `tests/CopilotBridge.Playground/ModelProfileProbe.cs`.
  Unknown models surface as 400 + Anthropic-format error (with diagnostics)
  rather than silent passthrough.
- **Headless test harness** (xUnit + real `claude.exe`) covers a (model × effort
  × tool-use) matrix: sonnet-4.5/4.6, opus-4.5/4.6/4.7 (with `-high` / `-xhigh`
  variants), opus-4.7-1m-internal, opus-4.6-1m, opus-4.8, haiku-4.5 — plus
  Bash tool round-trip, MCP server round-trip, and direct API diffing against
  `api.anthropic.com`.

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
    "ANTHROPIC_AUTH_TOKEN": "dummy",
    "ANTHROPIC_MODEL": "claude-sonnet-4.6"
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
fold, beta strips). See [`docs/pipeline-design.md §7`](docs/pipeline-design.md)
for the full flow and [`docs/copilot-api-research.md`](docs/copilot-api-research.md)
for the underlying protocol facts.

## Limitations

The bridge passes through whatever Copilot's `/v1/messages` accepts. Copilot's
gateway has a curated subset of Anthropic's API surface, so a few Claude Code
features behave differently than they would against a paid Anthropic
subscription:

- **WebSearch tool doesn't work.** Claude Code's built-in WebSearch uses
  Anthropic's server-side search capability, which Copilot does not expose on
  any model. The bridge intercepts these requests and returns a friendly
  error. **Workaround:** configure a search MCP server in your Claude Code
  config (e.g. via `--mcp-config` or `.mcp.json`) and disable the built-in
  WebSearch tool. MCP tools flow through the bridge transparently.

- **`max` effort is universally stripped.** Probing every Copilot model with
  `effort=max` returns 400 (`output_config.effort "max" is not supported by
  model …`). The bridge strips the field for every profile, so any client
  that sends `max` gets the model's default effort instead of an upstream
  rejection. The richest sized variants Copilot offers are
  `claude-opus-4.7-xhigh` and `claude-opus-4.7-1m-internal` (which itself
  accepts `low/medium/high/xhigh`).

- **`opus-4.7` non-medium effort selects a sized variant.** The base
  `claude-opus-4-7` model only accepts `effort=medium`. For `high` / `xhigh`
  the bridge automatically rewrites the model id to `claude-opus-4-7-high` /
  `claude-opus-4-7-xhigh` (each a fixed-effort variant Copilot exposes
  separately). Transparent to the user — `--model claude-opus-4-7 --effort
  xhigh` just works.

- **`opus-4.8` effort is medium-only, with no sized variants.** Copilot has
  no `-high` / `-xhigh` siblings for 4.8 (only for 4.7). The bridge strips
  any non-medium effort for 4.8; reasoning depth is left to the model's
  default. Until Copilot ships sized 4.8 variants this is the best the
  bridge can do without lying about availability.

- **No mid-conversation `role:"system"` even on opus-4.8.** Anthropic's
  4.8 protocol adds `system` messages outside the first slot; Copilot's
  gateway rejects them on every model. The bridge folds these messages
  into the top-level `system` field automatically so opus-4.8 clients
  keep working — but the model gets the directive as a system prompt
  prefix, not as a turn-position-aware injection.

- **1M context is opus-only.** Copilot ships `claude-opus-4.6-1m` and
  `claude-opus-4.7-1m-internal` as separate model ids. Sonnet and Haiku do
  not have 1M variants on Copilot regardless of what Anthropic offers
  natively. There is no `claude-opus-4.8-1m` either; requests for
  `opus-4.8 + context-1m-2025-08-07` are redirected to
  `claude-opus-4.7-1m-internal` as the closest fallback.

- **No thinking on older models.** `claude-sonnet-4.5`, `claude-opus-4.5`,
  and `claude-haiku-4.5` don't declare any reasoning-effort capability on
  Copilot. Setting `--effort` for these has no effect (bridge strips the
  field before forwarding).

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

- **Audit log** (`logs/<utc>-<seq>.json`) — always-on per-request capture of
  inbound headers/body, upstream URL/headers/body, all SSE events (including
  filtered `[DONE]`), duration. Useful for cache-hit verification and
  protocol-mismatch debugging.
- **Runtime log** — Serilog 4.3.1 with two sinks: console (stderr) and a new
  per-startup file at `<exe-dir>/log/bridge-{YYYYMMDD-HHMMSS}.log`. Default
  level is `Debug`; tune with `BRIDGE_LOG_LEVEL=Information` (or any
  `Verbose|Debug|Information|Warning|Error|Fatal`). One file per process
  start makes a single run trivially greppable; old files accumulate until
  cleaned up.

## References

- [`docs/pipeline-design.md`](docs/pipeline-design.md) — pipeline architecture spec
- [`docs/copilot-api-research.md`](docs/copilot-api-research.md) — Copilot API protocol notes
- [`docs/design.md`](docs/design.md) — original design doc
- [`docs/size-history.md`](docs/size-history.md) — AOT binary size record per change
- [`tests/harness/README.md`](tests/harness/README.md) — end-to-end harness instructions
