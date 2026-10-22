## Context

Live native Codex subagents fail before executing tools because Copilot rejects the encrypted `spawn_agent.message` replayed as `agent_message`. Direct upstream probes reproduce the failure, including Astra-to-Astra replay; plaintext controls succeed. LiteLLM PR #46315 demonstrates a producer-side ordinary-namespace projection and Codex's explicit `encrypted_function_args: []` delivery marker. Existing ciphertext cannot be recovered by the bridge.

The current native response path uses T2/T3 around the semantic IR and an event ledger for native fidelity. T3 immediately throws on `response.failed`, even when its error is null and the next SSE event contains the actual diagnostic. Arbitrary upstream messages must remain excluded from client/log boundaries.

## Goals / Non-Goals

**Goals:** Preserve complete new collaboration tasks through spawn, send-message, and follow-up; keep the workaround explicit, request-local, stateless, and Native AOT compatible; expose actionable reviewed failure diagnostics; verify real Astra subagent tool execution.

**Non-Goals:** Decrypt or repair old history, support stateful continuation under the workaround, alter model profiles, change Claude Code delivery, publish a release, or modify the user's running bridge.

## Decisions

### Explicit native policy at the strategy boundary

Add `Codex:PlaintextCollaboration:Enabled`, default false. For native `/codex/responses` requests only, the strategy applies a request-local projection after T2 and before upstream auditing/sending, and reverses it before T3 consumes response objects/events. T1 and the general T2 builder retain their existing source/destination independence. Disabled requests take the original path without parsing for collaboration.

### Project a complete namespace with narrow schema edits

Activate only for a `collaboration` namespace with one of `spawn_agent`, `send_message`, or `followup_task` declaring `parameters.properties.message` as `type:string, encrypted:true`. Project that namespace to the reserved ordinary namespace `bridge_plaintext_collaboration`. Remove only the recognized message encryption annotation, and preserve every other schema field and tool. Project top-level tools, `additional_tools`/`tool_search_output` inventories, replayed function-call identities, and explicit/allowed tool choices consistently. Preserve JSON argument strings and all old agent-message/reasoning ciphertext.

Reject reserved-name collisions, contradictory qualified identities, and `previous_response_id`/`conversation` continuation when adaptation activates. Force `store:false`. The existing normal destination policy already strips `store:true`; continuation guards inspect the native IR carrier before T2 where necessary.

### Restore identity and explicit plaintext metadata

Restore namespaces/names on all affected function-call lifecycle copies, argument events, terminal output arrays, echoed tool inventories, and buffered response objects. Only reviewed message actions receive `encrypted_function_args: []`; non-message controls retain their original metadata. Track sparse identity by output index within a request. Reject conflicting nonempty identities or a returned nonempty encrypted-argument marker for a projected plaintext action, rather than relabeling potentially encrypted data. Never log arguments.

### Enrich a failed terminal with a bounded trailing diagnostic

Known failed terminals remain immediate. For native Codex only, an empty/unknown failed terminal permits one following SSE event within a short fixed bound. A following structured `error` can supply a normalized bounded machine code and an exact-match reviewed diagnostic. EOF, a different event, timeout, or an unreadable tail retains the original failure; cancellation still wins. Completed/incomplete terminals never wait for a tail. Preserve exactly one downstream failed terminal and honest audit bytes.

Use a canonical bridge-owned message for the exact confirmed encrypted-function-output error. Other upstream text remains excluded. Carry this reviewed message through the typed failure to native streaming/buffered output and summaries. Preserve the general sanitized failure behavior for unknown messages.

## Risks / Trade-offs

- Plaintext task parameters are a deliberate representation change over existing HTTPS → opt-in configuration, clear documentation, and no argument logging.
- Sparse streamed identities may be incomplete → retain only nonempty known identity fields, validate conflicts, and cover terminal snapshots and multiple indices independently.
- Failed streams may stall → one bounded diagnostic read with cancellation/draining, never wait for normal transport EOF.
- Existing encrypted history can still fail → keep it intact and recommend a fresh task/session when enabling the workaround.
- Unit tests cannot prove Codex chose plaintext dispatch → require a real current client, child-generated evidence, tool/output round trips, and SQLite dispatch-log inspection.

## Migration Plan

Ship the new option disabled. Operators enable it and restart the bridge, then start a fresh native Codex task. Disabling the option restores normal projection for future requests. No credential, routing-location, or history migration is performed.

## Open Questions

No implementation-blocking questions remain. Copilot's internal decryption cause is outside this change; the workaround depends only on the independently demonstrated plaintext contract.
