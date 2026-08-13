## Why

Release 0.5.3 does not give operators a transparent timeout chain. The bridge
silently derives and force-writes Claude Code values, `config codex` replaces a
provider block in a way that can discard user timeout/retry fields, and startup
labels per-attempt deadlines as if they ended the whole turn while omitting client
retries, buffered-body gaps, configuration precedence, and several client bounds.

The operator must be able to place the bridge configuration beside the observed
client configuration and calculate what every timeout means without any hidden
margin, fixed fallback, clamp, overwrite, or retry.

## What Changes

- **BREAKING:** Make `config claude-code` connection-only. It writes the base URL,
  fills the required token placeholder only when absent, and preserves every
  timeout, retry, watchdog, first-party/1M, telemetry, and fallback setting. This
  removes the hidden `+300s` margin and fixed `API_TIMEOUT_MS=3600000`, as well as
  unrelated behavioral mutations from a command advertised as pointing the
  client at the bridge. `config status` no longer treats those user values as
  bridge drift.
- **BREAKING:** Make `config codex` merge only the connection/auth fields it owns.
  Preserve `stream_idle_timeout_ms`, `request_max_retries`,
  `stream_max_retries`, WebSocket timeout, headers, and every other user field in
  `[model_providers.copilot-bridge]`; never derive or invent a client timeout.
- Replace the startup claim `what ends a turn` with a source-labelled timeout
  inventory that distinguishes configured duration, client default/effective value,
  scope (`per send`, `per request`, `per stream attempt`, `per gap`), retries, and
  what can actually end the whole turn.
- Name the bridge phases truthfully: the first-byte option bounds **upstream
  response headers per bridge send**, not the client's first downstream byte; the
  stream-idle option bounds a gap between complete parsed upstream SSE events; a
  true buffered body has no bridge timeout after headers.
- Report the bridge network retry count, Claude Code's request timeout in one
  plain-language line (`normal` / `after stream error`), and Codex's provider
  request/stream retry counts. Never expand retry counts into noisy total-attempt
  arithmetic or reduce these layers to one false turn-level number.
- Report both clients from the global files the bridge can inspect and explicitly
  state the visibility boundary. In particular, Codex calculations are based on
  global `~/.codex/config.toml`; project, profile, CLI, and other higher-precedence
  overrides are not visible at bridge startup.
- Display durations in concise human units (`15s`, `5m`, `10m`). When a known
  client floor/cap changes an explicit value, show both human durations; unknown or
  version-sensitive behavior remains labelled unknown rather than being guessed.
- Correct keepalive reporting: pings begin only after the first upstream event,
  never reset the bridge idle deadline, cannot reach a whole-buffered response, and
  make a client idle watchdog non-binding only while they are actually delivered.
  Equal unprotected deadlines are reported as a race, not as a bridge win.
- Reject bridge timeout values that cannot be represented by the runtime timer
  APIs instead of accepting configuration that later throws on a request.
- Update docs and contract tests from the operator-visible contract, then verify
  both real clients from their own logs before shipping.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `client-autoconfiguration`: Make connection commands own connection/auth facts
  only and preserve all user-selected Claude Code and Codex behavioral fields.
- `timeout-budget-report`: Replace the misleading turn-level table with a compact
  source-, scope-, retry-, and mode-aware global configuration inventory.
- `stream-keepalive`: Describe keepalive strictly as runtime downstream activity,
  without requiring the bridge to rewrite client timeout configuration.
- `upstream-timeout`: Validate representable timer ranges and preserve the exact
  configured positive bridge durations without hidden arithmetic.

## Impact

This affects client configuration merges and drift rules, timeout readers,
startup rendering, timeout option validation, documentation, and unit/real-client
verification. It changes no request/response protocol and adds no dependency. The
existing unapproved `complete-codex-timeout-parity` artifacts are superseded in
place by this proposal; no product implementation begins until the revised console
output is explicitly approved.
