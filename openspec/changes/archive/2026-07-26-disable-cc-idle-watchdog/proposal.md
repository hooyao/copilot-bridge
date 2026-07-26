## Why

A Claude Code session on `claude-opus-5` at `xhigh` effort failed deterministically
through the bridge: the upstream emitted `message_start` + an empty `thinking`
`content_block_start`, then went silent and the turn died. Investigation produced
three findings, one of which is a bridge defect and one of which is a stale premise
baked into an existing default.

1. **Copilot caps a token-less stream at ~300s.** Measured 4× (303.0s / 304.4s /
   305.2s / 305.8s) across two unrelated prompts and two independent HTTP stacks,
   including a direct-to-Copilot replay that bypassed the bridge entirely (raw
   Python `HTTPSConnection`, 900s socket timeout). The connection ends in a clean
   EOF — no `error` event, no `message_stop`. This is an upstream fact the repo does
   not record anywhere, and it is invisible in normal operation: across 9,608
   captured responses, **zero** ran past 250s, so nothing previously touched the cap.

2. **Claude Code's own idle watchdog fires at the same 300s, and the bridge leaves
   it on.** `API_FORCE_IDLE_TIMEOUT` aborts a streaming response after 5 minutes
   with no bytes; per Claude Code's documentation it is *inactive* on direct
   Anthropic/AWS connections and **active on every other provider** — i.e. active
   for every bridge user, by default. Copilot never emits an SSE `ping` (verified:
   0 occurrences across 290 captured upstream bodies), so during extended thinking
   there are genuinely no bytes on the wire and a legitimately long think is
   indistinguishable from a dead stream. The bridge already owns this bound
   (`Pipeline:UpstreamTimeout:StreamIdleTimeoutSeconds`, operator-tunable, with a
   defined recovery action); a second uncoordinated client-side timer can only abort
   turns the bridge intended to keep alive. The bridge force-writes other Claude Code
   env keys already but not this one.

3. **The stream-idle default rests on a variable that does not exist.**
   `StreamIdleTimeoutSeconds` = 60 is justified in three places as "below Claude
   Code's own watchdog `CLAUDE_STREAM_IDLE_TIMEOUT_MS`, default 90s". That variable
   appears nowhere in current Claude Code documentation; the real mechanism is
   `API_FORCE_IDLE_TIMEOUT` at 5 minutes. The stated reason for the default is
   therefore false, and an operator tuning this knob is reasoning from a fiction.

## What Changes

- **`API_FORCE_IDLE_TIMEOUT` = `"0"` becomes a bridge-managed Claude Code env key**,
  force-written by `config claude-code` exactly as the 1M-context pair is, and
  reported by `config status` (its absence or a non-`"0"` value is drift).
- **The three stale `CLAUDE_STREAM_IDLE_TIMEOUT_MS` references are corrected** to the
  real `API_FORCE_IDLE_TIMEOUT` mechanism, in `UpstreamTimeoutOptions.cs`,
  `appsettings.json`, and `docs/pipeline-design.md`. The 60s default value is
  **unchanged** — only the justification is corrected. Re-tuning it is a separate
  decision that should be made against real evidence, not folded into a doc fix.
- **A new `docs/copilot-stream-cap.md`** records the ~300s upstream cap, the
  measurements, the reproduction method, and the operational consequence
  (`effort=medium` completes where `high`/`xhigh` do not).

Not in scope: changing `StreamIdleTimeoutSeconds`, or any attempt to work around the
Copilot cap. The cap is server-side and unconditional — no client or bridge setting
can extend it, and the honest remedy is lower effort or a shorter prompt.

## Capabilities

### New Capabilities

None. Both behavioral changes extend existing capabilities.

### Modified Capabilities

- `client-autoconfiguration`: adds `API_FORCE_IDLE_TIMEOUT` to the set of Claude Code
  env keys the bridge force-writes and drift-checks, alongside
  `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` and `DISABLE_ERROR_REPORTING`.
- `upstream-timeout`: corrects the documented relationship between the bridge's
  stream-idle budget and the client-side watchdog it was said to coordinate with, and
  records that the bridge is now the sole idle actor for a configured client.

## Impact

**Code**
- `src/CopilotBridge.Cli/Hosting/ClientConfig/ClaudeCodeConfigurator.cs` — new managed
  key constant, force-write in `MergeInto`, read + detail line in `Read`.
- `src/CopilotBridge.Cli/Hosting/ClientConfig/ConfigState.cs` — new
  `ExpectedForceIdleTimeout` / `CurrentForceIdleTimeout` pair, included in `Drifted`.
- `src/CopilotBridge.Cli/Hosting/ClientConfig/CodexConfigurator.cs` — passes `null`
  for the new pair (Codex does not manage it), mechanical.
- `src/CopilotBridge.Cli/Hosting/Options/UpstreamTimeoutOptions.cs`,
  `src/CopilotBridge.Cli/appsettings.json` — comment corrections only, no behavior.

**Tests**
- `tests/CopilotBridge.UnitTests/ClientConfigTests.cs` — every `ConfigState`
  construction gains the new pair; new cases for force-write, drift-on-missing, and
  preservation of unrelated keys.

**Docs**
- New `docs/copilot-stream-cap.md`; correction in `docs/pipeline-design.md`.

**User-visible**: a user who re-runs `config claude-code` gets one added env key.
Existing configured users are reported as DRIFTED until they re-run it — intended,
since that is exactly the condition where the watchdog is still armed.
