## 1. Timeout derivation seam

- [x] 1.1 Add a pure, dependency-free derivation helper that maps the two `UpstreamTimeoutOptions` budgets to the two Claude Code env values (`budget + 300 s`, clamped to the client's accepted range; a budget `<= 0` yields the clamped maximum). No filesystem, no DI — it must be callable from both the config composition root and the server startup path.
- [x] 1.2 Add a tolerant reader that extracts the two timeout env values from a Claude Code `settings.json` and reports "unknown" for missing / malformed / unreadable / non-bridge-pointed files. Reuse the existing tolerant `AsStringOrNull` semantics; never throw.
- [x] 1.3 Unit-test the derivation from the contract: a raised budget raises the written value; a `<= 0` budget yields the clamped maximum; the result never exceeds the client's hard cap. Mutation-check at least one case (break the clamp, confirm red).
- [x] 1.4 Unit-test the reader against absent / malformed / non-string-typed / valid inputs, asserting it returns "unknown" rather than throwing.

## 2. Claude Code managed env keys

- [x] 2.1 Add `CLAUDE_STREAM_IDLE_TIMEOUT_MS` and `API_TIMEOUT_MS` as force-written managed keys in `ClaudeCodeConfigurator.MergeInto`, sourced from the 1.1 helper, with summary lines for `--dry-run`.
- [x] 2.2 Thread the derived values into `ClaudeCodeConfigurator` — it currently receives only `BridgeConnection`, so it needs the derived pair (or the budgets) supplied without pulling server services into the config graph.
- [x] 2.3 Extend `ConfigState` with expected/current pairs for both keys and include them in `Drifted`.
- [x] 2.4 Report both keys in `ClaudeCodeConfigurator.Read` `Details`, matching the existing `(unset)` formatting.
- [x] 2.5 Confirm `CodexConfigurator` writes neither key (assert, don't assume).
- [x] 2.6 Extend `ClientConfigTests` from the contract: both keys are written; pre-existing values are overwritten; unrelated `env` keys and non-ASCII values survive; a second run is byte-identical; `--dry-run` writes nothing.
- [x] 2.7 Test drift from the contract: a config missing either key, or holding a value the current budgets would not produce, reads as drifted; a matching config does not.

## 3. Remove the coarse HTTP client cap

- [x] 3.1 Change the shared upstream `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan` in `BridgeServiceCollectionExtensions`, with a comment naming the two budgets as the sole bound and pointing at `docs/timeout-chain.md`.
- [x] 3.2 Verify the `auth` and `debug` commands' own short-lived clients keep their 30 s timeouts (they must not inherit the infinite value).
- [x] 3.3 Add a contract test that a buffered (non-streaming) upstream response slower than the former 10-minute cap still completes when the first-byte budget has not elapsed — the regression this change exists to fix. Confirm it fails against the pre-change 10-minute cap.
- [x] 3.4 Re-run `UpstreamTimeoutContractTests` and `CodexUpstreamTimeoutTests`; confirm the first-byte/stream-idle behavior and the `FirstByteCtsLifetimeProbe` lifetime invariant are unaffected.

## 4. Startup effective-timeout report

- [x] 4.1 Emit the report from `BridgeStartupHostedService` after the existing summary lines: each contributing bound, its source (bridge budget vs client env), and the resulting effective end-to-end bound. A disabled budget prints as "no bound" and is excluded from the minimum.
- [x] 4.2 Emit a warning whenever the client would fire first — a stored value shorter than the bridge budget **or** a missing key — naming the effective client bound (stored value, or the known first-party default when absent), the bridge budget it undercuts, and **both** remedies: the `config claude-code` command and the environment variable the operator can set by hand to at least the named value. No warning when values are present and equal-or-greater, or when the settings file itself is unreadable.
- [x] 4.3 State in the report that client-side values take effect on Claude Code's next start.
- [x] 4.4 Confirm a missing / malformed / unreadable client settings file does not fail startup and still produces the report with the client side marked unknown — distinct from a readable bridge-pointed file that merely lacks a key, which must warn.
- [x] 4.5 Test the report from the contract using the existing `RecordingLoggerProvider`: client-outlasts-bridge (no warning), client-undercuts-bridge (warning names the key, both values, and both remedies), **client key absent (warning, not "unknown")**, disabled budget (reported as no bound, not as the minimum), unreadable settings file (report present, no warning).

## 5. Documentation

- [x] 5.1 Document both new managed keys in `appsettings.json` at the `UpstreamTimeout` section: how each is derived, that re-running `config claude-code` is required after changing a budget, and that a `<= 0` budget now means genuinely unbounded (no coarse backstop).
- [x] 5.2 Rework `docs/timeout-chain.md` from an incident write-up into the topic's reference document, since README's new section links here for depth. It must open with the shipped model (bridge owns the budgets; client keys derived to outlast them; what the startup line reports), then keep the measured evidence — the eight-bound table, the lab results, the production cascade — as the grounding beneath it, updated for the removed 10-minute cap. Keep the "Diagnosing a recurrence" section and note the deferred keepalive change as the remaining gap.
- [x] 5.3 Add a dedicated **`## Long-thinking timeouts`** section to `README.md`, placed after `## Configuration (appsettings.json)` and before `## Limitations`. This topic is currently one table row, yet it is the failure users are least able to self-diagnose (the binding bound lives in the *client*, is invisible, and the bridge's own 1M-context key tightens it). The section must state: why a deep-thinking turn goes silent (Copilot sends no keepalive); that the bridge owns the budgets and writes the client keys to outlast them; the one-knob rule (`CLAUDE_STREAM_IDLE_TIMEOUT_MS` lifts both client watchdogs, the byte-level key alone does not); how to read the startup effective-timeout line and the undercut warning; and both remedies (`config claude-code`, or set the env vars by hand). Link `docs/timeout-chain.md` for the measurements.
- [x] 5.4 Update the rest of `README.md` for consistency with 5.3: the sample `settings.json` `env` block (§ "Point Claude Code at the bridge") shows the two new keys; the bullet list under it explains them and notes they take effect on Claude Code's next start; the `Pipeline:UpstreamTimeout` settings-table row cross-links the new section, states that the budgets now drive the client keys, and quotes defaults matching what ships; add a `Limitations` bullet for the restart dependency and for clients the bridge did not configure; add `docs/timeout-chain.md` to `## References`.
- [x] 5.5 Add the side-effect note to `docs/context-window.md`: that document is where `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` is recommended, and it never mentions that asserting first-party also tightens Claude Code's stream-idle bound (300 s → 180 s, measured). Cross-link `docs/timeout-chain.md` so a reader who enables 1M context learns about the timeout consequence at the same place.
- [x] 5.6 Mirror any resulting guidance change in `CLAUDE.md` and `AGENTS.md` only if a project-wide invariant changed; otherwise note explicitly that no constitution edit was needed. **No edit needed** — neither file carries timeout guidance (grep: zero hits), and this change adds no project-wide invariant; the facts live in `docs/timeout-chain.md` and `README.md`.
- [x] 5.7 Re-grep the repo for stale timeout facts before closing the group (`HttpClient.Timeout`, `600000`, `10 minutes`, `240`/`60` budget quotes, and any doc asserting the client is the binding bound), so no file still describes the pre-change chain.

## 6. Real-client verification

- [x] 6.1 Run `dotnet test tests/CopilotBridge.UnitTests` and the solution-wide `--filter "Category!=Integration"` leg; both green.
- [x] 6.2 Run `config claude-code --dry-run` against a real settings file and confirm the planned output carries both keys with the derived values and preserves everything else.
- [x] 6.3 Drive a real `claude.exe` through a real bridge on a deep-thinking `claude-opus-5 effort=max` prompt whose silent phase exceeds the pre-change 180 s client bound; confirm from the **client's own** result that the turn completed, and from the bridge summary that `upstream_timeout=(none)` with no non-streaming fallback. A bridge 200 alone is not the verdict.
- [x] 6.4 Confirm from the bridge summary and the captured trace that the turn completed in one attempt — no `cancelled by client` followed by a `streaming=false` request, which is the signature of the client having aborted first.

## 7. Follow-ups raised during implementation

Both landed in this change rather than a follow-up: the first is a direct
consequence of measuring the real silence, and the second was raised in review of
the `HttpClient.Timeout` edit.

- [x] 7.1 Raise the `StreamIdleTimeoutSeconds` default from 60 to 240. Measured evidence: a real `claude-opus-5` turn at `effort=xhigh` opened a thinking block and then sent nothing for **600 s** (confirmed from the raw upstream capture), so 60 s aborted healthy turns. Updated the code default, `appsettings.json`, `README.md`, `docs/timeout-chain.md`, and `docs/pipeline-design.md` together; the derived client values follow automatically.
- [x] 7.2 Replace the hand-rolled singleton `HttpClient` with `IHttpClientFactory`, one named client per upstream surface (`copilot-anthropic`, `copilot-responses`, `github-auth`) so each gets its own connection pool — they share a host, and this bridge holds connections open for minutes, so a burst on one surface could stall another. Consumers inject the factory and create a client at the send site rather than caching one (a cached client pins a pooled handler and defeats rotation). Auth keeps a finite 30 s timeout; the two model surfaces stay unbounded. Verified against a real socket that handler rotation does **not** abort an in-flight long read, and added `EachUpstreamSurface_GetsItsOwnConnectionPool` + `ModelSurfaces_HaveNoCoarseRequestTimeout_ButAuthDoes`, both mutation-checked.
