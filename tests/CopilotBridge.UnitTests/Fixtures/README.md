# A-invariant fixtures — real Claude Code request captures

These `cc-request-*.json` files are **real, de-identified** Anthropic request
bodies that `claude.exe` put on the wire, captured through the bridge. They are
the input corpus for the A-invariant test suite
(`tests/CopilotBridge.UnitTests/Invariant/`, design:
`docs/ir-definition-design.md` §7).

## They are INPUT SAMPLES, never oracles

Per `docs/ir-definition-design.md` §7.0: the A-invariant tests assert
mathematical properties of **our own translators** (round-trip self-inverse,
opaque byte-passthrough, provider-extensions bag survival, hot-path
byte-equality). Those hold regardless of how Copilot behaves — so a fixture
never "expires" and is never treated as a statement about what Copilot currently
accepts. (What Copilot currently accepts is the B-contract suite's job — a later
change.)

## Layout

- `cc-request-<slug>.json` — `{ "_meta": {...}, "body": {...} }`. `body` is the
  verbatim (de-identified) Anthropic request; `_meta` stamps the client version,
  capture date, source, and purpose so a stale fixture is visible.
- `hotpath-golden/<slug>.upstream.json` — the frozen serialized **upstream body**
  the hot path produces for that fixture (H1b byte-equality golden). Regenerate
  ONLY via `BRIDGE_REGEN_HOTPATH_GOLDEN=1` after an intentional serialization
  change, then review the git diff.

## Coverage

| slug | turns | exercises |
| --- | --- | --- |
| `plain-opus48` | 1 | minimal text + thinking:enabled (A1/H1 baseline) |
| `haiku45-enabled-thinking-tools` | 1 | system(+cache_control) + 4 tools + metadata + thinking:enabled + context_management + stream |
| `opus47-adaptive-effort-medium` | 1 | thinking:adaptive + output_config.effort=medium |
| `sonnet46-adaptive-effort-high` | 1 | thinking:adaptive + output_config.effort=high |
| `sonnet46-toolcall-thinking-multiturn` | 3 | tool_use.Input + thinking.signature (324 chars) + call_id pairing (A2b/A5) |
| `opus47-toolcall-multiturn` | 3 | tool_use.Input + call_id pairing, no thinking (A5) |

## Refresh procedure

When Claude Code or the IR changes shape and the fixtures should be re-captured:

1. **Capture** — run a headless `claude.exe` session through `BridgeFixture`,
   which writes a four-file audit per request to
   `tests/CopilotBridge.Playground/bin/Debug/net10.0/request-traces/`. The
   single-turn samples come from a smoke run; the multi-turn tool-call samples
   come from the tool round-trip test:
   ```pwsh
   $env:CLAUDE_EXE = "C:\path\to\claude.exe"
   dotnet test tests/CopilotBridge.Playground/CopilotBridge.Playground.csproj `
     --filter "FullyQualifiedName~ToolUseHeadlessTests"
   ```
   (Uses an ephemeral port, never 8765; production auth via the user's
   `github_token.dat`.)

2. **De-identify + promote** — pick representative `*-inbound-req.json` files and
   list them in `docs/scratch/deidentify-cc-fixtures.py` (`CHOSEN`), then:
   ```pwsh
   python docs/scratch/deidentify-cc-fixtures.py
   ```
   The script strips PII (git user, machine paths, device/session ids)
   deterministically and writes the committed `cc-request-*.json` here, asserting
   no PII leaked. The capture script + raw traces stay gitignored in
   `docs/scratch/` / `bin/`; only the de-identified fixtures are committed.

3. **Re-seed goldens** — delete `hotpath-golden/` and run the H1b test once to
   re-seed, then review the diff:
   ```pwsh
   dotnet test tests/CopilotBridge.UnitTests/CopilotBridge.UnitTests.csproj `
     --filter "FullyQualifiedName~HotPath"
   ```
