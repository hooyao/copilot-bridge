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

## Codex fixtures (`codex-request-*.json`, `responses-sse-*.txt`)

Real, de-identified Codex `/responses` data for the Codex A-invariant suite
(`Invariant/Codex*Tests.cs`):

| Fixture | What | Drives |
| --- | --- | --- |
| `codex-request-plain-3turn.json` | developer + 2 user messages, full tool defs, reasoning/text/include | A0/A1/A2/A4/A7 |
| `codex-request-multiturn-8.json` | developer + user + 5 assistant turns | A1/A3/A4/A7 breadth |
| `codex-request-toolcall-multiturn.json` | REAL tool round-trip: `function_call` (shell_command) + `function_call_output` | A5b tool pairing (live-harvested) |
| `responses-sse-text.txt` | live text stream | A6 T3→IR→T4 |
| `responses-sse-toolcall.txt` | live forced-tool stream | A6 tool stream |

Same philosophy: input samples, never oracles. Refresh:

1. **Single-turn / multi-turn** come from a `codex.exe` session captured by the
   throwaway server in `docs/scratch/` (raw HTTP). **The tool-call fixture** is
   harvested from the live E1 tool turn
   (`CodexE2EHeadlessTests.Codex_ToolTurn_CompletesThroughBridge`), whose
   four-file audit lands in `request-traces/` — copy the `inbound-req.json` to
   `docs/scratch/codex-capture/toolflow-audit.json`.
2. De-identify + promote: list sources in `docs/scratch/deidentify-codex-fixtures.py`
   (`CHOSEN` for raw HTTP, `CHOSEN_AUDIT` for audit-envelope captures), then
   `python docs/scratch/deidentify-codex-fixtures.py`. It strips UUIDs (session/
   thread/installation), git user, machine paths, and asserts no PII leaked.
3. **SSE fixtures** come from `ResponsesProbe.CaptureResponsesSseFixtures` with
   `BRIDGE_REGEN_CONTRACT_SNAPSHOT=1` (live, regenerable any time).
