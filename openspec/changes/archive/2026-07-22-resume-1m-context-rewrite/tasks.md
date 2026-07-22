## 1. Revert the dead `[1m]` approach

- [x] 1.1 `git checkout --` the 8 modified M1 files (ModelProfile,
      ModelProfileCatalog, BridgeContext, ModelRouterStage, ModelRewriteDetector,
      ResponseModelRewriteOptions, appsettings.json, ResponseModelRewriteStageTests)
      back to HEAD.
- [x] 1.2 Delete the two new M1 test files (ModelProfileNativeOneMillionTests,
      ResponseModelRewrite1mStageTests) and the stray `x.txt`.
- [x] 1.3 Confirm the working tree has no `[1m]`-suffix source changes and the
      model-rewrite regression suite is back to its shipped state.

## 2. Configurator: write the two 1M-context env keys

- [x] 2.1 In `ClaudeCodeConfigurator`, add two key consts next to the existing
      ones: `Assume1mKey = "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"` and
      `DisableErrorReportingKey = "DISABLE_ERROR_REPORTING"`, each with managed
      value `"1"`. Document *why* in a comment (native-1M capability gate needs
      first-party asserted for a custom base URL; disable-error-reporting
      neutralizes the telemetry side effect).
- [x] 2.2 In `MergeInto`, force-write both keys (mirror the `BaseUrlKey` block),
      each with a summary line for `--dry-run`.
- [x] 2.3 In `Read`, read both current values and add a details line for each
      (like `FallbackKey`), so `config status` shows them.

## 3. ConfigState drift

- [x] 3.1 Add explicit expected/current fields to `ConfigState` for both keys
      (`ExpectedAssume1m`/`CurrentAssume1m`,
      `ExpectedDisableErrorReporting`/`CurrentDisableErrorReporting`), matching the
      existing `ExpectedFallback`/`CurrentFallback` style.
- [x] 3.2 Extend `Drifted` to include the two new pairs in its equality chain.
- [x] 3.3 In `ClaudeCodeConfigurator.Read`, populate the new expected fields with
      the managed `"1"` values and current fields from the file (null when the
      config is not pointed at the bridge, so a non-bridge config never counts as
      drift). Codex's `Read` passes null for all four (no drift).

## 4. Contract tests (from the spec, mutation-checked)

- [x] 4.1 `BuildContent` writes both env keys = `"1"` on a fresh config.
- [x] 4.2 Force-write: a config pre-setting either key to a different value is
      overwritten to `"1"`.
- [x] 4.3 Preservation: an unrelated `env` key survives alongside the managed keys
      (extend/confirm the existing preservation test).
- [x] 4.4 Idempotence: `BuildContent(BuildContent(null))` is byte-stable
      (extend the existing idempotence test to include the new keys).
- [x] 4.5 Drift: a bridge-pointed `ConfigState` missing either key → `Drifted`;
      both present and `"1"` → not drifted; Codex-like null-all → not drifted.
- [x] 4.6 Codex negative: `CodexConfigurator.BuildContent` output contains neither
      key.
- [x] 4.7 Mutation-check each new assertion: break the const / a `Drifted` term
      and confirm the corresponding test goes red.

## 5. Docs

- [x] 5.1 Rewrite `docs/context-window.md` §5 from "the resume caveat (deliberately
      not fixed)" to "Restoring 1M after resume": the 2.1.216 capability-table +
      `firstParty && Wd()` gate, the env fix (`config` writes both keys), the
      side-effect note (what flips, why error-reporting is auto-disabled, inference
      traffic still goes to the bridge), and that the `[1m]`-injection idea was
      tried and empirically failed on 2.1.216.
- [x] 5.2 Reconcile §1/§4 statements that say "the bridge cannot change the
      window" — clarify the *response* can't, but a *client-config* env var can.
- [x] 5.3 Cross-check README.md / docs/routing.md for a "config writes these env
      keys" enumeration and add the two keys if present.

## 6. Real-client resume verification (per the 🔴 headless-client directive)

- [x] 6.1 Run `config claude-code --dry-run` and confirm the summary lists the two
      new `set env.…` lines; run a real `config` into a temp scope and diff the
      written `settings.json` (both keys = `"1"`, base URL set, unrelated keys kept).
- [x] 6.2 Drive real `claude.exe --print --output-format json --model
      claude-opus-4-8` through a bridge on a non-8765 port, using a `--settings`
      file (to bypass the user's own `~/.claude/settings.json` base-URL override)
      that carries the config-written env keys → assert
      `modelUsage[claude-opus-4-8].contextWindow == 1000000`.
- [x] 6.3 `--resume` that session with no `--model` → assert 1,000,000 (the bug
      target); a control run without the env → 200,000 (proves the lever).
- [x] 6.4 A real tool task (Write a canary file + Read it) executes through the
      env-configured client (file appears, exit 0), proving the env doesn't break
      tools (tool-search stays off).
- [x] 6.5 Cleanup: stop the test bridge (preserve the user's 8765), remove temp
      files, delete `~/cc_wd_analysis.txt`.
