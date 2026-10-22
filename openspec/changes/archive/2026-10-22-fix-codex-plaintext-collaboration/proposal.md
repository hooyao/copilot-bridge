## Why

Copilot's Astra Responses route can generate encrypted collaboration task arguments that it cannot subsequently decrypt, preventing otherwise healthy Codex subagents from starting. The bridge also loses the actionable error when Copilot emits an empty `response.failed` followed by a detailed `error` event, causing Codex to report a generic stream failure.

## What Changes

- Add an opt-in native Codex plaintext collaboration adapter, following the protocol demonstrated by LiteLLM PR #46315. Translate recognized collaboration inventories into an ordinary upstream namespace, preserve complete message text, and restore the Codex namespace plus explicit plaintext dispatch metadata on returned calls.
- Apply the projection consistently to incremental inventories, explicit tool choices, replayed calls, streaming item lifecycles, and buffered responses, with request-local identity tracking and explicit rejection of unsafe collisions or stateful continuation.
- Preserve existing encrypted history without attempting to decrypt, relabel, or silently delete it. Leave the workaround disabled by default and document how to enable it for new tasks.
- Surface bounded upstream error codes and reviewed diagnostic messages, including a trailing error after an otherwise empty failed terminal, without leaking arbitrary upstream-generated text or delaying a known completed stream.
- Verify the change with contract tests, mutation checks, captured request replay, and a real Codex Astra spawn/message/follow-up/tool execution scenario with client-owned dispatch evidence.

## Capabilities

### New Capabilities
- `codex-plaintext-collaboration`: Explicit, stateless, content-preserving plaintext collaboration adaptation for native Codex traffic through Copilot Responses.

### Modified Capabilities
- `codex-response-fidelity`: Preserve actionable, bounded upstream failure diagnostics and the one-terminal failure contract when Copilot separates the terminal from its error detail.

## Impact

The native Codex Responses strategy, configuration binding, request/response projection, and stream error handling change. Claude Code protocol behavior and existing backend model profiles remain unchanged. No new runtime dependency or reflection-based JSON serialization is required. The plaintext mode changes the representation of new delegated task text over the existing authenticated HTTPS connection and requires explicit configuration.

References: https://github.com/BerriAI/litellm/pull/46315 and the live Copilot reproduction recorded in `tmp-namespace-repro/astra-encrypted-content-investigation.md` (local diagnostic material, not a shipped fixture).
