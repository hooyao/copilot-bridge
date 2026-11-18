# Non-Claude model reconciliation verification — 2026-11-18

## Scope and outcomes

Live discovery and endpoint probes used the requested installation's existing
custom-OAuth credential through the authentication facade. Tests staged an
encrypted, unexpired v4 copy; the source remained at generation 132. Temporary
credential copies were removed and the playground's original credential files
restored. No credential material or full client traces are committed.

| Exact model | Inference | Codex picker | Accepted effort |
| --- | --- | --- | --- |
| `gpt-6.2-astra` | Real-client PASS | One visible exact-id row | low, medium, high, xhigh, max |
| `gpt-6.2-sol` | Real-client PASS | One visible exact-id row | low, medium, high, xhigh, max |
| `gpt-5.6-luna-utility` | Real-client PASS, explicit id | No official resource; not synthesized | none, low, medium, high, xhigh, max |
| `mai-code-2` | Real-client PASS, explicit id | No official resource; not synthesized | none, low, medium, high, xhigh, max |

All four support function/custom/web-search tools and two-turn structured image
function output. Their existing backend-wide store/service-tier/image-generation
rejections remain. Unsupported efforts fall back to low. Real streaming client
captures confirmed every rejection that causes a rewrite. A separately accepted
capture with web search removed establishes MAI-Code 2's minimal-effort rejection
without allowing search validation to mask the model boundary.

Both GPT-6.2 picker resources are complete, byte-identical official resources
from `openai/codex` commit `9f80bb5957f0d863fdeaaff0ea193cac94c192d1`.
The source file's SHA-256 is
`d50b6749506d97e5f334dbabcb666eb1689f423cf3e88cfabdcf3c536b873068`.
The existing four supplemental resources also retain their original bytes.
Projection filters rejected Ultra from old supplemented and newer same-slug
baselines. Inconsistent live total/prompt/output limits retain official context
values (Astra 272,000; Sol 500,000), maximum context 872,000, and null explicit
compaction. Both resources declare minimum client version 0.166.0.

Exact probes retire `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.5`, `gpt-5.6-terra`, and
`gpt-5-mini`: `copilot-developer-cli` returns
`model_not_available_for_integrator`, while `vscode-chat` returns
`model_not_supported`. The resulting catalog contains 16 exact profiles.
Grok 4.6/4.7 facts are recorded separately without registering routes or applying
Codex compatibility rewrites. Claude models were explicitly excluded.

## Real-client execution verdict

`Codex_November2026Models_MultiToolTask_ProducesDispatchLogForVerdict` ran real
Codex **0.167.0-alpha.4.1** app-server processes through isolated bridge
subprocesses on non-8765 ports. The task created a CSV, read it to calculate
sum/product, edited a summary file, and read both files to verify the result.
All runs completed with sum **143**, product **107113**, and the model-specific
canary. Each trace uses the exact upstream model, with matching calls and outputs
on later requests. No stdout execution-abort signal was present.

| Model | Matched call/output pairs | Tool path | Client log rows read | Router fatals / ERROR rows / retries |
| --- | ---: | --- | ---: | --- |
| `gpt-6.2-astra` | 4 | custom `exec` | 284 | 0 / 0 / 0 |
| `gpt-6.2-sol` | 4 | custom `exec` | 281 | 0 / 0 / 0 |
| `gpt-5.6-luna-utility` | 4 | custom `exec` | 266 | 0 / 0 / 0 |
| `mai-code-2` | 11 | function `exec_command` | 1000 | 0 / 0 / 0 |

MAI-Code 2 initially used malformed shell invocations of the file-editing helper,
consumed the returned errors, corrected the invocation, and completed the task.
Those ordinary tool errors are preserved in the evidence; they were not client
dispatch failures. No client tool emulation or payload stripping was added.

The GPT-6.2 runs each called real `model/list`, observed exactly one visible
exact-id row with low/medium/high/xhigh/max, and then selected that id.
Utility and MAI-Code 2 do not claim picker support.

The private, gitignored evidence is under `tests/behavior-runs/`:

- Manifests: `manifests/codex-november2026-*-20261118-*.json` (four runs).
- Each manifest selects its own `dispatchLogPath`, Unix time window, stdout, and trace directory.
- `model-sync-20261118/*-dispatch.txt` contains the bounded SQLite reader output.
- `model-sync-20261118/client-wire-verdicts.json` summarizes matched calls and picker rows.

The verdict used the manifest-selected client SQLite database and actual tool
outputs, not bridge HTTP status, client exit code, or a canary alone.

## Other checks

- `dotnet build CopilotBridge.slnx`: zero warnings and errors.
- Unit suite: **2012 passed**.
- Full `B_ResponsesContract_SweepAssertAndDetectDrift`: all **16 models** passed
  B1 liveness, B2 snapshot comparison, B3 rewrite-fact comparison, and backend SSE checks.
- Final targeted integration selection: **13 passed** (five retirement probes,
  four real-byte rewrite replays, two Grok backend comparisons, one committed
  B3 comparison, and one captured catalog projection check).
- **12 product mutations** made the intended unit contracts fail: accepted
  efforts, fallback effort, multimodal output, custom tools, minimal/search,
  routing, missing supplement, picker Ultra, context limits, newer-baseline
  precedence, retired profile, and retired route.
- An additional catalog-effort mutation made the offline B3 comparison fail.
  The separate live full sweep provides the backend evidence.
- Official .NET 10 release metadata identifies SDK **10.0.404** as the latest
  stable 10.x SDK (runtime 10.0.15); `global.json` selects that installed SDK.

This verifies the JIT client/model paths. Cross-platform Native AOT compilation
and binary startup remain the PR CI gates; this report does not claim that a
downloaded release binary was tested.

During PR validation, GitHub rejected the retired macos-14 runner configuration
with no build steps executed. CI and release workflows now select the officially
supported macos-15 arm64 image; matching runner documentation is updated.
The repository compatibility checks also passed (4 tests) after the runner
documentation update.

## PR review follow-up

The retirement probe now explicitly selects copilot-developer-cli for its first
request and vscode-chat for its second, independently of credential version.
All five exact-id retirement checks passed again.

The hosted-search relay regression uses an isolated Codex 0.167.0-alpha.4.1
startup catalog derived from the complete official GPT-6.2 Sol resource, changing
only use_responses_lite to false inside that test. The official production
resource remains unchanged. A real Codex run produced upstream web_search_call
and relayed response.web_search_call events; the six selected tests passed.
Restoring Lite=true in the test setup made the hosted-search assertion fail with
no upstream web_search_call, confirming that the test detects the reviewed gap.
This complementary ApiContract CLI test is transport evidence, separate from the
four app-server dispatch verdicts above.
## Operator confirmation after review

The locally retained manifests, nonempty client SQLite windows, exact upstream
models, matching tool outputs, arithmetic results, and visible GPT-6.2 picker
rows were inspected again after review. All four original native/explicit-id
execution verdicts remain PASS; no missing execution evidence was found.
The current code head 24a0b96 passed every required cross-platform Native AOT
compile/startup gate, including macOS 15 arm64, in
[CI run 44236135187](https://github.com/hooyao/copilot-bridge/actions/runs/44236135187).
The restored hosted-search setup and all five retirement checks also passed a
second live run. This records completion of the review overview's execution-
evidence and AOT confirmation reminder; subsequent edits in this section are
documentation only.