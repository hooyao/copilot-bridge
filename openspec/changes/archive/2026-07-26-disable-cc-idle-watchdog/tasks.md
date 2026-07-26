# Tasks

## 1. Managed env key: `API_FORCE_IDLE_TIMEOUT`

- [x] 1.1 Add the `ForceIdleTimeoutKey` constant (`API_FORCE_IDLE_TIMEOUT`) and its
      managed value (`"0"`) to `ClaudeCodeConfigurator`, with an XML-doc comment
      explaining WHY (client watchdog active on non-Anthropic providers + Copilot
      sends no SSE ping ⇒ cannot distinguish thinking from a dead stream; the bridge
      already owns this bound and is tunable).
- [x] 1.2 Force-write it in `MergeInto` alongside the 1M-context pair, with a summary
      line for `--dry-run`.
- [x] 1.3 Read it in `Read`, add a detail line, and pass Expected/Current through —
      current value only when pointed at the bridge, matching the existing keys.
- [x] 1.4 Add `ExpectedForceIdleTimeout` / `CurrentForceIdleTimeout` to `ConfigState`,
      include both in `Drifted`, and document them in the record's XML doc.
- [x] 1.5 Update `CodexConfigurator` to pass `null` for the new pair (Codex does not
      manage this key).

## 2. Tests (contract-first — write the assertion from the requirement, not the code)

- [x] 2.1 `config claude-code` writes `env.API_FORCE_IDLE_TIMEOUT == "0"`.
- [x] 2.2 A pre-existing `"1"` is force-written to `"0"` (the overwrite is the point:
      `"1"` is the value that arms the watchdog on every provider).
- [x] 2.3 Unrelated `env` keys survive the write unchanged (surgical-merge guarantee).
- [x] 2.4 `config status` reports DRIFTED when the key is absent, and when it holds a
      value other than `"0"`; not drifted when it holds `"0"` and all other managed
      keys match.
- [x] 2.5 `config codex` writes no `API_FORCE_IDLE_TIMEOUT` key.
- [x] 2.6 Update every existing `ConfigState` construction in the test file for the new
      pair (mechanical; keep existing assertions intact).
- [x] 2.7 **Mutation-check 2.1 and 2.4**: comment out the force-write and confirm both
      go red. A new test that passes on the first run has not been shown to assert
      anything.

## 3. Correct the stale `CLAUDE_STREAM_IDLE_TIMEOUT_MS` references

- [x] 3.1 `Hosting/Options/UpstreamTimeoutOptions.cs` — replace the "below Claude
      Code's 90s watchdog" justification with the real mechanism
      (`API_FORCE_IDLE_TIMEOUT`, 5 min, active on non-Anthropic providers, and now
      disabled by the bridge's own client config). Do NOT change the value.
- [x] 3.2 `appsettings.json` `_StreamIdleTimeoutSeconds` — same correction, matching
      the file's existing comment voice.
- [x] 3.3 `docs/pipeline-design.md:402` — same correction.
- [x] 3.4 Confirm no other occurrence survives outside `openspec/changes/archive/`
      (archived changes are historical records and are left as-written).

## 4. Document the upstream cap

- [x] 4.1 Write `docs/copilot-stream-cap.md`: the four measurements (303.0 / 304.4 /
      305.2 / 305.8s), the clean-EOF termination, the direct-to-Copilot method that
      excludes the bridge, the absence of SSE ping (0/290 bodies), the 0/9,608
      historical responses over 250s, and the `effort=medium` completion at 276.4s
      with a 207s thinking gap.
- [x] 4.2 State plainly that no bridge or client setting extends the cap, and that the
      remedy is lower effort or a smaller prompt.
- [x] 4.3 Include the reproduction (request shape, headers, how to get a Copilot token)
      so the measurement can be re-run when Copilot changes.
- [x] 4.4 Link it from `docs/pipeline-design.md` near the stream-idle discussion.

## 5. Verification

- [x] 5.1 `dotnet test tests/CopilotBridge.UnitTests` green.
- [x] 5.2 `dotnet build` clean (no new warnings).
- [x] 5.3 **Real-client verification** per the project's testing directive — run
      `config claude-code --dry-run` against a real settings file and confirm the
      written key, then drive real `claude.exe` through the bridge on a multi-tool
      task and confirm from the CLIENT's own evidence that the turn completes. The
      config path is what changed, so the run must exercise a client actually reading
      the written settings.
- [x] 5.4 Confirm the change is inert for Codex: `config codex` output unchanged.
