## Why

gpt-5.6 introduced **namespaced tools** — a tool can live in a NON-default namespace
(the `collaboration` multi-agent namespace: `list_agents`, `spawn_agent`, …, or an
MCP registry) declared as a `{"type":"namespace","name":"…","tools":[…]}` wrapper.
Copilot's native `/responses` streams such a call's `function_call` output item with
a `"namespace"` field, and the client MUST round-trip that field back on echo, or the
next turn 400s:

```
Missing namespace for function_call 'list_agents'. It does not exist in the default
namespace. Round-trip the model's function_call item with its namespace field included.
```

The bridge dropped `namespace` on BOTH hops: T3 (Responses SSE→IR) read only
`call_id`/`name` off the function_call item, so Codex never learned the namespace and
could not echo it; and even if it had, T1/T2 had no `namespace` field to carry or
re-emit. A real gpt-5.6-sol multi-agent session (spawn_agent/list_agents) therefore
died the moment the model used a collaboration tool and the client replayed the call
(live: `request-traces/20260711-111649-0010`, `list_agents` echo lacks namespace →
`buffered status=400`).

This is a distinct feature from the three prior gpt-5.6 tool bugs (additional_tools
400, custom-tool response-side arg loss, custom-tool request-side raw args) — none of
them touched namespaces, and no test exercised a namespaced tool, which is exactly why
it only surfaced on a real codex.exe collaboration task.

Contract source (authoritative, not reverse-engineered from the error):
- **openai/codex** `codex-rs/core/tests/common/responses.rs` —
  `ev_function_call_with_namespace` / `ev_custom_tool_call_with_namespace` put a
  top-level `"namespace"` string on the `output_item.done`/`.added` item, sibling to
  `call_id`/`name`.
- **vercel/ai SDK** CHANGELOG: a `tool_search`-dispatched tool's `function_call`
  carries `namespace`; the read side stores it under `providerMetadata.openai.namespace`
  and the write side MUST re-emit it — the exact same error otherwise.
- **Live replay** (`NamespaceRealReplayProbe`): the real 400-ing request replayed
  verbatim → 400 "Missing namespace"; the SAME bytes with `"namespace":"collaboration"`
  injected on the echoed call → 200. Necessary AND sufficient.

## What Changes

- **Model.** `ResponsesFunctionCallItem` gains an optional `Namespace` field (present
  only for a non-default-namespace tool; absent on the wire otherwise).
- **T3 (Responses SSE→IR) carries the namespace.** `OnOutputItemAdded` reads the
  function_call/custom_tool_call item's `namespace` and stamps a bridge-internal marker
  `bridge_tool_namespace` on the IR `tool_use` content_block (alongside the existing
  `bridge_input_is_grammar_text` marker). The marker is removed at BOTH client edges: T4
  strips it on the `/codex` route, and — because T3 also runs when Claude Code is routed
  to a gpt-5.6 `/responses` backend (the CC→gpt route) — `ClaudeCodeOutboundAdapter`
  scrubs it there (see `fix-codex-exec-custom-tool-call`; without that scrub the marker
  would leak to `claude.exe`). Native Anthropic `/cc` traffic never stamps it.
- **T4 (IR→Responses SSE) re-emits it to Codex.** `OnBlockStart` lifts
  `bridge_tool_namespace` off the content_block; `FunctionCallItem` writes `"namespace"`
  on the Codex-facing function_call item (sibling to call_id/name, per the codex
  fixture). The marker itself never reaches Codex.
- **T1 (Responses→IR) preserves an echoed namespace.** The `function_call` case stashes
  a non-default `namespace` into the tool_use block's part-level
  `ProviderExtensions["openai"]` bag (unified with the grammar-text marker in
  `BuildFunctionCallPartBag`; returns null when neither applies → H1 byte-identity).
- **T2 (IR→Responses wire) re-emits an echoed namespace.** The `ToolUseBlockParam`
  case writes `"namespace"` on the upstream function_call when the bag carries it
  (`TryGetToolNamespace`). Default-namespace tools emit no field — byte-identical.

## Impact

- Fixes the production 400 that killed every gpt-5.6 multi-agent / MCP tool loop.
- Native Anthropic `/cc` traffic is behaviorally unchanged (T3 never runs for it, so no
  marker is stamped). The CC→gpt route (Claude Code → a gpt-5.6 `/responses` backend) DOES
  run T3, so this batch adds a `ClaudeCodeOutboundAdapter` scrub that strips the internal
  `bridge_tool_namespace` / `bridge_input_is_grammar_text` markers before they reach
  `claude.exe` (byte-identical no-op when no marker is present).
- No change to default-namespace function tools or custom (grammar) tools: the
  namespace field is emitted only when present, so their wire bytes are unchanged.

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: the four translators SHALL round-trip a tool's
  `namespace` in BOTH directions — T3/T4 SHALL deliver a streamed non-default-namespace
  `function_call`'s namespace to Codex, and T1/T2 SHALL re-emit it on echo — so a
  gpt-5.6 collaboration/MCP tool loop no longer 400s with "Missing namespace for
  function_call". A default-namespace tool's wire bytes SHALL be unchanged.
