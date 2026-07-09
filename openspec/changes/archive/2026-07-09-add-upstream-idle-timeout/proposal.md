## Why

When Copilot is slow or stalls, the bridge has no upstream inactivity bound: a request can hang until the *client* gives up. In a real incident three `/cc/v1/messages` requests (opus-4.8, `effort=xhigh`, a ~500k-token near-full-`[1m]` prompt) blocked waiting for Copilot's first byte for 4.4 min / 5.0 min / 0.8 min and were then cancelled by Claude Code — before the bridge's only backstop (a 10-minute `HttpClient.Timeout`) could fire. Worse, once an SSE stream *has* started, `StreamEventsAsync` reads with only `httpCtx.RequestAborted`; if Copilot emits some events then hangs forever, the bridge hangs forever too. `HttpClient.Timeout` does **not** cover post-headers body reads under `ResponseHeadersRead`, and the reference implementation (`references/copilot-api`) forwards with a bare `fetch` and no timeout at all — so there is nothing to copy and no existing safety net.

## What Changes

- Add an **upstream inactivity (idle) timeout** to the `/cc` Anthropic passthrough forward path — an *inactivity* bound, not a total-duration cap, so a slow-but-progressing request is never punished: any byte/event from upstream resets the timer.
- Split it into **two independently-tunable budgets**, because a legitimate time-to-first-byte and a legitimate mid-stream gap have very different ranges:
  1. **First-byte budget** — bounds the `SendAsync`/`ResponseHeadersRead` phase (the exact phase the incident hung in). Generous, since near-full-context prompts legitimately take minutes to first byte.
  2. **Stream-idle budget** — bounds the gap between consecutive SSE events pulled from upstream in `StreamEventsAsync`. Tighter; reset on every event.
- Add a `Pipeline:UpstreamTimeout` options section (mirroring the existing `Pipeline:UpstreamRetry` style: verbose `_comment`, tunable, read-at-startup), with defaults grounded in the incident data. Each budget is individually **disable-able** (`<= 0` ⇒ off, and off means zero timer overhead — the byte-identical `/cc` hot path must not regress).
- Map a fired timeout into the endpoint's existing error structure: **before headers** ⇒ a real HTTP status (`504 Gateway Timeout`); **mid-stream** (headers already sent) ⇒ inject a retryable `overloaded_error` SSE event and end the stream, the same mechanism `RunawayGuard`/`ResponseLeakGuard` already use. Under the `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1` setting the bridge's own `config` command writes, this drives a **clean whole-turn retry** (the error propagates to Claude Code's `query.ts` retry path); with the flag unset, Claude Code instead does a non-streaming fallback re-request. Either way the stalled turn is re-attempted rather than left as a silent partial. (Plain truncation stays available as a config option for operators who do not want a retry.)
- Cover **both** forward paths — the `/cc` Anthropic passthrough (`CopilotMessagesPassthroughStrategy`) and the Codex/Responses translation path (`CopilotResponsesStrategy`). The first-byte budget is shared at the client (both `PostMessagesAsync` and `PostResponsesAsync`); the stream-idle budget is applied at each strategy's read site. Codex surfaces a mid-stream timeout through its **existing fault channel** (`response.failed` + `UpstreamStreamFault`), not the Anthropic `overloaded_error` event, because the Codex CLI speaks the Responses protocol and would not understand an Anthropic error envelope.

## Capabilities

### New Capabilities
- `upstream-timeout`: Bounds how long the bridge will wait on an unresponsive upstream while forwarding a request — a first-byte inactivity budget over the response-headers phase and a stream-idle inactivity budget over the SSE body — and defines how a fired budget surfaces to the client (pre-headers 504 vs. a mid-stream retryable `overloaded_error`, defaulting to a whole-turn retry under the flag the bridge sets) and to the operator (summary + log).

### Modified Capabilities
<!-- None. This is new forwarding-resilience behavior; it does not change the requirements of observability, response-leak-guard, runaway-guard, or pipeline-request-isolation. -->

## Impact

- **New:** `Hosting/Options/UpstreamTimeoutOptions.cs`; a `Pipeline:UpstreamTimeout` block in `appsettings.json`; DI registration in `BridgeServiceCollectionExtensions`.
- **Modified:** `Copilot/CopilotClient.cs` (a shared first-byte-budget helper wrapping the `SendAsync` in **both** `PostMessagesAsync` and `PostResponsesAsync`); `Pipeline/Strategies/Anthropic/CopilotMessagesPassthroughStrategy.cs` and `Pipeline/Strategies/Codex/CopilotResponsesStrategy.cs` (stream-idle timer around each read site — the `/cc` one throws `UpstreamTimeoutException`; the Codex one latches it as a stream fault so its existing `response.failed` terminal fires); `Endpoints/ClaudeCode/ClaudeCodeMessagesEndpoint.cs` and `Endpoints/Codex/CodexResponsesEndpoint.cs` (map/surface the timeout + summary field).
- **Unchanged:** `CopilotClient` retry loops are not moved into the timeout (the first-byte budget wraps the call from outside so backoff can't eat the budget). The buffered (non-streaming) path and the disabled-default hot paths (both `/cc` and Codex) are behaviorally untouched.
- **Constraints:** Native-AOT clean (no reflection, no new non-trim-friendly dependency); the disabled path allocates no timer and adds no wrapper.
