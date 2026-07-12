# Preset test cases

Plain-language prompts + the client-side evidence that decides PASS. **No assertion
DSL** — you (the chat session) run a case and judge the client's own log. Pick a case
by the path your change touches (Gate 1); if none fits, add one that hits your path
rather than reusing a trivial task that doesn't.

Every case names: **route**, **client**, **scenario** (`ServeProcess` appsettings),
**prompt**, and **PASS evidence** (from the client's own log/transcript, per
`evidence.md`). The `Kind=ClientBehavior` tests already implement the flagship cases;
this table is the menu — including cases the CLI can and cannot drive.

Latest ids under test live in `models.md` (`claude-opus-4.8`, `gpt-5.6-sol` today).

---

## A. Claude Code — native `/cc`

Client `claude.exe`, scenario `Passthrough`, route `/cc`.

| Case | Prompt (essence) | Tools | PASS evidence |
| --- | --- | --- | --- |
| A1 multi-tool chain | write a file, append a canary line via a second Bash call, Read it back, report the exact second line | `Bash,Read` | transcript: turn completed; a `tool_use`→`tool_result` round-trip executed; canary in the final text. Trace: ≥2 `/v1/messages` 2xx with `tool_use` then `tool_result`. |
| A2 parallel tools | "in ONE turn, run `echo a` and `echo b` as separate Bash calls" | `Bash` | transcript shows two tool_use blocks in one assistant turn, both results consumed |
| A3 MCP tool | drive the bundled `mcp-echo-server.py` and echo a canary through it | MCP echo | the MCP tool call executed and its result reached the final answer (see `McpToolUseTests`) |
| A4 1M-context routing | a >600k-token prompt at opus with the 1M beta | none | trace: upstream carried the 1M beta / model that serves it, 2xx; turn completed |

## B. Codex CLI — native `/codex`

Client `codex.exe` (`codex exec`), scenario `Passthrough`, route `/codex`.

| Case | Prompt (essence) | PASS evidence |
| --- | --- | --- |
| B1 multi-step tool chain | two `echo` writes then a `cat` read-back, report the exact second line (`CodexBehaviorTests.Codex_MultiStepToolChain…`) | `logs_2.sqlite`: the shell tool actually ran (output present, not `aborted`); **zero** `[ERROR] codex_core::tools::router` / `incompatible payload`. Canary in stdout. ≥2 upstream `/responses` 2xx with `function_call` + `function_call_output`. |
| B1b code-computation → custom exec | "using your code-execution tool, sum 1..100, append a canary suffix, report it" (`CodexBehaviorTests.Codex_CodeComputation_DrivesCustomExecPath…`) | biases codex toward the **custom `exec` grammar tool** (`custom_tool_call` on the wire) — the exact path the 0.4.13 exec fix guards. PASS: canary in stdout AND **zero** `incompatible payload` in `logs_2.sqlite`. Confirm from the trace that the run actually took `custom_tool_call` (codex still picks its tool per run). |
| B2 second-turn echo | a task that makes codex call a tool, get a result, then reference that prior call on the next turn | `logs_2.sqlite`: no deserialize/echo error on turn 2 (the request-side round-trip); tool ran both turns |
| B3 custom `exec` (desktop) | a task the desktop app services via its custom grammar `exec` tool | `logs_2.sqlite`: **no** `Fatal error: tool exec invoked with incompatible payload`; exec output present |

> **codex picks its tool per run — B1b biases, it does not guarantee.** The same task
> can be serviced by a plain `function_call` shell tool (which the exec bug never
> touches) or the `custom_tool_call` grammar tool (which it does). So when verifying the
> exec fix specifically, read the bridge trace to confirm the run took the
> `custom_tool_call` path; a clean log over a `function_call`-only run does not exercise
> the fix. This is a Gate-1 consequence — the case must actually hit the path.

> **CLI vs desktop coverage.** `codex exec` (headless CLI) drives B1/B2 — the real
> function-tool loop and the turn-2 echo. The custom-`exec` grammar tool (B3), the
> namespaced-collaboration tools (`list_agents`/`spawn_agent`), and multi-agent
> `agent_message` are emitted by the desktop Codex app's multi-agent mode, which the
> CLI does not drive. **These are NOT unverifiable** — they were all reproduced from
> real captured CLI/desktop bytes and are guarded directly by the `ApiContract`
> captured-byte replays (`CodexNamespaceEchoHeadlessTests`,
> `CodexAgentMessageHeadlessTests`, `CodexCustomToolEchoHeadlessTests`). When your
> change touches those paths, the live gate is the replay (real bytes → `/codex/responses`
> → assert fixed shape); the behavior leg drives what the CLI can drive. Never skip a
> path by calling it "desktop-only / can't be tested" — capture its bytes and replay.

## C. Claude Code → gpt (CC routed to a Codex backend)

Client `claude.exe`, scenario `CcToGpt` (promotes the `claude-opus-4.8 → gpt-5.6-sol`
location), route `/cc->gpt`. The client speaks `claude-opus-4.8`; routing rewrites it
to `gpt-5.6-sol` on the `/responses` wire.

| Case | Prompt (essence) | PASS evidence |
| --- | --- | --- |
| C1 multi-tool chain over the route | same A1 task, but through the CC→gpt route | transcript: turn completed, tools executed; trace: upstream is `gpt-5.6-sol` on `/responses`, 2xx, tool round-trip intact |
| C2 marker no-leak | any tool task through the route | **bridge trace**: the client-facing `content_block_start` events must NOT carry `bridge_tool_namespace` or `bridge_input_is_grammar_text`. Those are T3-internal markers `ClaudeCodeOutboundAdapter` scrubs on this route; if they reach the Claude client the scrub regressed. (This is the 0.4.13 leg — verify it here.) |

---

## Adding a case

When a change touches a path no case hits: add a `[Fact]` in the matching
`Headless/ClientBehavior/*BehaviorTests.cs`, driving the client on a prompt that
provably reaches the path, and record what the client-side PASS evidence is here.
Keep the prompt plain and bounded ("as soon as step N is done, stop") so the model
converges instead of re-verifying forever.
