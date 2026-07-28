## Why

Copilot sends **no keepalive while a model is thinking** — zero `ping` events
across 137 captured response traces, and a measured `claude-opus-5` turn at
`effort=xhigh` opened a thinking block and then put nothing on the wire for
**600 s**. The real Anthropic API pings through exactly that silence, so Claude
Code's two idle watchdogs are calibrated for a stream that never goes quiet. Point
Claude Code at the bridge instead and a *perfectly healthy* deep-thinking turn
looks identical to a dead socket, and the client kills it.

The `bridge-owns-timeouts` change made the bridge's budget the intended authority
by *raising the client's* thresholds through env keys. That works but leaves two
gaps the design doc already names: the env values require a client restart to take
effect, and they protect only clients the bridge itself configured. Injecting the
keepalive the upstream omits closes both — the client stops having an opinion about
silence at all, and the bridge's budget becomes the only thing that can end a
stalled turn.

## What Changes

- While a `/cc` SSE relay is in flight and **upstream has been silent** longer than
  a configured interval, the bridge synthesizes and flushes an Anthropic
  `ping` event to the client, repeating each interval until upstream speaks again
  or the stream-idle budget fires. When upstream is emitting normally the bridge
  injects nothing and arms no keepalive timer.
- The stream-idle budget continues to be the sole authority on when a stalled turn
  ends: an injected `ping` resets the *client's* watchdogs but SHALL NOT reset the
  bridge's own stream-idle budget, which keeps measuring the true upstream gap. On
  expiry the surface is unchanged — the existing retryable `overloaded_error`
  injection (or `Truncate`, per operator config).
- Injected pings are distinguishable from upstream events in the trace/audit
  artifacts, so "upstream went silent" stays observable rather than being masked by
  the very events that hide it from the client.
- Keepalive interval is operator-configurable under `Pipeline:UpstreamTimeout` and
  disable-able (zero or less ⇒ no injection, no timer, byte-identical to today).
- The client env keys the bridge writes (`CLAUDE_STREAM_IDLE_TIMEOUT_MS`) are
  **retained** as a second line of defence for the case where injection is disabled
  or a ping fails to reach the client.
- **Scope: the `/cc` (Anthropic client) path only.** The Codex path keeps its
  existing `response.failed` terminal behavior; a Responses-protocol keepalive is a
  separate question about what `codex.exe` accepts as progress and needs its own
  live probe.

## Capabilities

### New Capabilities
- `stream-keepalive`: Synthesizing downstream keepalive events during upstream
  silence so the downstream client's own idle watchdogs never end a healthy
  long-thinking turn, while leaving the bridge's stream-idle budget as the sole
  authority on when a stalled turn actually ends.

### Modified Capabilities
- `upstream-timeout`: The stream-idle budget's requirement gains the constraint
  that it measures the **upstream** gap only — bridge-synthesized downstream
  keepalives SHALL NOT reset it — so injecting keepalives cannot silently make the
  budget unenforceable.

## Impact

- `src/CopilotBridge.Cli/Hosting/Options/UpstreamTimeoutOptions.cs` — new
  keepalive interval knob; `appsettings.json` documented default.
- `src/CopilotBridge.Cli/Pipeline/Strategies/StreamIdleReader.cs` — the shared
  read-with-deadline helper is where upstream silence is already observed; it must
  be able to report a *keepalive-due* tick without ending the read or resetting the
  idle budget.
- `src/CopilotBridge.Cli/Pipeline/Strategies/Anthropic/CopilotMessagesPassthroughStrategy.cs`
  — emits the synthesized `ping` into the downstream event sequence.
- `src/CopilotBridge.Cli/Endpoints/ClaudeCode/ClaudeCodeMessagesEndpoint.cs` —
  writes/flushes the event; must not let a ping pollute the usage probe or the
  inbound-resp capture's meaning.
- `docs/timeout-chain.md` — §"Why the silence happens at all" currently states the
  bridge deliberately does not synthesize keepalives; that decision is being
  reversed and the doc must record the new one.
- `openspec/specs/upstream-timeout/spec.md` — delta as above.
- Tests: unit coverage for inject-only-when-silent, budget-not-reset-by-ping, and
  disabled ⇒ byte-identical; `Kind=ClientBehavior` real `claude.exe` verification
  against an upstream that goes silent past the client's 180 s watchdog.
