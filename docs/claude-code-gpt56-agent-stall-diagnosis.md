# Diagnosis: Claude Code agent stalls in a mixed gpt-5.6 / native-Claude session

**Status:** root-caused at the observable protocol boundary; no product-code
change is made by this document  
**Incident date:** 2026-07-13  
**Client:** Claude Code 2.1.206  
**Claude Code session ID:** `bf116d2a-d330-4b1c-b9ef-fb628be0e2f1`  
**Main route:** Claude Code `claude-opus-4-8` -> bridge `/cc/v1/messages` ->
Copilot Responses `gpt-5.6-sol`  
**Background-agent route:** Claude Code `claude-sonnet-5` -> bridge
`/cc/v1/messages` -> Copilot native Anthropic `/v1/messages`

## Summary

The user-visible incident combines two independent liveness failures:

1. **The main gpt-5.6 agent entered a semantic task-state loop.** The bridge and
   upstream completed every relevant request normally, but gpt-5.6 repeatedly
   read task 5 after announcing that the work was complete and never called
   `TaskUpdate(..., status="completed")`. The last request was cancelled by the
   client. The same Claude Code session switched back to native Opus immediately
   afterward, and Opus completed the missing state transition on its first turn.

2. **A background Plan agent repeatedly stalled on native Claude Sonnet.** Four
   `claude-sonnet-5` requests emitted only `message_start` plus an empty thinking
   block start, then produced no SSE event for 120 seconds. The bridge correctly
   recorded a `stream_idle` timeout each time. This is a real upstream stream
   stall on the native Anthropic route, not a gpt-5.6 Responses stall.

The apparent "agent message leak" is related context, but it is not a Responses
`agent_message` item leaking through the bridge. Claude Code deliberately injects
background-agent completion notifications as user-role meta text containing a
`<task-notification>` envelope. Once Claude Code serializes that text into an
Anthropic request, the bridge has no provenance field that distinguishes it from
ordinary user text, so the Claude-to-Responses builder correctly emits it as a
normal `message/input_text`. The investigated bridge responses do not echo this
envelope back to the client.

## Where the evidence lives

These files are local production evidence and are intentionally outside the Git
repository.

### Bridge text log

```text
C:\Users\yahu2\Desktop\copilot-bridge\log\bridge-20260713-030912.log
```

This is the first place to look. Use request IDs from this log to select exact
trace files; do not search the entire trace corpus without an ID.

```powershell
$log = 'C:\Users\yahu2\Desktop\copilot-bridge\log\bridge-20260713-030912.log'

rg -n '20260713-054103-0340|20260713-054159-0341|20260713-054229-0342|20260713-054250-0343|20260713-054301-0344' $log

rg -n '20260713-050243-0282|20260713-051557-0293|20260713-052059-0296|20260713-052610-0303' $log
```

The timestamp encoded in a request ID is UTC. The text log timestamps in this
incident are local Asia/Singapore time, eight hours ahead. For example,
`20260713-054250-0343` appears in the log at `13:42:50`.

### Per-request bridge traces

```text
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\
```

For each request ID, the suffixes mean:

| Suffix | Content |
|---|---|
| `inbound-req.json` | Exact Claude Code -> bridge Anthropic request |
| `upstream-req.json` | Exact bridge -> Copilot request after routing/translation |
| `upstream-resp.json` | Raw Copilot response body/SSE captured by the bridge |
| `inbound-resp.json` | Claude-facing status and captured Anthropic SSE events |

Example:

```powershell
$traces = 'C:\Users\yahu2\Desktop\copilot-bridge\request-traces'
$id = '20260713-054250-0343'

Get-ChildItem -LiteralPath $traces -Filter "$id*"
Get-Content -Raw -LiteralPath (Join-Path $traces "$id-inbound-req.json")
Get-Content -Raw -LiteralPath (Join-Path $traces "$id-upstream-req.json")
Get-Content -Raw -LiteralPath (Join-Path $traces "$id-upstream-resp.json")
Get-Content -Raw -LiteralPath (Join-Path $traces "$id-inbound-resp.json")
```

### Claude Code client transcript

```text
C:\Users\yahu2\.claude\projects\Q--MyProjects-cc-copilot-bridge\bf116d2a-d330-4b1c-b9ef-fb628be0e2f1.jsonl
```

This is the authoritative client-side record for what Claude Code persisted,
which tools it dispatched, whether a tool actually completed, and what happened
after the bridge requests ended. A bridge `200` is not sufficient evidence.

Useful searches:

```powershell
$transcript = 'C:\Users\yahu2\.claude\projects\Q--MyProjects-cc-copilot-bridge\bf116d2a-d330-4b1c-b9ef-fb628be0e2f1.jsonl'

rg -n 'call_dRTqxRiuuJ5BaHMHc2iwjMMH|toolu_01PbTmSbHqvU5JqYTGMALyg6' $transcript
rg -n 'SYSTEM NOTIFICATION - NOT USER INPUT|<task-notification>' $transcript
rg -n 'TaskUpdate|TaskGet|Plan and validate implementation' $transcript
```

### Background-agent output files

The main background review involved these Claude Code task files:

```text
C:\Users\yahu2\AppData\Local\Temp\claude\Q--MyProjects-cc-copilot-bridge\16ffe8da-7425-4c85-9666-f14c1956425e\tasks\a71c47aee6016d581.output

C:\Users\yahu2\AppData\Local\Temp\claude\Q--MyProjects-cc-copilot-bridge\16ffe8da-7425-4c85-9666-f14c1956425e\tasks\a903f26492ccb5a78.output
```

These are temporary client artifacts and may disappear. Prefer the durable
Claude transcript and bridge request traces when preserving evidence.

## Problem 1: main gpt-5.6 agent fails to close the task state

### User-visible behavior

The main agent repeatedly printed variants of "finishing the final review" and
continued making read/edit/validation calls even after the OpenSpec artifacts
were complete and strict validation had passed. Near the end it announced that
the work was complete, but it did not finish the Claude Code task or produce a
final answer. It eventually appeared stuck and was cancelled.

### Exact request sequence

All of these requests route `claude-opus-4-8` to `gpt-5.6-sol` through Copilot
Responses:

| Request ID | Claude-facing behavior | Terminal |
|---|---|---|
| `20260713-054003-0337` | Text plus one `Edit` | normal `tool_use` |
| `20260713-054030-0338` | Text plus one `Edit` | normal `tool_use` |
| `20260713-054048-0339` | Text plus three `Read` calls | normal `tool_use` |
| `20260713-054103-0340` | Text plus nine `Edit` calls | normal `tool_use` |
| `20260713-054159-0341` | Text plus two `Bash` calls and `Grep` | normal `tool_use` |
| `20260713-054229-0342` | Says OpenSpec is complete; calls `TaskGet`, `Glob`, `Bash` | normal `tool_use` |
| `20260713-054250-0343` | No text; calls only `TaskGet(5)` | normal `tool_use` |
| `20260713-054301-0344` | Starts the next turn, then the client cancels before upstream starts | client cancellation |

The strongest evidence is in:

```text
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-054250-0343-inbound-req.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-054250-0343-upstream-req.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-054250-0343-upstream-resp.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-054250-0343-inbound-resp.json
```

### What request 0343 proves

The inbound Claude request is approximately 1.14 MB and contains only five
top-level messages because Claude Code has accumulated the turn as large block
arrays:

- the assistant history contains 186 `tool_use` blocks;
- the user history contains 186 matching `tool_result` blocks;
- all call IDs are paired;
- the translated Responses request contains 377 input items:
  - 186 `function_call`;
  - 186 `function_call_output`;
  - 5 `message`;
- upstream reports 231,731 input tokens;
- the only newly generated action is `TaskGet({"taskId":"5"})`;
- the tool result still says task 5 is `in_progress`;
- the model never emits `TaskUpdate({"taskId":"5","status":"completed"})`.

The bridge summary is clean:

```text
status=200
streaming=true
response_leak=false
runaway=false
tool_input_invalid=false
upstream_timeout=(none)
error=(none)
```

Request `0344` is not an upstream timeout. The log explicitly says
`endpoint cancelled by client` after 5.7 seconds.

### Native Opus counterfactual

After the gpt-5.6 turn was cancelled, the same Claude Code transcript switched
back to native `claude-opus-4-8`. Opus immediately emitted:

```json
{
  "name": "TaskUpdate",
  "input": { "taskId": "5", "status": "completed" }
}
```

The client transcript records the successful state change from `in_progress` to
`completed`. This is strong evidence that the task itself and Claude Code's task
tool were healthy. The missing state transition is specific to how gpt-5.6 drove
the Claude Code harness in this large mixed-control transcript.

### Causal assessment

At the observable protocol boundary, this is a **model/harness semantic liveness
failure**, not a transport failure:

- Copilot completed each Responses stream;
- the bridge produced valid Anthropic event sequences;
- tool names, arguments, IDs, and results survived the translation;
- Claude Code executed every emitted tool call and correctly continued after
  each `stop_reason: "tool_use"`;
- gpt-5.6 kept choosing a read-only status action instead of the required state
  transition.

The oversized transcript and repeated control text are likely amplifiers. By the
final turn, the history contains many near-duplicate progress messages, repeated
task reminders showing task 5 as `in_progress`, and completed background-agent
notifications. This creates a bad fixed point: the model sees an unfinished task,
calls `TaskGet`, receives the same unfinished state, and repeats without taking
the mutating action that would break the loop.

### The task-notification finding

The following text is present in the **inbound request**, starting around line
3091 of `20260713-054250-0343-inbound-req.json`:

```text
[SYSTEM NOTIFICATION - NOT USER INPUT]
...
<task-notification>
...
</task-notification>
```

This is produced by Claude Code, not the bridge. The reconstructed Claude Code
source confirms the behavior:

- `Q:\MyProjects\claude-code-sourcemap\restored-src\src\utils\task\framework.ts`
  enqueues completed tasks using `mode: 'task-notification'`;
- `Q:\MyProjects\claude-code-sourcemap\restored-src\src\coordinator\coordinatorMode.ts`
  explicitly says worker results arrive as user-role messages containing
  `<task-notification>` XML;
- `Q:\MyProjects\claude-code-sourcemap\restored-src\src\utils\messages.ts`
  marks the queued command as meta based on `commandMode`.

The Claude-to-Responses bridge sees an ordinary Anthropic user text block and
maps it to `input_text`, as required by the current wire contract. There is no
Responses `agent_message` input item in request `0343`.

The four downstream responses `0340` through `0343` contain no
`<task-notification>`, `SYSTEM NOTIFICATION`, `agent_message`, or
`teammate-message` text. Their summaries all report `response_leak=false`.
Therefore:

- **confirmed:** an internal Claude Code control envelope is exposed to gpt-5.6
  as user-role model input;
- **not confirmed:** the bridge emitted that control envelope as assistant text
  back to the Claude Code UI;
- **not present:** a Responses-protocol `agent_message` item leaked through the
  Claude-facing response.

If the raw XML was visibly rendered in the TUI, the client transcript/UI entry
for that exact display needs to be captured separately. The bridge traces for
the investigated final requests do not contain such an output.

### What is not the cause

- `poisoned_tool_results=1` is not the liveness cause. The sole failed result is
  an earlier synchronous `Agent` call interrupted by the user:
  `[Request interrupted by user for tool use]`. It is not a repeated API-error
  loop and remains below the configured warning threshold.
- There is no missing call ID or mismatched tool result in the final history.
- There is no Responses stream idle timeout in requests `0337`-`0343`.
- There is no response-leak detector hit.
- Request `0344` is a client cancellation, not a bridge-generated 500 failure.

### Reproduction/acceptance test needed

A useful real-client behavior test must force the exact semantic path, not only
run shell tools:

1. run real `claude.exe` through a non-8765 bridge;
2. route the main `claude-opus-4-8` request to `gpt-5.6-sol`;
3. create a Claude Code task and mark it `in_progress`;
4. launch a background Agent and wait for its `<task-notification>`;
5. require the main agent to consume the result, mark the task `completed`, and
   produce a final user answer;
6. verify from the Claude transcript that `TaskUpdate(...completed)` executed;
7. fail if the same read-only task query and equivalent result repeat without a
   progress-producing action.

A normal multi-tool smoke that never exercises Agent, task notification, and
task-state mutation does not cover this failure.

## Problem 2: native Sonnet background agent repeatedly goes stream-idle

### Why this is a separate route

The main Claude Code session was routed to gpt-5.6 only when its requested model
was `claude-opus-4-8`. The background Plan agent was explicitly launched with a
Sonnet model. Its requests therefore selected:

```text
requested=claude-sonnet-5
resolved=claude-sonnet-5
target=CopilotAnthropic:/v1/messages
```

These requests bypass the Responses translation path entirely. A stall here
cannot be attributed to gpt-5.6 or Responses T3.

### Exact stalled requests

| Request ID | Message count | Upstream events before silence | Outcome |
|---|---:|---|---|
| `20260713-050243-0282` | 95 | `message_start`, empty thinking `content_block_start` | 120 s `stream_idle` |
| `20260713-051557-0293` | 116 | `message_start`, empty thinking `content_block_start` | 120 s `stream_idle` |
| `20260713-052059-0296` | 120 | `message_start`, empty thinking `content_block_start` | 120 s `stream_idle` |
| `20260713-052610-0303` | 131 | `message_start`, empty thinking `content_block_start` | 120 s `stream_idle` |

For each request, inspect the four matching trace files. For example:

```text
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-050243-0282-inbound-req.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-050243-0282-upstream-req.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-050243-0282-upstream-resp.json
C:\Users\yahu2\Desktop\copilot-bridge\request-traces\20260713-050243-0282-inbound-resp.json
```

The first captured downstream event is a valid Anthropic `message_start`; the
second opens a thinking block with empty content/signature. There is no captured
thinking delta, `content_block_stop`, `message_delta`, or `message_stop` before
the upstream goes silent.

The text log is the authoritative timeout record:

```text
endpoint upstream-timeout: phase=stream_idle idle=120s
summary ... upstream_timeout=stream_idle ...
error=upstream stream_idle timeout after 120s of inactivity
```

### Causal assessment

This is an **upstream native-Claude stream stall**:

- Copilot accepted each request and returned HTTP 200 SSE headers;
- Copilot began an Anthropic message;
- Copilot stopped emitting events after opening an empty thinking block;
- the bridge's independent inactivity timer fired after 120 seconds;
- repeated attempts grew the replayed transcript from 95 to 131 messages,
  increasing cost without producing an agent result.

The background agent was not permanently lost: Claude Code retried/resumed it
and eventually delivered a result. It nevertheless appeared stuck for a long
period and incurred four full stream-idle waits. The behavioral bug is lack of
prompt, bounded recovery from repeated half-started native Claude streams.

### Trace-observability caveat

The deployed binary's `inbound-resp` trace records the two pipeline-relayed
events but does not record the retryable `event:error` appended later by the
endpoint timeout catch. The text log records the timeout correctly. The change
in PR #43 adds endpoint-generated error events to trace capture, so post-fix
evidence should contain the complete Claude-facing failure frame.

Do not infer from the two-event `inbound-resp` trace alone that no error frame was
attempted on the actual socket. Check the text log and the Claude client
transcript together.

### Reproduction/acceptance test needed

The real-client test must exercise the background-agent path:

1. drive real Claude Code through a non-8765 bridge;
2. launch an Agent whose requested model selects native `claude-sonnet-5`;
3. use a task large enough to emit thinking and tools;
4. inject or reproduce an upstream stall immediately after
   `message_start/content_block_start`;
5. prove from the Claude transcript that the failed partial turn is not accepted
   as a completed agent result;
6. prove that retry/non-streaming fallback is bounded and the background agent
   either completes or reports a terminal error to the main agent;
7. inspect the exact request-ID trace to require one retryable `event:error`, no
   synthetic normal `message_stop`, and accurate `upstream_timeout=stream_idle`.

## Relationship to PR #43 (`fix-cc-responses-stream-fault`)

PR #43 fixes a related but distinct production failure in which a Responses T3
stream fault was swallowed and converted into a private normal-looking
`stop_reason: "error"` plus `message_stop`. It also keeps Claude Code's
non-streaming recovery enabled and improves endpoint error trace capture.

Effect on these two problems:

- **Problem 1 is not fixed by PR #43.** Requests `0337`-`0343` contain complete,
  successful Responses terminals. There is no stream fault for PR #43 to
  reframe. This needs a dedicated Agent/task-notification/task-state liveness
  behavior test and potentially a separate mitigation.
- **Problem 2 shares the `/cc` recovery boundary but has a different origin.**
  Native Anthropic passthrough already propagates its `StreamIdle` exception to
  the Claude endpoint. PR #43 improves recovery configuration and trace
  observability, but it cannot prevent Copilot's native Sonnet backend from
  becoming idle. It still needs a real background-agent verification run on the
  PR binary.

## Candidate mitigations and open questions

### Main gpt-5.6 task-state loop

- Add a client-behavior scenario that requires Agent completion notification,
  task mutation, and final answer rather than only tool execution.
- Record a semantic no-progress fingerprint: repeated equivalent read-only tool
  call plus equivalent result across consecutive turns. Initially observe only;
  an automatic abort/retry policy requires its own contract because legitimate
  polling exists.
- Measure transcript structure, not just token count: repeated task reminders,
  duplicate progress commentary, tool-call/result count, and internal meta
  envelopes.
- Determine whether Claude Code can preserve notification provenance in a field
  rather than flattening it into user text. The bridge cannot reconstruct
  provenance that is absent from the Anthropic request.
- Do not heuristically strip `<task-notification>` text without a protocol
  contract. Users can legitimately discuss the same XML, and removing a real
  notification can make the agent miss a completed worker result.

### Native Sonnet background-agent stalls

- Verify the post-PR Claude-facing `event:error` and non-streaming fallback using
  the real client transcript, not only a bridge 200 or unit test.
- Track retries by Claude session, background-agent task ID, requested model, and
  timeout phase so four attempts are visible as one liveness incident.
- Consider a bounded retry budget surfaced to the main agent. Infinite or
  opaque background retry makes the main agent wait on work that may never
  finish.
- Keep timeout tuning separate from correctness. Raising 120 seconds may reduce
  false positives on slow reasoning but would make this exact zero-progress
  stall more expensive.

## Minimal incident checklist

When this symptom happens again:

1. Find the final `summary messages` entries in the bridge text log.
2. Copy the exact request IDs; note requested/resolved model and route target.
3. Open only those four trace files per request.
4. Inspect upstream terminal events and downstream `stop_reason`.
5. Inspect the Claude Code transcript for actual tool dispatch and task state.
6. Separate main-model requests from Agent model overrides.
7. Search for `task-notification` in the inbound request and in the downstream
   response independently; input-side control context is not output leakage.
8. Treat `status=200` as transport evidence only, never client-success evidence.

