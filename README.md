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

- `claude -p` round-trips through the bridge succeed (text, tool round-trip).
- Identical-prompt runs hit Copilot's prompt cache (`cache_read_input_tokens`
  matches the prefix) — verified `SystemSanitizeStage` is correctly stripping
  Claude Code's volatile `# currentDate` injection.
- Playground (24 xUnit cases) hits live Copilot `/v1/messages` for
  capability-level assertions: thinking variants, prompt caching, streaming,
  tool use, parallel tools, vision, context management, max_tokens.

## Quick start

Requires Windows + .NET 10 SDK + Visual Studio C++ Build Tools (for the AOT
linker).

```pwsh
# 1. Clone + build
git clone https://github.com/hooyao/copilot-bridge
cd copilot-bridge
.\aot_publish.bat                              # produces publish\copilot-bridge.exe

# 2. One-time GitHub OAuth (device-code flow). Token is DPAPI-encrypted and
#    stored next to the exe (or ~\github_token.dat as fallback).
.\publish\copilot-bridge.exe auth login

# 3. Verify Copilot reachable
.\publish\copilot-bridge.exe debug list-models  # 11 Claude models on Enterprise

# 4. Start the server
.\publish\copilot-bridge.exe serve              # default port 8765
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

The 7 request stages currently active for `Pipeline<MessagesRequest>`:

```
ModelRouter → AssistantThinkingFilter → SystemSanitize → MessagesSanitize
            → ToolsSanitize → ThinkingRewrite → HeadersOutbound
            → CopilotMessagesPassthroughStrategy → DoneFilter (response side)
```

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
- **Diag trace** (`<exe-dir>/logs/diag.log` when `BRIDGE_DIAG_FILE` env var
  is set; Debug builds only) — per-stage timing and one-line diff descriptions
  via `DiagTracer.Log(...)`. `[Conditional("BRIDGE_DIAG")]`-stripped from
  Release builds → zero runtime cost in production.

## References

- [`docs/pipeline-design.md`](docs/pipeline-design.md) — pipeline architecture spec
- [`docs/copilot-api-research.md`](docs/copilot-api-research.md) — Copilot API protocol notes
- [`docs/design.md`](docs/design.md) — original design doc
- [`docs/size-history.md`](docs/size-history.md) — AOT binary size record per change
- [`tests/harness/README.md`](tests/harness/README.md) — end-to-end harness instructions
