## Why

Codex applies a 300-second idle timeout to each wait for the next parsed Responses SSE event, while GitHub Copilot can remain healthy but emit no event for longer than that during deep reasoning. The bridge currently injects keepalives only for Anthropic clients, so Codex can cancel a healthy stream before the bridge's configured upstream-idle authority decides that it is stalled.

## What Changes

- Extend silence-triggered downstream keepalive injection to native `/codex` Responses streams.
- Use the same read race, elapsed-gap calculation, and timer ownership already used for Claude Code instead of creating a second periodic timer loop.
- Render a complete Responses-compatible SSE data event that resets Codex's parsed-event idle timeout and is otherwise ignored by the client; an SSE comment alone is insufficient.
- Keep synthesized events downstream-only: they do not reset the bridge's upstream stream-idle budget, alter response state or usage, or appear as Copilot-originated trace data.
- Extend the startup timeout report with Codex's configured or default parsed-event idle watchdog and the keepalive-aware party that actually ends a silent gap.
- Add contract tests for timing, protocol shape, disablement, upstream-timeout independence, and native event fidelity, plus a real headless Codex behavior verification.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `stream-keepalive`: Extend the existing protocol-aware keepalive contract from Anthropic clients to native Codex Responses clients while preserving the shared silence and timeout-management semantics.
- `timeout-budget-report`: Report the native Codex client bound and keepalive-aware end-to-end idle-gap authority at startup alongside Claude Code.

## Impact

This affects the shared streaming timeout/keepalive strategy, the Codex Responses outbound edge, keepalive configuration documentation, tracing, and unit/integration coverage. It changes only streaming behavior during upstream silence and adds no dependency or breaking configuration change.
