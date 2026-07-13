## Context

The captured incident was not a malformed tool translation: Claude Code sent a valid `Agent` definition on requests whose system prompt declared `cc_is_subagent=true`, and T2 emitted the same tool as a Responses function. GPT then invoked it recursively. Claude Code bounded depth but allowed enough width to produce a 4 → 4 → 22 → 78 → 219 tree. The existing runaway guard is intentionally per response and therefore did not trip.

The inbound `x-claude-code-agent-id` header is present on every sub-agent request, including first-generation children that omit `x-claude-code-parent-agent-id`. `HeadersOutboundStage` clears private Claude headers before the selected strategy runs, so the endpoint must snapshot the classification on request-scoped pipeline state.

## Goals / Non-Goals

**Goals:**

- Prevent a GPT Responses backend from recursively invoking Claude Code's `Agent` tool when serving a Claude Code sub-agent.
- Apply the policy only while translating `/cc` IR to a Responses upstream request.
- Preserve root-agent delegation, native Anthropic passthrough, native Codex behavior, and valid `tool_choice` output.
- Make the behavior operator-configurable and enabled by default.

**Non-Goals:**

- Add a global session-wide concurrency or rate limiter.
- Modify Claude Code's transcript, system prompt, or local agent scheduler.
- Treat upstream 429s or ordinary parallel tool calls as runaways.
- Infer sub-agent state from prompt text or the optional parent-agent header.

## Decisions

### D1 — Snapshot a typed sub-agent signal at the `/cc` endpoint

`ClaudeCodeMessagesEndpoint` SHALL set a boolean on the scoped `BridgeContext` when the inbound header dictionary contains a non-empty `x-claude-code-agent-id`. This survives outbound-header sanitization without forwarding the private identifier or coupling T2 to ASP.NET.

Alternative considered: inspect `x-claude-code-parent-agent-id`. Rejected because the incident proves first-generation sub-agents omit it.

Alternative considered: retain the private header through `HeadersOutboundStage`. Rejected because it would risk leaking Claude client metadata upstream and violate the header boundary.

### D2 — Filter only in T2's typed Anthropic-tool writer

`CopilotResponsesStrategy` SHALL combine the request-scoped sub-agent signal with the configuration switch and pass one explicit filter decision to `ResponsesRequestBuilder`. `WriteIrTools` SHALL omit only the exact case-sensitive tool name `Agent`. It SHALL not mutate `MessagesRequest.Tools`, so a different strategy or audit of the IR observes the original request.

The builder SHALL report whether it actually removed `Agent`, and the strategy SHALL emit a warning only for an actual removal. The warning SHALL identify `Pipeline:CcToResponses:PreventRecursiveAgentDelegation=false` plus a restart as the recovery path when an operator considers the removal incorrect.

The Codex bag path is outside this policy: native Codex tools are re-emitted from `ProviderExtensions["openai"]`, while Claude Code tools use the typed IR writer. This placement therefore makes the requested route isolation structural rather than dependent on model names.

Alternative considered: remove `Agent` in the shared `ToolsSanitizeStage`. Rejected because that stage is Anthropic-upstream-only and modifying the IR there would also risk changing native `/cc` behavior.

### D3 — Reuse survivor-aware tool-choice reconciliation

The existing survivor set returned by `WriteIrTools` SHALL exclude `Agent`. If Claude Code forced that removed tool, `WriteIrToolChoice` SHALL emit `"auto"`, avoiding a Responses request that names a missing function. Other tool choices remain unchanged.

### D4 — Startup-bound, default-on configuration

The switch SHALL be `Pipeline:CcToResponses:PreventRecursiveAgentDelegation`, bound to an AOT-safe options class and read from the same non-reloading startup configuration as existing pipeline options. `true` is the shipping default because the unsafe behavior can create an unbounded-width request tree; `false` is the explicit compatibility escape hatch.

### D5 — Contract and real-client verification

Contract tests SHALL prove the four boundary cases: enabled sub-agent removes `Agent`; enabled root retains it; disabled sub-agent retains it; native Codex bag tools are untouched. A forced `Agent` choice SHALL downgrade to `auto`. Tests SHALL be mutation-checked by disabling the filter and observing the sub-agent assertion fail.

Final acceptance SHALL drive real headless `claude.exe` through `CcToGpt` on a task that asks a root agent to delegate and asks the child to delegate again. The transcript must show the root `Agent` tool executes, while the child request's upstream trace lacks `Agent`; the child must still finish through its remaining tools and the turn must complete.

## Risks / Trade-offs

- **A workflow intentionally depends on recursive delegation** → operators can disable the switch; root-level parallel delegation remains available by default.
- **Claude Code changes its sub-agent header contract** → captured-byte tests pin the current real-client signal; a future protocol update must revise the classifier from live evidence.
- **A differently cased tool bypasses the filter** → tool names are protocol identifiers and case-sensitive; filtering exact `Agent` avoids accidentally removing an unrelated user-defined tool.
- **The filter leaves an invalid forced choice** → reuse the existing survivor-aware downgrade to `auto` and pin it in tests.

## Migration Plan

1. Ship the default-on option and translation guard together.
2. Existing configurations inherit protection without edits.
3. An operator requiring recursive GPT-backed delegation can set the option to `false` and restart the bridge.
4. Rollback is configuration-only; no persisted data or transcript migration is involved.

## Open Questions

- A session-wide concurrency limiter remains useful defense in depth, but requires separate lifecycle/state semantics and is intentionally deferred.

## Verification Record

- Real-client manifest: `tests/behavior-runs/manifests/cc-to-gpt-recursive-agent-guard-20260713-104135-001.json`.
- Bridge used the OS-assigned `http://localhost:64214`, not port 8765, with Claude Code 2.1.207 and the `CcToGpt` scenario.
- The real client transcript records a root `Agent` tool call, child id `aa222890d1746a5ab`, the corresponding completed tool result, child Bash/Read tool results, a root Read result, and final canary `cc-agent-guard-canary-73154`.
- Child request `20260713-104106-0003` carried `x-claude-code-agent-id` without a parent-agent header. Its inbound tool set contained `Agent` (27 tools); the translated upstream `/responses` tool set omitted only `Agent` (26 tools). The same held on all later child turns.
- No grandchild was launched; the child executed two Bash calls and one Read, the root consumed the result and completed successfully, and no `bridge_tool_namespace` or `bridge_input_is_grammar_text` marker appeared in any Claude-facing response.
- The temporary home token hard link used only to let the subprocess follow `TokenStore`'s documented fallback was deleted after the test; `%USERPROFILE%\github_token.dat` does not exist.
