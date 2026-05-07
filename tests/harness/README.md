# copilot-bridge harness

This harness drives **real headless Claude Code** against the bridge to verify
end-to-end protocol compatibility with GitHub Copilot. It is driven by Claude
Code (the chat session) — there is no test runner, no assertion DSL.
Run a prompt, read the logs, reason about what worked.

## Files

- `../../.claude/settings.local.json` — Claude Code env config pointing at the
  bridge. Gitignored. Set `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN=dummy`,
  and the four model env vars.
- `prompts/<id>-<short>.md` — one scenario per file. First line is an HTML
  comment hinting at the `--allowedTools` set and what the prompt is meant to
  exercise. The body is the prompt verbatim.
- `last-stdout.txt` / `last-stderr.txt` — scratch capture of the most recent
  `claude -p` run. Gitignored, overwritten each invocation.
- `../../logs/<timestamp>-<seq>.json` — the bridge's per-request capture
  (inbound request, upstream request, upstream SSE events, downstream SSE
  events, status, duration). Will exist once the bridge `/v1/messages` endpoint
  is implemented (M1 step 6).

## Workflow

1. **Start the bridge** (background). Once the server endpoint exists:

   ```pwsh
   Start-Process -NoNewWindow .\publish\copilot-bridge.exe
   ```

   Until M1 step 6 is done, this is a placeholder.

2. **Run a prompt**. Read the `<!-- allowed_tools: ... -->` hint at the top of
   the prompt file, then:

   ```pwsh
   claude -p (Get-Content tests/harness/prompts/02-bash-list.md -Raw) `
       --output-format stream-json `
       --allowedTools "Bash" `
       --verbose > tests/harness/last-stdout.txt 2>&1
   ```

3. **Inspect**:

   - `tests/harness/last-stdout.txt` — Claude Code's stream-json output (one
     SDKMessage per line: `assistant`, `tool_use`, `tool_result`, `result`).
   - Newest file in `logs/` — bridge captures of the same conversation. Useful
     for diagnosing protocol mismatches between Claude Code and Copilot.

## Prompt corpus

| ID | What it exercises |
| --- | --- |
| `01-simple-text` | Pure text, no tools |
| `02-bash-list` | Single Bash tool call |
| `03-read-summarize` | Single Read tool call |
| `04-prompt-cache` | ≥2 model turns in one invocation; verifies prompt caching round-trips through the bridge |

Add prompts when a new feature needs coverage. **No frontmatter, no expectation
DSL.** Just the prompt body and a one-line `<!-- allowed_tools: ... -->` hint.
Claude Code (the dev session) reads logs and reasons about pass/fail.

## Capability verification: two layers

Two checklists, two scopes:

1. **Copilot API layer** — does Copilot's `/v1/messages` accept the wire format?
   Verified by `tests/CopilotBridge.Playground/` (xUnit, hits live Copilot).
2. **End-to-end bridge layer** — does Claude Code → bridge → Copilot work?
   Verified by this harness (real headless Claude Code through the bridge).

Layer 1 must work before Layer 2 makes sense. Once the bridge endpoint is built,
each Layer 2 box gets ticked when a prompt runs end-to-end.

### Layer 1: Copilot API capabilities (playground tests)

These confirm Copilot's `/v1/messages` accepts each Anthropic feature. Test source
in `tests/CopilotBridge.Playground/`. See `docs/copilot-api-research.md` §15 for
detailed findings, including model-specific quirks (haiku-4.5 lies about adaptive
thinking; opus-4.7-1m-internal rejects explicit thinking; vision rejects images
under ~100×100; etc.).

- [x] **GitHub OAuth (device-code flow)** — `copilot-bridge auth login` round-trip
- [x] **DPAPI-encrypted token persistence** — `Auth/TokenStore.cs`
- [x] **Copilot token exchange + auto-refresh** — `Auth/CopilotTokenClient.cs`
- [x] **Model discovery** — `copilot-bridge debug list-models`; 11 Claude models on Enterprise
- [x] **Adaptive thinking + reasoning_effort** — `EffortLevelsTests` (4 levels on opus-4.7-1m-internal)
- [x] **Explicit thinking budget** — `ExplicitThinkingTests` (sonnet-4.6, budget=2048)
- [x] **Prompt caching, 5m TTL** — `PromptCachingTests` (sonnet, haiku); 5303 tokens cached, exact round-trip
- [x] **Prompt caching, 1h TTL** — `ExtendedCacheTtlTests` (sonnet)
- [x] **Streaming SSE** — `StreamingTests`; full Anthropic event sequence + `[DONE]` terminator (filter)
- [x] **Tool use round-trip** — `ToolUseTests` (sonnet, haiku)
- [x] **Parallel tool calls** — `ParallelToolUseTests` (sonnet)
- [x] **Vision (base64 image)** — `VisionTests` (sonnet, 100×100 PNG)
- [x] **Context management field accepted** — `ContextManagementTests` (sonnet); triggering `applied_edits` not tested
- [x] **`max_tokens` truncation** — `MaxTokensTests` (sonnet, haiku); `stop_reason="max_tokens"`

Run: `dotnet test`. ~1m40s, 18 cases, single-threaded (Anthropic per-account rate limit).

### Layer 2: End-to-end bridge (this harness)

Tick once Claude Code → bridge → Copilot works end-to-end via `claude -p`. Ordered
roughly by complexity:

- [ ] `/v1/messages` Anthropic native passthrough (depends on M1 endpoint, not yet built)
- [ ] `/v1/messages/count_tokens` placeholder (returns `{input_tokens:1}`)
- [ ] `/v1/models` discovery filtered to `/v1/messages`-supporting models
- [ ] Pure text request (`prompts/01-simple-text.md`)
- [ ] Single Bash tool call (`prompts/02-bash-list.md`)
- [ ] Single Read tool call (`prompts/03-read-summarize.md`)
- [ ] Prompt caching across turns (`prompts/04-prompt-cache.md`); inspect logs for `cache_read_input_tokens > 0` on 2nd+ upstream calls
- [ ] Parallel tool calls (`prompts/05-parallel-tools.md` — TBA)
- [ ] Extended thinking (`prompts/06-thinking.md` — TBA)
- [ ] Context window exceeded → auto-compact (`prompts/07-compaction.md` — TBA)
- [ ] Sub-agent / Task tool dispatch (`prompts/08-subagent.md` — TBA)
- [ ] Conversation resume via `--resume` (`prompts/09-resume.md` — TBA)
- [ ] MCP server tools (when MCP set up, `prompts/10-mcp.md` — TBA)

## Verifying prompt cache

Anthropic returns `cache_creation_input_tokens` (first time prefix is cached)
and `cache_read_input_tokens` (subsequent reuses) in the `usage` block of
`message_start` and `message_delta` events. To verify the bridge preserves
these:

```pwsh
Select-String -Path logs\*.json -Pattern 'cache_read_input_tokens'
```

In a single `claude -p` invocation that triggers ≥2 upstream calls (e.g. one
tool round-trip), the 2nd+ upstream-response logs should show
`cache_read_input_tokens > 0`. If they're always 0, the bridge is dropping
`cache_control` somewhere — check the preprocessing pipeline.
