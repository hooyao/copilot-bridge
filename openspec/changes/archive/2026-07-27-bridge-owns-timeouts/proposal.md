## Why

A deep-thinking `claude-opus-5` turn at `effort=max` reliably fails: Copilot emits
no SSE keepalive while the model thinks (verified: zero `ping` events across 137
captured response traces), so Claude Code's own idle watchdog kills a perfectly
healthy stream. Measured in a controlled lab driving the real `claude.exe`
(2.1.220), the abort lands at **180.013 s**; the non-streaming fallback it then
issues dies against the bridge's 240 s first-byte budget, producing a `504`. A
real session reproduced the full cascade and only succeeded on the third attempt
at 236.7 s — 3 s under the budget. See `docs/timeout-chain.md`.

Today the *client* owns the binding constraint, and it is both invisible and
accidentally tightened by the bridge itself: the `config` command writes
`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` to unlock the 1M context window, and
that flag's side effect swaps Claude Code's idle budget from its 300 s default
down to the 180 s first-party value. The operator has no way to see any of this.

## What Changes

- **The bridge becomes the single authority on long-thinking timeouts.** The
  client is configured to outlast the bridge, so the bridge's own inactivity
  budgets are what actually fire — one place to tune, one place that reports.
- `config claude-code` force-writes two additional managed env keys.
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS` is **derived** from the bridge's stream-idle
  budget (it is the only knob that lifts *both* client idle watchdogs).
  `API_TIMEOUT_MS` is written as a **fixed maximum**, not derived: it is a
  wall-clock cap while the budgets bound inactivity, so no finite value can be
  guaranteed to outlast them — it is reported as a residual bound instead. It is
  still raised because it also caps the non-streaming-fallback per attempt.
  `config status` reports drift on both, like the existing managed keys.
- **BREAKING (operational, not wire):** the shared upstream `HttpClient.Timeout`
  changes from a hard-coded 10 minutes to `Timeout.InfiniteTimeSpan`. That coarse
  cap silently truncated the non-streaming fallback path — the one path where it
  bounds the whole request including the body — at 10 minutes regardless of
  `FirstByteTimeoutSeconds`. The two fine-grained `Pipeline:UpstreamTimeout`
  budgets become the only upstream bound. An operator who disables *both* budgets
  now has no upstream bound at all, where previously 10 minutes backstopped them.
- At startup the bridge reads Claude Code's `settings.json`, combines the two
  client env values with its own `FirstByteTimeoutSeconds` /
  `StreamIdleTimeoutSeconds`, and logs the **effective end-to-end timeout** — plus
  a warning whenever the client would fire first, naming both remedies: run the
  bridge's `config claude-code` command, or set the environment variable by hand.
  A **missing** key warns exactly like a too-short one: absence is not benign,
  because the bridge's own 1M-context key makes the client fall back to the
  measured 180 s first-party bound, and absence is the state of every install
  configured before this change.
- **Not in this change: SSE `ping` injection.** Keepalive injection is the other
  half of the fix — it needs no client restart and no client-side configuration,
  and it is what the real Anthropic API does — but it touches the `/cc` relay
  rather than configuration, so it ships as its own change. This change
  deliberately writes **no** requirement forbidding it.

## Capabilities

### New Capabilities

- `timeout-budget-report`: At startup the bridge reports the effective
  end-to-end timeout derived from both sides of the chain (its own upstream
  budgets and the client's configured watchdog values), and warns when the
  client would fire first — so the operator can see the real bound without
  reverse-engineering it from two config files.

### Modified Capabilities

- `client-autoconfiguration`: adds `CLAUDE_STREAM_IDLE_TIMEOUT_MS` and
  `API_TIMEOUT_MS` to the Claude-Code-managed force-written env keys, and to the
  drift set `config status` reports. Codex remains unaffected.
- `upstream-timeout`: the coarse `HttpClient.Timeout` is no longer the fallback
  bound when a budget is disabled; the two configured inactivity budgets become
  the sole upstream bound, and the first-byte budget governs the buffered
  (non-streaming) path end-to-end rather than being capped at 10 minutes.

## Impact

- `src/CopilotBridge.Cli/Hosting/ClientConfig/ClaudeCodeConfigurator.cs` — two new
  managed keys in the merge and the drift read.
- `src/CopilotBridge.Cli/Hosting/ClientConfig/ConfigState.cs` — carries the new
  expected/current pairs for drift.
- `src/CopilotBridge.Cli/Hosting/BridgeServiceCollectionExtensions.cs` —
  `HttpClient.Timeout` becomes infinite.
- `src/CopilotBridge.Cli/Hosting/BridgeStartupHostedService.cs` — emits the
  effective-timeout report.
- `src/CopilotBridge.Cli/appsettings.json` — documents the new relationship
  between the bridge budgets and the client keys they drive.
- `README.md` — gains a dedicated **`## Long-thinking timeouts`** section. The
  topic is currently a single settings-table row, yet it is the failure users are
  least equipped to self-diagnose: the binding bound lives in the *client*, is
  invisible from the bridge's logs, and is tightened by the bridge's own
  1M-context key. The sample `settings.json` block, the key bullet list, the
  `Pipeline:UpstreamTimeout` row, `Limitations`, and `References` are updated to
  agree with it.
- `docs/timeout-chain.md` — promoted from an incident write-up to the topic's
  reference document (README's new section links here for depth), keeping the
  measured lab results and production cascade as its evidence base.
- `docs/context-window.md` — recommends
  `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` without noting that asserting
  first-party also tightens Claude Code's stream-idle bound from 300 s to 180 s;
  needs that side effect and a cross-link to the timeout chain.
- Tests: `tests/CopilotBridge.UnitTests/ClientConfigTests.cs` (managed keys,
  drift, idempotence, preservation) and new coverage for the budget report.
- No change to forwarded or relayed bytes on any path.
