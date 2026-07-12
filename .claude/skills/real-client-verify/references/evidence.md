# Reading the client's own evidence

The verdict never comes from the bridge's status code. It comes from what the client
recorded about whether it could parse and execute the bridge's response. This file is
how to read each source and what signatures decide PASS / FAIL.

## The run manifest (the seam)

`ServeProcess` + `BehaviorRun` write one manifest per run under
`tests/behavior-runs/manifests/<caseId>-<stamp>.json`. Fields:

- `client` — `codex` | `claude`
- `route` — `/codex` | `/cc` | `/cc->gpt`
- `model`, `scenario`, `clientExitCode`, `durationSeconds`, `prompt`
- `traceDir` — the bridge's four-file audit for this run
- `dispatchLogPath` / `dispatchSinceUnix` / `dispatchUntilUnix` — codex only: the codex
  dispatch DB and the Unix-second lower AND upper bounds to window it to THIS run. **The
  path is the real `~/.codex/logs_2.sqlite`, NOT anything under `CODEX_HOME`**: codex
  writes its dispatch log to the real user home regardless of the `CODEX_HOME` override
  (an isolated home's `logs_2.sqlite` stays empty). The DB is long-lived and holds every
  past session, so BOTH bounds isolate this run's rows — a start-only window would sweep
  in rows from later runs (or a concurrent desktop codex) and could misattribute a later
  fatal to this case.
- `stdoutPath` / `stderrPath` — the saved client stdout/stderr (claude transcript /
  codex JSONL)

Read the manifest first; it tells you exactly which files to open.

## Codex — `logs_2.sqlite`

The dispatch log at **`~/.codex/logs_2.sqlite`** (the real user Codex home — codex logs
there even under an isolated `CODEX_HOME`). **This is the only place the tool-router
fatal is recorded** — the bridge stays 200. Schema (`table logs`): `ts` (Unix seconds),
`level`, `target`, `feedback_log_body` (the message + otel span; the human message is at
the END, e.g. `… error=Fatal error: tool exec invoked with incompatible payload`).

Read it with the bundled reader — it snapshots the live DB via SQLite's **online backup
API** (a transactionally-consistent, WAL-aware copy in one call, so it never contends
with a live codex and can't miss WAL-resident rows the way a naive file-by-file main+`-wal`
copy can), then queries the snapshot:

```powershell
dotnet run .claude/skills/real-client-verify/scripts/read-codex-log.cs -- "<dispatchLogPath>" <dispatchSinceUnix> <dispatchUntilUnix> "<out.txt>"
```

`<dispatchSinceUnix>` / `<dispatchUntilUnix>` = the manifest's window bounds, so you only
see THIS run's rows — the real `~/.codex` is long-lived and holds every past session, and
without the upper bound a later run's fatal could be misattributed to this one.
Then Read `<out.txt>`. It has three sections: router/dispatch fatals, all ERROR rows,
and a recent tail — plus a summary with the fatal count.

**Codex PASS requires ALL of:**
- **the canary is present in the client stdout** — the value the task could only produce
  by actually running the tool. **Canary ABSENT = FAIL**, full stop: the tool did not
  execute, whatever the log says. (Proven: a mutated exec fix made codex abort the
  custom-`exec` call — stdout showed *"execution was aborted"* and the canary was
  missing — while the bridge stayed 200 AND the sqlite log had zero router-ERROR rows.
  The canary/stdout is the load-bearing signal; the log fatal is corroborating.)
- **no execution-abort in stdout** — codex prints *"execution was aborted"* /
  *"the shell tool aborted"* when it could not run the tool the bridge sent. Any such
  line = FAIL.
- `router/dispatch-fatal rows: 0` in the log summary — no `[ERROR] codex_core::tools::router`,
  no `incompatible payload`, no `Missing namespace`, no `Polymorphism_`. (A fatal here is
  conclusive FAIL, but its ABSENCE is not sufficient — see the canary rule above; some
  abort shapes surface only in stdout, not as a router-ERROR row.)

Any fatal row, a missing canary, or a stdout abort = **FAIL**, regardless of the
bridge's 200 and regardless of exit code.

> **Empty window ≠ PASS.** If the reader finds essentially no rows in the window (the
> tail is empty), the verdict is **INCONCLUSIVE, not clean** — the run may not have
> logged where you looked, or the window is wrong. Cross-check with the stdout canary
> and the bridge trace's tool round-trip before trusting a zero-fatal count; a zero over
> an empty DB proves nothing.

> **The mutation isn't always triggered.** The custom-`exec` fatal only fires when
> Copilot returns a `custom_tool_call` (the grammar `exec` tool) — codex sometimes
> services the same task with a plain `function_call` shell tool, which the bug doesn't
> touch. Confirm from the bridge trace which path ran (`custom_tool_call` vs
> `function_call` in the upstream `input[]`); if you're verifying the exec fix
> specifically, require a run that actually took the `custom_tool_call` path.

## Claude Code — the transcript

The saved stdout (`--output-format json`) is the turn transcript. Plus the bridge trace
for wire confirmation.

**Claude PASS requires:**
- the turn **completed** (a final assistant message, not an error/cutoff), and
- the tool calls **executed**: a `tool_use` block was followed by a `tool_result` the
  model consumed on a later turn, and
- the canary is in the final answer.

A streamed 200 with no `tool_result` consumed = the tool did not close the loop = not a
pass.

## The bridge trace (four-file audit)

`BridgeLogReader` reassembles each request's `inbound-req` / `inbound-resp` /
`upstream-req` / `upstream-resp` (shared `seq`) into one entry. Use it for **wire
confirmation**, never as the sole verdict:

- the request reached the intended model on the intended endpoint (e.g. CC→gpt: upstream
  `model=gpt-5.6-sol` on `/responses`),
- the tool round-trip is present on the wire (`function_call`+`function_call_output`, or
  `tool_use`+`tool_result`),
- **CC→gpt marker no-leak (C2):** the client-facing `inbound-resp` events'
  `content_block_start` must NOT contain `bridge_tool_namespace` or
  `bridge_input_is_grammar_text`. Present → the `ClaudeCodeOutboundAdapter` scrub
  regressed and markers leaked to the client.

## Failure signatures cheat-sheet

| Signature (where) | Meaning |
| --- | --- |
| `Fatal error: tool exec invoked with incompatible payload` (logs_2.sqlite, ERROR router) | exec sent as the wrong tool-payload kind (`function_call` where `custom_tool_call` is required) |
| `Missing namespace for function_call '<name>'` (client body / logs) | a namespaced collaboration tool lost its `namespace` on round-trip |
| `Polymorphism_UnrecognizedTypeDiscriminator, <type>` (bridge inbound 400) | the bridge's closed `input[]` whitelist rejected an item type it should pass through opaquely |
| tool span `aborted=true`, args byte-complete, upstream 200 | the client's OWN runtime aborted (e.g. broken local JS isolate) — restart the client; not a bridge regression |
| bridge trace shows markers in client-facing events | the CC→gpt marker scrub regressed |
