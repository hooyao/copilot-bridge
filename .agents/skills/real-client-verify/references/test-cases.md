# Preset test cases

Plain-language prompts + the client-side evidence that decides PASS. **No assertion
DSL** — you (the chat session) run a case and judge the client's own log. Pick a case
by the path your change touches (Gate 1); if none fits, add one that hits your path
rather than reusing a trivial task that doesn't.

Every case names: **route**, **client**, **scenario** (`ServeProcess` appsettings),
**prompt**, and **PASS evidence** (from the client's own log/transcript, per
`evidence.md`). The `Kind=ClientBehavior` tests already implement the flagship cases;
this table is the menu — including cases the CLI can and cannot drive.

Latest ids under test live in `models.md` (`claude-opus-5`, `gpt-5.6-sol` today).

---

## A. Claude Code — native `/cc`

Client `claude.exe`, scenario `Passthrough`, route `/cc`.

| Case | Prompt (essence) | Tools | PASS evidence |
| --- | --- | --- | --- |
| A1 multi-tool chain | write a file, append a canary line via a second Bash call, Read it back, report the exact second line | `Bash,Read` | transcript: turn completed; a `tool_use`→`tool_result` round-trip executed; canary in the final text. Trace: ≥2 `/v1/messages` 2xx with `tool_use` then `tool_result`. |
| A2 parallel tools | "in ONE turn, run `echo a` and `echo b` as separate Bash calls" | `Bash` | transcript shows two tool_use blocks in one assistant turn, both results consumed |
| A3 MCP tool | drive the bundled `mcp-echo-server.py` and echo a canary through it | MCP echo | the MCP tool call executed and its result reached the final answer (see `McpToolUseTests`) |
| A4 1M-context routing | a >600k-token prompt at opus with the 1M beta | none | trace: upstream carried the 1M beta / model that serves it, 2xx; turn completed |
| A5 max-effort cross-field | the A1 task with `CLAUDE_CODE_EFFORT_LEVEL=max` (`ClaudeCode_NativeCc_MaxEffort_DisabledThinkingEffortIsClamped`) | `Bash,Read` | **every upstream 2xx**, and no upstream body pairs `thinking.type=disabled` with effort `xhigh`/`max`. Cross-check an *inbound* body actually carried that pair — otherwise the case never reached the path and is INCONCLUSIVE, not PASS. |
| A6 forced first CAPI 403 | A1 through `ClaudeCode_NativeCc_OneShotCopilot403_RefreshesAndCompletesToolChain` with the Debug seam targeting `/v1/messages` | `Bash,Read` | bridge lifecycle: exactly one injected 403, `copilot_403` lease refresh/reuse, one successful auth replay, no terminal policy classification. Transcript: completed `tool_use`→`tool_result`, canary final, no visible retry exhaustion. |

> **A5 is the worked example of Gate 1.** A1 at default effort does NOT exercise
> the opus-5 disabled-thinking clamp: its trace shows `thinking=disabled` reaching
> the wire only at `effort=high`, a legal combination. Pinning max effort makes the
> real client emit the rejected `disabled`+`max` pair on the no-thinking internal
> requests it issues alongside the main turn. Verified by mutation: with the
> constraint removed from the catalog, that request 400s upstream with Copilot's
> *"effort 'max' is not supported when thinking is disabled"*. When a change adds a
> constraint that only binds on a FIELD COMBINATION, the case must force the
> combination — a default-settings task will silently miss it.

## B. Codex CLI — native `/codex`

Client `codex.exe` (`codex exec`), scenario `Passthrough`, route `/codex`.

| Case | Prompt (essence) | PASS evidence |
| --- | --- | --- |
| B1 multi-step tool chain | two `echo` writes then a `cat` read-back, report the exact second line (`CodexBehaviorTests.Codex_MultiStepToolChain…`) | `logs_2.sqlite`: the shell tool actually ran (output present, not `aborted`); **zero** `[ERROR] codex_core::tools::router` / `incompatible payload`. Canary in stdout. ≥2 upstream `/responses` 2xx with `function_call` + `function_call_output`. |
| B1b code-computation → custom exec | "using your code-execution tool, sum 1..100, append a canary suffix, report it" (`CodexBehaviorTests.Codex_CodeComputation_DrivesCustomExecPath…`) | biases codex toward the **custom `exec` grammar tool** (`custom_tool_call` on the wire) — the exact path the 0.4.13 exec fix guards. PASS: canary in stdout AND **zero** `incompatible payload` in `logs_2.sqlite`. Confirm from the trace that the run actually took `custom_tool_call` (codex still picks its tool per run). |
| B2 second-turn echo | a task that makes codex call a tool, get a result, then reference that prior call on the next turn | `logs_2.sqlite`: no deserialize/echo error on turn 2 (the request-side round-trip); tool ran both turns |
| B3 native-response fidelity | xhigh + detailed reasoning; call namespaced `collaboration.list_agents`, then custom `exec`, then report a canary (`Codex_XhighReasoningAndCustomExec_PreservesNativeResponse_ForVerdict`) | trace MUST show reasoning item/summary events, a namespaced `function_call` + matching output, a `custom_tool_call` + matching output, every round 2xx, and full upstream→inbound event-value equality (phase/ids/usage included, no `bridge_*`). Client stdout: completed + canary + no abort. SQLite: zero router fatals/errors. |
| B4 keepalive exceeds client idle | deterministic upstream emits `response.created`, stays silent past an isolated Codex event-idle timeout, then emits custom `exec` (`Codex_SilentResponsesTurn_SurvivesViaSharedKeepaliveDeadline_ForVerdict`) | trace: injected pings span the silence, then custom call + matching output. Client: tools complete, canary, no abort; SQLite: zero fatal/error. |
| B5 request + stream retry scopes | deterministic upstream returns one HTTP 500, then one stream that bridge ends with retryable `response.failed`, then custom `exec` (`Codex_RetryableBridgeStreamTimeout_UsesConfiguredStreamRetries_ForVerdict`) | trace: `500 → response.failed → custom_tool_call → matching output`; isolated `request_max_retries=1`, `stream_max_retries=2`. SQLite: `responses_retry 1/2`, zero fatal/error. Client commands and turn complete with canary. |
| B6 forced first CAPI 403 | code computation plus three shell commands through `Codex_OneShotCopilot403_RefreshesAndCompletesComplexToolChain_ForVerdict`, with the Debug seam targeting `/responses` | bridge lifecycle: exactly one injected 403, `copilot_403` lease refresh/reuse, one successful auth replay, no terminal policy classification. Trace/stdout/SQLite: custom-exec path when selected plus matching outputs, completed canary, no abort, zero router/dispatch fatal. |
| B7 reasoning usage accounting | two turns through `Codex_ReasoningIncluded_PreventsFalsePostSamplingCompact_ForVerdict`: turn one creates historical encrypted reasoning at low usage; turn two receives a below-limit custom `exec` response whose fallback estimate would cross the limit | deterministic upstream: zero `request_kind=compaction`, tool output echoed. Trace: raw upstream header absent, every successful inbound response has `X-Reasoning-Included=true`, custom call + matching output. Client: canary, no abort; SQLite: zero router/dispatch fatal. |
| B8 migrated version-1 auth | B1 through `Codex_MigratedVersionOne_ExchangeLeaseCompletesToolChain_ForVerdict`, staging only a copied legacy raw mirror | bridge log confirms credential version 1; trace has the complete tool round-trip; stdout has the canary and no abort; SQLite has zero router/dispatch fatal. |
| B9 version-2 direct auth | B1 through `Codex_BuiltInGitHubCliOAuth_DirectLeaseCompletesToolChain_ForVerdict`, staging a validated purpose-built, non-refreshable DPAPI-encrypted unified version-2 credential plus a legacy residual | bridge log confirms residual cleanup and direct credential version 2 with no token exchange; trace has the complete tool round-trip; stdout has the canary and no abort; SQLite has zero router/dispatch fatal. |
| B10 native context recovery | two turns through `Codex_ContextWindow400_TrimsCompactionAndCompletesToolTurn_ForVerdict`: the first pre-turn compact attempt receives Copilot's exact HTTP 400 context rejection | deterministic upstream: one rejection, then a smaller compaction retry, compact summary, custom exec output, and final canary. Trace: raw upstream 400 remains intact; Codex-facing response is one `response.failed` with `context_length_exceeded`. Client: no generic bad-request/abort; SQLite: zero router/dispatch fatal. |
| B11 updater activation auth deferral | B1 through `Codex_UpdaterManagedActivation_DefersAuthAndCompletesToolChain_ForVerdict`, with a fake updater pipe and complete target activation context | fake updater receives a valid target `Ready` before the client starts; bridge startup logs auth/migration deferral, then the first real client request performs residual cleanup + direct version-2 lease resolution. Trace/stdout/SQLite: complete tool round-trip, final canary, no abort, zero router/dispatch fatal. |
| B12 Copilot Plugin version-3 auth | B1 through `Codex_CopilotPluginVersionThree_ExchangeLeaseCompletesToolChain_ForVerdict`, staging a freshly authorized encrypted version-3 credential | startup exchanges the recorded Copilot Plugin credential (`credential_version=3`) for a Copilot bearer; trace has the complete tool round-trip; stdout has the canary and no abort; SQLite has zero router/dispatch fatal. |
| B13 custom OAuth version-4 auth | B1 through `Codex_CustomOAuthVersionFour_DirectLeaseCompletesToolChain_ForVerdict`, with the custom provider enabled and a freshly authorized encrypted version-4 credential | startup publishes the recorded custom OAuth access token directly (`credential_version=4`) with no token exchange and scopes `copilot-developer-cli` to v4 while older versions retain `vscode-chat`; trace has the complete tool round-trip; stdout has the canary and no abort; SQLite has zero router/dispatch fatal. |
| B14 custom OAuth v4 forced first CAPI 403 | B6 through `Codex_CustomOAuthVersionFour_OneShotCopilot403_RotatesAndCompletesComplexToolChain_ForVerdict`, staging a freshly authorized refreshable version-4 credential | bridge lifecycle: one injected 403, a forced version-4 OAuth rotation through its recorded client ID, one successful direct-lease authentication replay, and no terminal policy classification. Trace/stdout/SQLite: matching tool outputs, completed canary, no abort, zero router/dispatch fatal. |

> **codex picks its tool per run — B1b biases, it does not guarantee.** The same task
> can be serviced by a plain `function_call` shell tool (which the exec bug never
> touches) or the `custom_tool_call` grammar tool (which it does). So when verifying the
> exec fix specifically, read the bridge trace to confirm the run took the
> `custom_tool_call` path; a clean log over a `function_call`-only run does not exercise
> the fix. This is a Gate-1 consequence — the case must actually hit the path.

> **CLI vs desktop coverage.** `codex exec` (headless CLI) drives B1, B2, AND B1b — the
> real function-tool loop, the turn-2 echo, AND the custom-`exec` grammar tool (B1b's
> code-computation prompt biases codex toward `custom_tool_call`, confirmed on the trace
> by `Codex_CodeComputation_DrivesCustomExecPath_ForVerdict`). What the headless CLI does
> NOT emit is the desktop Codex app's **multi-agent** shapes: the namespaced-collaboration
> tools (`list_agents`/`spawn_agent`) and multi-agent `agent_message`. **Those are NOT
> unverifiable** — they were reproduced from real
> captured bytes and are guarded directly by the `ApiContract` captured-byte replays
> (`CodexNamespaceEchoHeadlessTests`, `CodexAgentMessageHeadlessTests`,
> `CodexCustomToolEchoHeadlessTests`). When your change touches a multi-agent shape, the
> live gate is the replay (real bytes → `/codex/responses` → assert fixed shape); custom
> `exec` and the function-tool loop are driven live by the CLI behavior cases. Never skip
> a path by calling it "desktop-only / can't be tested" — either drive it headless (exec,
> function tools) or capture its bytes and replay (collaboration, agent_message).

## C. Claude Code → gpt (CC routed to a Codex backend)

Client `claude.exe`, scenario `CcToGpt` (promotes the `claude-opus-5 → gpt-5.6-sol`
location), route `/cc->gpt`. The client speaks `claude-opus-5`; routing rewrites it
to `gpt-5.6-sol` on the `/responses` wire.

| Case | Prompt (essence) | PASS evidence |
| --- | --- | --- |
| C1 multi-tool chain over the route | same A1 task, but through the CC→gpt route | transcript: turn completed, tools executed; trace: upstream is `gpt-5.6-sol` on `/responses`, 2xx, tool round-trip intact |
| C2 marker no-leak | any tool task through the route | **bridge trace**: the client-facing `content_block_start` events must NOT carry `bridge_tool_namespace` or `bridge_input_is_grammar_text`. Those are T3-internal markers `ClaudeCodeOutboundAdapter` scrubs on this route; if they reach the Claude client the scrub regressed. (This is the 0.4.13 leg — verify it here.) |
| C3 multimodal tool result | generate a solid-red PNG, require real Claude Code to `Read` it, then answer with exactly the observed color (`ClaudeCode_RoutedToGpt_ImageToolResult_IsUnderstood`) | transcript: actual `Read` tool_use → image tool_result → completed final answer `red`. Trace: later `function_call_output.output` contains ordered `input_image`, exact source data URL, `copilot-vision-request=true`, every upstream 2xx, and no bridge marker leak. A 200 without the transcript loop/correct color is INCONCLUSIVE. |
| C4 stream timeout → recovery request | deterministic stream stalls until bridge emits the retryable timeout; real Claude must issue a non-streaming recovery and continue Bash + Read (`ClaudeCode_RoutedToGpt_StalledAttempt_RetriesAndExecutesTools`) | startup reports isolated event/byte idle and distinct absent normal/recovery request defaults. Trace: one `upstream_timeout=stream_idle`, then `streaming=False`, tool results, no marker leak. Transcript: Bash/Read results and successful canary final. |

---

## Adding a case

When a change touches a path no case hits: add a `[Fact]` in the matching
`Headless/ClientBehavior/*BehaviorTests.cs`, driving the client on a prompt that
provably reaches the path, and record what the client-side PASS evidence is here.
Keep the prompt plain and bounded ("as soon as step N is done, stop") so the model
converges instead of re-verifying forever.
