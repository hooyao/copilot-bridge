## 1. Approval and Contract Reset

- [x] 1.1 Obtain explicit operator approval of the exact startup rendering in `design.md` before editing product source.
- [x] 1.2 Replace tests that enforce `MarginSeconds`, the disabled-to-30m substitution, fixed `API_TIMEOUT_MS=60m`, or timeout-driven connection drift with contract tests that forbid every implicit client mutation; mutation-check each forbidden transformation.
- [x] 1.3 Add golden rendering tests for the compact Bridge/Claude/Codex inventory, concise human durations, retry counts, scope note, global-only caveat, invalid/unknown states, and default legend.
- [x] 1.4 Mutation-check the report by removing one retry layer, changing `per attempt` to `per turn`, hiding a configured/effective distinction, selecting a winner on an equal unprotected deadline, and omitting either global-only caveat; every mutation must fail.

## 2. Connection-Only Client Configuration

- [x] 2.1 Remove Claude timeout derivation from `BridgeConnection` and make `config claude-code` preserve all timeout, retry, watchdog, 1M/first-party, telemetry, and fallback values while still updating the base URL and filling only an absent required token placeholder.
- [x] 2.2 Refactor `CodexConfigurator` from whole-provider replacement to field-level trivia-preserving upserts for provider identity and command auth, preserving every timeout/retry/transport/query/header field inside the bridge provider.
- [x] 2.3 Update dry-run/apply summaries to enumerate exactly the connection/auth fields changed and explicitly state that behavioral client fields are preserved.
- [x] 2.4 Restrict `config status` drift to bridge-owned connection/auth facts; show user timeout/retry values observationally without an expected derived replacement.
- [x] 2.5 Add byte-preservation, idempotence, no-op, backup, malformed-file, absent-field, explicit-zero, and old-bridge-value migration tests for both client configurators.

## 3. Source-Aware Timeout Readers

- [x] 3.1 Expand the Claude global reader to retain raw stream-idle, byte-idle, request-timeout, relevant watchdog/retry fields, and genuine absence/invalid provenance instead of collapsing them into one value.
- [x] 3.2 Encode only version/source-confirmed Claude defaults, floors, and caps; print configured plus effective human durations, and fall back to version-dependent/unknown when the installed behavior cannot be established.
- [x] 3.3 Expand the Codex global provider reader to retain raw `stream_idle_timeout_ms`, `request_max_retries`, and `stream_max_retries`, applying official defaults only for genuine absence and treating explicit zero exactly.
- [x] 3.4 Keep all client reads best-effort and global-only; an unreadable client must not suppress bridge or sibling-client facts.

## 4. Honest Compact Startup Inventory

- [x] 4.1 Replace `TimeoutBudgetReport` with the approved compact source sections and exact labels: upstream response headers, parsed SSE event gap, after-first-event keepalive, bridge network retry count, and unbounded buffered body.
- [x] 4.2 Render Claude `SSE event idle` plus `SSE byte idle`, Codex `SSE event idle`, Claude request timeout in one plain-language line (`normal` / `after stream error`), and Codex retry counts without total-attempt arithmetic; label unknown Claude retry policy rather than guessing.
- [x] 4.3 Emit only the approved one-line attempt/turn scope note; keep header-plus-first-event, buffering, and retry arithmetic in `docs/timeout-chain.md` rather than startup.
- [x] 4.4 Base keepalive reachability on the actual active detector set, preserve the first-event exclusion, and render equal unprotected deadlines as a race.
- [x] 4.5 Print global-only source paths and the approved Claude repo/process-env plus Codex project/profile/CLI visibility footer on every startup.
- [x] 4.6 Format normal startup durations as concise exact human values; retain raw storage values internally and in detailed diagnostics rather than printing redundant milliseconds.

## 5. Bridge Timeout Validation

- [x] 5.1 Add startup validation for every positive first-byte, stream-idle, and keepalive duration against the real `CancelAfter`/`Task.Delay` representable range.
- [x] 5.2 Keep positive bridge values exact and non-positive values disabled; add mutation tests proving no margin, clamp, or fallback is introduced.
- [x] 5.3 Verify validation fails before the server binds and names the raw option/value and supported range.

## 6. Documentation and Automated Verification

- [x] 6.1 Rewrite `docs/timeout-chain.md`, README timeout/config examples, CLI help, appsettings comments, and durable pipeline documentation to remove the hidden policy and stale 15m/60m and 900s/600s examples.
- [x] 6.2 Document the distinction between upstream headers, first downstream event, streaming event gaps, true buffered bodies, request attempts, sampling attempts, and whole turns.
- [x] 6.3 Document official Codex defaults/precedence with source links and explicitly state that startup is a global baseline, not the future client's definitive effective config.
- [x] 6.4 Run targeted config/timeout/report/keepalive tests, then the full unit suite and solution build; resolve failures without retaining old-policy assertions.
- [x] 6.5 Start a scratch bridge on a non-8765 port, capture its real startup log, compare it byte-for-byte with the approved dynamic layout, and terminate only the confirmed scratch PID.

## 7. Real-Client Acceptance Gates

- [x] 7.1 Run real `config claude-code` and `config codex` against isolated homes containing deliberately distinct behavioral values; prove every timeout/retry/1M/fallback/provider field survives exactly.
- [x] 7.2 Drive real Claude Code through streaming timeout and after-stream-error request cases with distinct stream-idle, byte-idle, normal-request, and retry-request values; confirm behavior/transcript matches the reported scopes.
- [x] 7.3 Drive real Codex through a shortened retryable stream timeout and a complex multi-tool task; confirm the configured request/stream retry behavior from the matching client-owned `logs_2.sqlite` interval.
- [x] 7.4 Add an isolated Codex project override and prove startup still labels/reports only the global baseline without claiming project effectiveness.
- [x] 7.5 Require successful client tool execution and no router, incompatible-payload, dispatch, or aborted-tool fatal; bridge HTTP 200 and unit tests are not acceptance evidence.
- [x] 7.6 Record the real-client evidence, finish the OpenSpec artifacts, and only then hand the approved change to the PR/review/release workflow.

## 8. Release Readiness Review

- [x] 8.1 Remove current-directory command resolution from the Claude version probe while preserving the selected PATH installation and real 2.1.221 detection.
- [x] 8.2 Correct byte-idle inheritance, invalid dependency handling, Codex provider identity recognition, status observations, warning provenance, and culture-invariant exact duration formatting found during release review.
- [x] 8.3 Fix the non-8765 port assertion and reconcile the approved canonical wording across OpenSpec and durable documentation.
- [x] 8.4 Repeat contract tests, real startup, real Claude/Codex client verdicts, and defect-first review until the final review reports `No findings`.
- [x] 8.5 Reconcile every archived delta operation with the synced main specs, remove duplicate operation sections, retain all modified scenarios, and validate the complete OpenSpec set strictly.

## 9. PR Review Follow-ups

- [x] 9.1 Resolve filesystem aliases and reject linked PATH entries/candidates so a symlink or junction cannot route the Claude version probe back into the bridge working tree.
- [x] 9.2 Detect inline/dotted Codex auth representations before mutation, refuse the conflicting rewrite with actionable guidance, and reparse every rendered plan before returning it.
- [x] 9.3 Add mutation-sensitive contract tests, update the synced/archived spec and operator docs, then rerun real startup/config/client verification before the next Copilot round.
