## Why

For copilot-bridge users, Claude Code reverts to a 200,000-token context window
after `--resume`, even though the Copilot backend serves 1M for opus-4.6/4.7/4.8,
sonnet-4.6, and sonnet-5. The proactive auto-compaction then fires at 200k for
the rest of a resumed session.

The original approach in this change tried to re-append a `[1m]` suffix to the
response `model` so Claude Code would re-detect 1M on resume. **Verified against
the real running client (2.1.216), that does not work.** Claude Code rewrote its
window logic: `getContextWindowForModel` now has a branch that reads a **static
bundled capability table** (`context.native_1m`, true for opus-4.8 etc.), gated
on the request being first-party (`firstParty && Wd()`). `Wd()` is true only when
the base URL host is `api.anthropic.com`, so a bridge user (custom base URL) fails
that gate and falls to the 200k default. The `[1m]` suffix is no longer the lever,
and the bridge cannot influence the persisted model string — empirically, the
injection left the transcript model bare and resume still showed 200,000.

The real lever is entirely client-side: `Wd()` honors an escape-hatch env var,
`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`. The bridge already owns a `config`
command that writes Claude Code's `settings.json` `env` block; the fix is to have
that command write the env var (and neutralize its one side effect). No bridge
response rewrite, no `[1m]`, transcript stays clean, and it survives `--resume`.

## What Changes

- The `config claude-code` command force-writes two env keys into Claude Code's
  `settings.json` `env` block, alongside the existing `ANTHROPIC_BASE_URL`:
  - `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` — makes Claude Code treat the
    bridge base URL as first-party, so its native-1M capability gate fires and
    opus-4.8 (etc.) get a 1M window that survives resume.
  - `DISABLE_ERROR_REPORTING=1` — neutralizes the one side effect of the first
    key: asserting first-party flips Claude Code's error-reporting (Datadog)
    telemetry from off to on for a bridge user; this keeps it off, so the unlock
    changes exactly one thing (the window).
- `config status` reports and drift-detects both keys (a bridge-pointed config
  missing either reads as DRIFTED), the same way it already handles the base URL
  and the legacy fallback key.
- **Revert** the dead `[1m]`-injection code (the `ModelRewriteDetector` suffix,
  `ModelProfile.NativeOneMillionContext`, `BridgeContext.RoutedNativeOneMillionContext`,
  the `RestoreContext1mTag` option) back to the shipped model-name-restore behavior.
- Docs: rewrite `docs/context-window.md` §5 from "the resume caveat, deliberately
  not fixed" to the 2.1.216 mechanism + the env fix + the side-effect note.

Non-goals: no change to sonnet-4.5/haiku-4.5 (truly 200k on Copilot — their
revert-to-200k on resume is correct); no bridge-side response rewrite; no change
to Codex config (the env keys are Claude-Code-only).

## Capabilities

### New Capabilities
<!-- None. -->

### Modified Capabilities
- `client-autoconfiguration`: the `config claude-code` write set and the
  `config status` drift check grow to include the two Claude Code 1M-context env
  keys.

## Impact

- **Code**
  - `src/CopilotBridge.Cli/Hosting/ClientConfig/ClaudeCodeConfigurator.cs` —
    force-write the two env keys in `MergeInto`; surface + drift-detect them in
    `Read`.
  - `src/CopilotBridge.Cli/Hosting/ClientConfig/ConfigState.cs` — carry the
    expected/current values so `Drifted` covers them.
  - Revert of the M1 `[1m]` files (see What Changes).
- **Config output**: Claude Code `settings.json` `env` gains two keys on the next
  `config claude-code` run; Codex config is untouched.
- **Client behavior**: a bridge user who re-runs `config` gets a 1M window on
  opus-4.8 that survives `--resume`; error-reporting telemetry stays off.
- **Docs**: `docs/context-window.md` §5 rewritten; cross-checked README/routing.
- **Tests**: `ClientConfigTests` gains force-write + drift coverage; a real
  `claude.exe --resume` verification confirms the 1M window end-to-end.
