## Why

Claude Code can include its `Agent` delegation tool in requests made by a sub-agent. When that request is cross-routed to a GPT Responses backend, GPT treats the tool as an ordinary repeatable function and can recursively fan out hundreds of child agents before Claude Code's depth limit applies. A captured incident produced 327 unique agents in the first hour, 283 concurrent bridge requests, and upstream rate limiting while every individual response remained below the existing runaway thresholds.

## What Changes

- Add a configurable CC-to-Responses request-translation guard that removes the `Agent` tool from Claude Code sub-agent requests before the upstream `/responses` body is serialized.
- Identify a sub-agent by the inbound `x-claude-code-agent-id` header; do not require `x-claude-code-parent-agent-id`, because first-generation Claude Code sub-agents omit it.
- Keep `Agent` available to the root Claude Code request, and leave native `/cc` Anthropic passthrough and native `/codex` traffic unchanged.
- Reconcile `tool_choice` when a filtered tool was forced so the emitted Responses request remains valid.
- Emit an operator-visible warning for each actual removal, including the configuration recovery path when the removal is considered incorrect.
- Expose a startup-bound configuration switch, enabled by default, that restores the current recursive-delegation behavior when disabled.

## Capabilities

### New Capabilities

- `cc-gpt-agent-delegation-guard`: Request-translation behavior and configuration for preventing recursive Claude Code agent delegation on Responses backends.

### Modified Capabilities

- None.

## Impact

- Affected production code: CC-to-GPT T2 request serialization, bridge options binding, and default `appsettings.json`.
- Affected documentation: the pipeline architecture contract and operator configuration reference embedded in `appsettings.json`.
- Affected tests: contract-level Responses request builder tests, configuration binding tests, captured-byte/API-contract coverage, and a real headless Claude Code behavior run through the `CcToGpt` scenario.
- No dependency, endpoint, model-catalog, or Native AOT serialization changes.
