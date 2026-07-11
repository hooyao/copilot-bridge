## MODIFIED Requirements

### Requirement: Four translators route Codex through the shared IR

The bridge SHALL translate a Codex request to the frozen Anthropic-shape IR (T1), run the single shared `Pipeline<MessagesRequest>`, and translate back (T4); the upstream Responses call and its response SHALL be handled by a strategy holding T2 (IR→Responses wire) and T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. T1/T4 are real translators (not identity). The Claude Code pipeline SHALL be reused unchanged.

The four translators SHALL round-trip a tool's **namespace** in both directions. A tool in a NON-default namespace (a gpt-5.6 `collaboration` multi-agent tool such as `list_agents`/`spawn_agent`, or an MCP registry declared via a `{"type":"namespace",…}` wrapper) has a `"namespace"` field on its Responses `function_call` output item. On the response side, T3 SHALL carry that namespace through the IR (a bridge-internal `tool_use` marker) and T4 SHALL re-emit it on the Codex-facing `function_call` item, so Codex learns it. On the request side, when Codex echoes such a call back, T1 SHALL preserve the namespace (part-level `ProviderExtensions["openai"]` bag) and T2 SHALL re-emit `"namespace"` on the upstream `function_call`. The bridge SHALL NOT drop the namespace on either hop — dropping it makes the next turn 400 with `Missing namespace for function_call '<name>'`. A default-namespace tool has no namespace field and its wire bytes SHALL be unchanged. The namespace markers are IR-internal (only T3 emits them) and SHALL NOT appear on the Claude Code (`/cc`) path or reach a Codex client verbatim.

#### Scenario: Streamed namespaced function_call delivers its namespace to Codex

- **WHEN** Copilot streams a `function_call` output item carrying `"namespace":"collaboration"` (e.g. a `list_agents` call)
- **THEN** after the T3→T4 hub round-trip the Codex-facing `function_call` item carries `"namespace":"collaboration"` (so Codex can round-trip it), and the bridge-internal marker never appears on the wire

#### Scenario: Echoed namespaced function_call re-emits its namespace upstream

- **WHEN** a Codex follow-up request echoes a prior namespaced tool call as a `function_call` that includes `"namespace":"collaboration"`
- **THEN** T1→T2 re-emit the upstream `function_call` with `"namespace":"collaboration"` and Copilot accepts it (200) — instead of the previous "Missing namespace for function_call" 400

#### Scenario: Default-namespace tools are byte-identical

- **WHEN** a `function_call` (streamed or echoed) has no `namespace` field (a plain default-namespace or custom/grammar tool)
- **THEN** neither T4 nor T2 emits a `namespace` field — the wire bytes are unchanged from before the fix
