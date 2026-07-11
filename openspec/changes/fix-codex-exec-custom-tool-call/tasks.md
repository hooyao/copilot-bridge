# Tasks — T4 emits custom_tool_call for exec + review hardening (0.4.13-beta)

## 1. Ground the contract (codex source + codex's own logs)
- [x] 1.1 Diagnose from codex `~/.codex/logs_2.sqlite`: `ERROR codex_core::tools::router:
  ... incompatible payload` on every exec, while the bridge logged status=200.
- [x] 1.2 Confirm codex `execute_handler.rs` `matches_kind` = `ToolPayload::Custom` only —
  exec requires a `custom_tool_call` item, rejects `function_call`.
- [x] 1.3 Authoritative custom_tool_call event grammar from openai/codex `ev_custom_tool_call`
  + the real upstream fixture `responses-sse-customtool.txt`.

## 2. Fix — T4 custom_tool_call shape
- [x] 2.1 T4 `AnthropicToResponsesStream`: `_toolIsCustom` from `bridge_input_is_grammar_text`
  (reset per block in OnBlockStart + OnBlockStop). Custom → `custom_tool_call` item +
  `custom_tool_call_input.delta/.done`; plain → `function_call` + `function_call_arguments.*`.
- [x] 2.2 New `ToolCallItem()` branches on `_toolIsCustom`; both forms emit `namespace` when
  present (a future namespaced custom tool round-trips it, not silently dropped).
- [x] 2.3 Tripwire `LogWarning` if `exec` arrives without the grammar marker.
- [x] 2.4 Request-side symmetry: an echoed `custom_tool_call` input item rides the universal
  unknown passthrough → re-emitted verbatim (guarded by `EchoedCustomToolCall_RoundTripsVerbatim`).

## 3. Review-driven fixes (from the 5-agent pre-ship review)
- [x] 3.1 CC→gpt marker LEAK (ship-blocker): `ClaudeCodeOutboundAdapter` scrubs
  `bridge_input_is_grammar_text` + `bridge_tool_namespace` from `content_block_start` on the
  CC edge (no-op when absent). Fixed the false "never reaches /cc" comments in T3.
- [x] 3.2 Deleted `ResponsesAgentMessageItem` (required-field fragility, zero behavioral gain)
  → agent_message rides `ResponsesUnknownItem`.
- [x] 3.3 `KnownTypes`↔`[JsonDerivedType]` drift now guarded by `KnownTypesMatchesDerivedTypesTests`.
- [x] 3.4 Passthrough re-emit uses `WriteRawValue(GetRawText())` (not `WriteTo` reserialize);
  malformed `after` defaults to end-append (not front-hoist).
- [x] 3.5 Documented the field-granular residual on modeled types (Finding 1) on
  `ResponsesFunctionCallItem` — a future new field must be modeled + re-emitted.

## 4. Tests (from contract, mutation-checked)
- [x] 4.1 `CustomTool_FullRoundTrip_ReachesCodexAsCustomToolCall` +
  `CustomTool_RealCapture_...` rewritten to assert custom_tool_call (they froze the
  function_call bug before).
- [x] 4.2 `MixedCustomThenFunctionTool_...` guards the `_toolIsCustom` per-block reset
  (mutation-checked: conditional set + no reset → block 2 mis-emits custom → reddens).
- [x] 4.3 `ClaudeCodeMarkerScrubTests` (4) — markers stripped + real fields survive;
  marker-free byte-identical; a content_block whose VALUE mentions a marker name is not
  rewritten; only content_block marker properties removed (mutation-checked: disable scrub
  → leak test reddens).
- [x] 4.4 `KnownTypesMatchesDerivedTypesTests` (2, mutation-checked).
- [x] 4.5 `AgentMessage_RoundTripsVerbatim` strengthened to re-parse the wire bytes.
- [x] 4.6 Full unit suite green (855).

## 5. Verification (CLIENT'S OWN execution, not bridge 200)
- [x] 5.1 Diagnosed + fixed against codex `logs_2.sqlite` (the client's log), not bridge status.
- [x] 5.2 Real headless codex run: user confirmed exec now executes (no more aborted /
  incompatible payload). This covers the exec `custom_tool_call` fix (the /codex path).
- [x] 5.3 AOT publish clean (0 trim warnings); deployed to 8765.
- [ ] 5.4 **UNVERIFIED (per AGENTS.md 🔴 real-client rule):** the CC→gpt marker-scrub edge
  (`ClaudeCodeOutboundAdapter`) is proven by unit tests (`ClaudeCodeMarkerScrubTests`) but
  NOT by a real `claude.exe` routed to a gpt-5.6 backend executing a tool call. The unit
  scrub tests establish the markers are stripped; they do NOT establish Claude Code
  receives and executes the resulting tool call. A real CC→gpt run (docs/routing.md) is the
  user's to confirm before this route is considered real-client-verified.

## 6. Ship
- [x] 6.1 Pre-ship review (5-agent + 3 Copilot rounds) — findings addressed above.
- [ ] 6.2 **SHIPPING CAVEAT:** exec `custom_tool_call` is real-client-verified (5.2); the
  CC→gpt marker scrub (5.4) is unit-verified only — ship acknowledging that leg is
  UNVERIFIED against a real claude.exe.
- [ ] 6.3 `/ship-pr` → 0.4.13-beta (folds in fix-codex-tool-namespace + fix-codex-unknown-input-items).
