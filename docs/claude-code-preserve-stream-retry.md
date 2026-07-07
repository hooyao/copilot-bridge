# Claude Code PreserveStream retry behavior

This note records the July 2026 investigation into why copilot-bridge response-leak detection can emit an Anthropic `overloaded_error` SSE event but Claude Code may still show an incomplete/error message instead of retrying the whole turn.

## Problem

When `Pipeline:Detectors:ResponseLeakGuard:PreserveStream` is `true`, copilot-bridge keeps streaming upstream SSE events to Claude Code. If the response-leak detector later sees a leaked Claude Code protocol/tool-use shape in assistant text, it injects:

```sse
event: error
data: {"type":"error","error":{"type":"overloaded_error","message":"..."}}
```

The intent was that Claude Code would discard the attempt and retry from clean history. In current Claude Code versions, that is not guaranteed once visible assistant output has already streamed.

## Findings

### `overloaded_error` is the right retryable error type

Local Claude Code source confirms that the retry helper recognizes both HTTP `529` and error messages containing `"type":"overloaded_error"`:

- The API retry helper (Claude Code's `services/api` retry module, e.g. `withRetry`):
  - `is529Error(error)` checks `error.status === 529` or `error.message?.includes('"type":"overloaded_error"')`.
  - `shouldRetry(error)` also treats messages containing `"type":"overloaded_error"` as retryable.

So the bridge's `ResponseDetectionSignal.OverloadedError` wire shape is not the primary problem.

### Claude Code has a streaming-to-non-streaming fallback path

Local Claude Code source shows that streaming iteration errors are caught in:

- The main Anthropic API client (Claude Code's `services/api` client module, e.g. `claude.ts`)

In the streaming catch block, Claude Code computes:

```ts
const disableFallback =
  isEnvTruthy(process.env.CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK) ||
  getFeatureValue_CACHED_MAY_BE_STALE(
    'tengu_disable_streaming_to_non_streaming_fallback',
    false,
  )
```

If fallback is not disabled, it calls `executeNonStreamingRequest(...)`. When the original streaming error was a 529 / `overloaded_error`, it passes:

```ts
initialConsecutive529Errors: is529Error(streamingError) ? 1 : 0
```

Therefore the Claude Code setting that copilot-bridge should write is an environment setting that ensures this fallback path is not disabled:

```jsonc
{
  "env": {
    "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "false"
  }
}
```

This is not a magic "force retry" switch. It is a guardrail to avoid users or inherited config disabling the fallback path.

### Current Claude Code may preserve partial output after visible text has streamed

Official Claude Code docs state that transient failures are retried, but also document a key exception: if a server error arrives after Claude has already streamed visible output, Claude Code keeps the partial response and appends an incomplete-response notice instead of re-running the request, because re-running could execute the same tools twice.

Sources checked with MCP/web tools:

- <https://code.claude.com/docs/en/errors>
  - Automatic retries cover server errors, overloaded responses, request timeouts, temporary 429 throttles, and dropped connections.
  - The docs explicitly say server errors after visible output preserve partial output with an incomplete-response notice rather than retrying.
- <https://platform.claude.com/docs/en/api/errors>
  - HTTP `529` maps to `overloaded_error`.
  - SSE streams may emit errors after the HTTP response has already returned `200`.
- <https://platform.claude.com/docs/en/build-with-claude/streaming>
  - SSE `event: error` may carry `{"type":"overloaded_error"}`.

This explains the observed behavior: the bridge injects a retryable `overloaded_error`, but by the time the detector trips under `PreserveStream=true`, Claude Code may already have displayed visible assistant text. Newer Claude Code versions treat that as an incomplete partial response, not as a safe whole-turn retry.

## Why tool-use leak detection trips late

The response leak detector intentionally uses structural detection. For a leaked tool call it needs to observe enough text to prove a closed, balanced shape such as:

```xml
<invoke name="...">
  <parameter name="...">...</parameter>
</invoke>
```

That means the bridge often has to let earlier `text_delta` chunks pass through before it knows the content is a leak. With `PreserveStream=true`, those earlier chunks may already be visible to Claude Code. Once visible output exists, Claude Code's current retry policy may preserve the partial response instead of retrying.

## Consequences for copilot-bridge

### Recommended Claude Code config

`configure-claude-code` should write the bridge endpoint plus the fallback guardrail:

```jsonc
{
  "env": {
    "ANTHROPIC_BASE_URL": "http://localhost:{port}/cc",
    "ANTHROPIC_AUTH_TOKEN": "dummy",
    "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "false"
  }
}
```

Use repo scope for `.claude/settings.local.json` and global scope for `~/.claude/settings.json`.

### Documentation wording

Do not promise that `PreserveStream=true` guarantees automatic retry for every leak. More accurate wording:

> The Claude Code config ensures streaming-to-non-streaming fallback is not disabled, so pre-output or otherwise safe streaming failures can enter Claude Code's retry/fallback path. If a response leak is detected after visible output already streamed, current Claude Code versions may preserve the partial output and show an incomplete-response notice instead of retrying the whole turn.

### Reliable retry option

If the user wants leak detection to reliably produce a retryable pre-output failure, set:

```jsonc
{
  "Pipeline": {
    "Detectors": {
      "ResponseLeakGuard": {
        "PreserveStream": false
      }
    }
  }
}
```

With `PreserveStream=false`, copilot-bridge buffers the whole upstream response before writing to Claude Code. If a leak is detected, the bridge can return a real HTTP `529` / `overloaded_error` before any visible output reaches Claude Code. The cost is losing streaming TTFT for all responses.

### Code comment to update

The comment in `src/CopilotBridge.Cli/Pipeline/Response/Detection/ResponseInspectionStage.cs` around the streaming abort path is outdated. It currently says Claude Code discards the whole attempt and retries from clean history. It should be updated to say the injected error ends the stream and can enter Claude Code's retry/fallback path only when considered safe; after visible output, current Claude Code may preserve partial output.
