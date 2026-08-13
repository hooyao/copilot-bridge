## Verification

Verified on Windows on 2026-08-13 with real Claude Code 2.1.221 and real Codex
0.147.0-alpha.6.6. Every bridge subprocess used an OS-assigned scratch port;
port 8765 was never bound or terminated.

### Contract and mutation checks

- Targeted config/reader/report/version-probe suites passed after the final
  contract and release-review additions.
- Full unit suite: 1627 tests passed after Copilot review round 1.
- Full solution build: succeeded with 0 warnings and 0 errors.
- Release CLI build: succeeded with 0 warnings and 0 errors.
- `AgentRepositoryCompatibilityTests`: 4 tests passed after updating both
  real-client-verify skill mirrors.
- Manual mutations each reddened the intended contract test before being reverted:
  hidden Claude margin/fixed request value, behavioral timeout drift, omitted retry
  row, `per turn` scope, hidden configured/effective distinction, equal-deadline
  winner selection, either missing global-only heading, and a positive bridge
  timeout getter with `+300s`.
- Release review added mutation-sensitive coverage for current-directory command
  injection, PATH installation precedence, stream-watchdog-independent byte-idle
  inheritance, invalid first-party/stream dependencies, provider-name recognition,
  odd status value types, culture/precision-stable durations, warning key provenance,
  and structural non-8765 port comparison. The final defect-first pass reported
  `No findings`.
- Archive finalization reconciled every ADDED/MODIFIED/REMOVED requirement with
  the four synced main specs, preserved the pre-existing scenarios OpenSpec guards
  against dropping, removed repeated operation headers, and left no requirement
  without a migration scenario. `openspec validate --all --strict` passed all
  31 active changes/specs.
- Copilot review round 1 found two valid edge cases. The version probe now resolves
  the real working directory, rejects aliased/child PATH directories and linked
  executable targets, and remains best-effort when identity cannot be established.
  The Codex merge now refuses inline/dotted auth representations before mutation,
  preserves unmanaged fields in the supported explicit auth table, and reparses
  every planned document before it can be written. Focused contract tests cover
  both regressions. The real startup/config subprocess tests and both real-client
  behavior legs were repeated on the review-fix build.

### Real startup and real config commands

- The final `TimeoutStartupInventoryTests` run started the built bridge on an
  OS-assigned non-8765 port, captured real stderr, and compared the complete dynamic
  inventory byte-for-byte with the approved rendering. The scratch process was
  owned and stopped by `ServeProcess`; the test also included a project
  `.codex/config.toml` with conflicting values and still observed only the labelled
  global baseline.
- The hardened version probe resolved the real native Claude executable from the
  selected PATH installation without invoking a repo-local shell shim and printed
  the source-confirmed 2.1.221 defaults. Relative/current-directory launchers are
  rejected and PATH directory precedence is contract-tested.
- The real process test discovered and fixed Windows console encoding that had
  changed the approved em dash. The child now emits UTF-8 and the harness decodes
  UTF-8 explicitly.
- `ClientConfigProcessTests` drove the built `copilot-bridge.exe` against isolated
  global files. Claude event/byte/request timeout, retry, watchdog, 1M, telemetry,
  fallback, and existing token values survived. Codex idle/retry/WebSocket/query/
  header/rival-provider fields and TOML trivia survived. Both commands created the
  expected backup and changed only connection/auth fields.

### Real Claude Code

Manifest:
`tests/behavior-runs/manifests/cc-to-gpt-stream-fault-recovery-20260813-091340-907.json`

- Startup observed event idle `11s -> effective 5m`, byte idle `13s`, and absent
  request timeout as normal `10m` / after-stream-error `5m`.
- The first streaming request ended at the bridge's exact 1s SSE event-gap budget.
- The next client request was non-streaming (`streaming=False`), proving Claude's
  after-stream-error recovery scope.
- The real stream-json transcript contains Bash `tool_use -> tool_result`, Read
  `tool_use -> tool_result`, a successful final result, and canary
  `cc-stream-recovery-canary-64129`.
- All client-facing response traces were free of
  `bridge_tool_namespace` / `bridge_input_is_grammar_text` markers.

Verdict: PASS from the real Claude transcript plus per-run bridge trace.

### Real Codex

Final manifest:
`tests/behavior-runs/manifests/codex-native-retryable-stream-timeout-20260813-091950-716.json`

- Startup observed Codex idle `5s`, request retries `1`, and stream retries `2`.
- Per-run response sequence was HTTP `500`, then one bridge-generated retryable
  `response.failed` after the exact 1s stream-idle budget, then a completed
  `custom_tool_call`, then final output. This exercised both request retry and
  sampling-stream retry.
- Client-owned SQLite window `[1786612774,1786612790]` contained 150 rows,
  `codex_core::responses_retry` recorded `1/2`, router/dispatch fatal rows were 0,
  and ERROR rows were 0.
- Real app-server stdout recorded two completed command executions, matching
  `custom_tool_call_output` for `call_keepalive_exec`, no aborted execution, a
  completed turn, and canary `codex-stream-retry-canary-47291`.

Verdict: PASS from Codex's own SQLite/stdout plus the authoritative per-run trace.

### Source facts

- Official OpenAI Configuration Reference:
  <https://learn.chatgpt.com/docs/config-file/config-reference>
  (`stream_idle_timeout_ms=5m`, request retries 4, stream retries 5 when absent).
- Official OpenAI Config basics:
  <https://learn.chatgpt.com/docs/config-file/config-basic>
  (configuration precedence and global-baseline limitation).
