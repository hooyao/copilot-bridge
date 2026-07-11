## Why

gpt-5.6's `exec` is a **custom (grammar) tool**. codex 0.144.1's exec handler
(`codex-rs/core/src/tools/code_mode/execute_handler.rs`, `matches_kind` = `Custom`
only) accepts **only** `ToolPayload::Custom { input }` — i.e. a `custom_tool_call`
output item streamed via `custom_tool_call_input.*`. The bridge's T4
(`AnthropicToResponsesStream`) always emitted tool calls as a `function_call`
(`function_call_arguments.*`). codex built `ToolPayload::Function` from that and
fatally rejected it — **`Fatal error: tool exec invoked with incompatible payload`**
(in `~/.codex/logs_2.sqlite`: `ERROR codex_core::tools::router`) — aborting EVERY exec
at dispatch, before any shell ran. The bridge stayed HTTP 200 throughout, so the
failure was invisible in status and every unit/round-trip test that stopped at
"bridge 200" passed while real exec was 100% broken. Diagnosed from codex's OWN logs
(not the bridge's), fixed, and verified by a real headless codex run (the user
confirmed exec executes).

This change also carries the fixes the pre-ship review surfaced on the same code:

- **CC→gpt marker leak (regression this PR would otherwise introduce).** T3 stamps
  two bridge-internal markers (`bridge_input_is_grammar_text`, `bridge_tool_namespace`)
  on `tool_use` `content_block_start` for T4 to consume. On the Codex route T4 removes
  them, but on the **CC→gpt route** (Claude Code routed to a gpt-5.6 `/responses`
  backend — a supported route, `docs/routing.md`) the outbound adapter is the identity
  `ClaudeCodeOutboundAdapter` with no T4, so the markers leaked verbatim to `claude.exe`.
  `bridge_tool_namespace` is new in this PR, so its leak is a fresh regression.
- **`agent_message` typed model removed.** A `required`-field record for a still-evolving
  multi-agent shape re-introduced the 400-on-shape-evolution the passthrough exists to
  end, for zero behavioral gain (the bridge only forwards it). Deleted; it rides the
  universal `ResponsesUnknownItem` path (strictly more byte-faithful).

## What Changes

- **T4 emits `custom_tool_call` for custom (grammar) tools.** `AnthropicToResponsesStream`
  reads T3's `bridge_input_is_grammar_text` marker (new `_toolIsCustom`, reset per block)
  and, for a custom tool, emits `output_item.added/.done` with `item.type:"custom_tool_call"`
  (+ `input`) and streams input via `response.custom_tool_call_input.delta/.done` (field
  `input`), NOT `function_call`/`function_call_arguments`. New `ToolCallItem()` branches on
  `_toolIsCustom`; both forms carry `namespace` when present (so a future namespaced custom
  tool round-trips it). A tripwire `LogWarning` fires if `exec` ever arrives without the
  grammar marker (would-be `incompatible payload`).
- **CC-edge marker scrub.** `ClaudeCodeOutboundAdapter` strips `bridge_input_is_grammar_text`
  and `bridge_tool_namespace` from `content_block_start` events before they reach a Claude
  Code client. Gated on a substring pre-check — byte-identical pass-through for the common
  case (real Anthropic backend never stamps them).
- **`agent_message` un-modeled.** `ResponsesAgentMessageItem` + its `[JsonDerivedType]` +
  `KnownTypes` entry + `SerializeAgentMessage` removed; `agent_message` rides
  `ResponsesUnknownItem` like every other opaque item.
- **Hardening.** `KnownTypes`↔`[JsonDerivedType]` drift now guarded by a test; passthrough
  re-emit uses `WriteRawValue(GetRawText())` (not `WriteTo`, which reserializes); a
  malformed passthrough `after` defaults to end-append (not front-hoist).

## Impact

- Fixes the exec abort — real codex exec loops work again.
- Closes the CC→gpt marker leak (no bridge-internal metadata reaches Claude Code).
- No change to plain function tools (they still emit `function_call`) or the `/cc`
  Anthropic-passthrough path (marker scrub is a no-op there).

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: T4 SHALL emit a CUSTOM (grammar) tool — gpt-5.6 `exec` — to
  Codex as a `custom_tool_call` item (streamed via `custom_tool_call_input.*`), NOT a
  `function_call`, so codex's exec handler accepts the payload instead of aborting with
  "incompatible payload". The bridge SHALL NOT leak its internal `content_block` markers
  (`bridge_input_is_grammar_text`, `bridge_tool_namespace`) to any client — T4 removes them
  on the Codex route and the Claude Code outbound edge scrubs them on the CC→gpt route.
