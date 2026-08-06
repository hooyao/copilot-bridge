## Context

The current CC→Responses response path maps text and function calls from Responses SSE into Anthropic SSE but explicitly skips every `reasoning` output item. A 34-request real session contained 69 such items, all with `encrypted_content`, `summary`, `content`, and identity fields, while the client received and replayed none.

The inverse request-side machinery already maps an Anthropic `RedactedThinkingBlockParam` into a Responses reasoning input item, but ordinary `redacted_thinking.data` can carry only one opaque string and cannot independently represent all required Responses fields. A fresh live gpt-5.6-sol matrix established the current replay contract:

- `encrypted_content` alone or with `id` → 400, missing `summary`;
- `encrypted_content + summary` → 200;
- `id + encrypted_content + summary` → 200;
- `encrypted_content + summary + content` without `id` → 200;
- the complete original item → 200.

A deterministic native-Anthropic experiment through real Claude Code 2.1.220 established the client contract: a `redacted_thinking` block followed by a real Bash tool call was stored as hidden assistant content, the tool executed, and the next request echoed the exact data (including `+`, `/`, and `=`) before the same tool call. The data never appeared in visible final text.

Constraints:

- encrypted reasoning and every required JSON value are opaque and must not be normalized;
- provider/bridge metadata must not become visible text;
- the Claude client can only carry the standard `data` string, not arbitrary sibling fields;
- native Codex event fidelity and its existing reasoning part bag remain unchanged;
- response detectors must continue to inspect semantic text/tool blocks and must not scan encrypted payload as generated text;
- Native AOT/source-generated JSON constraints remain mandatory.

## Goals / Non-Goals

**Goals:**

- Preserve every complete Responses reasoning item across the Claude Code edge and the following tool-result request.
- Restore exact `encrypted_content`, required `summary`, and present `id`/`content` JSON values in original order.
- Use a standard hidden Anthropic block that real Claude Code accepts and echoes.
- Make the private format versioned, bounded, strict, and source-protocol isolated.
- Keep opaque state out of visible output and detector text surfaces.
- Prove the full two-turn loop with real Claude Code and current live gpt-5.6-sol.

**Non-Goals:**

- Displaying or decrypting model reasoning.
- Mapping plaintext Anthropic `thinking` to Responses.
- Requiring `id` or `content` when the backend omits them.
- Defining compatibility for arbitrary third-party Anthropic clients; this is the `/cc` Claude Code route.
- Tool-schema, cache, or WebSearch changes.

## Decisions

### 1. Use `redacted_thinking.data` as a versioned opaque envelope

T3 will emit one complete Anthropic `redacted_thinking` block for each completed/available Responses reasoning item. Its `data` is a bridge-owned envelope containing the original reasoning item fields needed for replay. A fixed high-entropy ASCII prefix plus version identifies the envelope; the payload is base64url of compact UTF-8 JSON so the outer string is stable through JSON serialization and distinguishable from a provider-native encrypted blob.

The envelope schema contains exactly:

- version;
- `encrypted_content` string;
- `summary` JSON array (required for a replayable item);
- optional `id` string;
- optional `content` JSON array.

Alternative: put only `encrypted_content` in `data`. Rejected by live 400 (`summary` required).

Alternative: visible text/marker blocks or custom Anthropic fields. Rejected because they leak provider state or violate the Claude protocol.

Alternative: a server-side session map keyed by message ID. Rejected because it breaks stateless/restart/resume behavior and couples state to one bridge process.

### 2. Emit reasoning when a complete item is available, not from deltas

Current Copilot reasoning output items carry the replay fields on `response.output_item.added` and/or `.done`; T3 will retain the latest complete item per open reasoning output and emit exactly one block at its proper output position. It will not turn summary deltas into visible thinking text.

The block lifecycle is `content_block_start` containing the complete `redacted_thinking` value, immediately followed by `content_block_stop`; no delta is needed or valid for this block. This matches the Anthropic SDK union and the real-client experiment.

A reasoning item missing `encrypted_content` or `summary` is not replayable. T3 records/logs that downgrade and does not emit a malformed carrier that guarantees a next-turn 400.

### 3. Decode at the Claude edge, not in a translator

The fold/unfold lives in the Claude client adapters, because `the Anthropic wire carries one opaque string` is a fact about THAT CLIENT PROTOCOL, not about translation. T3 pushes the whole item onto the IR and T2 pulls from the part bag it already reads; neither takes a client-identity parameter. Native Codex requests continue using their existing typed T1 part bag; native Anthropic passthrough never enters T2.

A non-prefixed `redacted_thinking.data` remains ordinary opaque provider data and follows the existing blob-only behavior. A prefixed but malformed/unsupported envelope fails closed with a bounded bridge error rather than being forwarded as arbitrary reasoning JSON or exposed as text. Size and structural limits prevent the private data string from becoming an unbounded JSON transport.

Alternative: infer provenance from provider bags/content. Rejected after the multimodal change proved this inference is unreliable for typed-only native Codex requests.

### 4. Preserve JSON values through explicit writer/parser code

The envelope encoder/decoder will use `Utf8JsonWriter`/`JsonDocument` and `JsonElement.WriteTo`, not reflection serialization. `summary` and `content` retain their exact JSON values; string/base64 encoding changes are insignificant outside the decoded values, while encrypted content itself remains exact.

The decoder accepts only the frozen field set/version and validates types. Unknown future versions fail closed rather than being interpreted with older semantics.

### 5. Verification is two-sided and multi-turn

Contract tests first establish:

- reasoning is currently absent on the Claude-facing T3 stream;
- a completed reasoning item becomes one valid redacted block;
- Claude echo through T2 reconstructs the required fields exactly;
- dropping `summary`, changing encrypted bytes, or disabling the carrier makes the test fail;
- native Codex and ordinary redacted thinking remain unchanged.

ApiContract replays a captured real reasoning item through T3→Claude request echo→T2. Live acceptance then drives real Claude Code through at least one tool call so the client must retain and echo the carrier; the next Copilot request must contain the restored reasoning item and complete without 400. Transcript plus exact trace decide PASS.

## Risks / Trade-offs

- **[Risk] Prefix collision with provider-native encrypted data.** → Use a long bridge-specific versioned prefix and decode only with explicit Claude→Responses provenance; test non-prefixed blobs unchanged.
- **[Risk] Corrupted envelope injects arbitrary JSON upstream.** → Strict version/type/size validation, fixed emitted field set, fail closed.
- **[Risk] Reasoning item arrives only on `.done` or fields differ between added/done.** → Track by output index/item id and emit the latest complete item exactly once at lifecycle completion.
- **[Risk] Hidden block trips content detectors.** → It is a non-text/non-tool semantic block; never feed encrypted bytes into text scanning.
- **[Risk] Claude Code strips thinking outside a tool trajectory.** → Scope acceptance to the proven tool-result trajectory; do not claim persistence beyond Claude's documented thinking rules/compaction.
- **[Trade-off] The private envelope is bridge-specific.** → It is confined to the bridge's own `/cc→Responses` round trip and uses a standard hidden transport block; this is safer than visible markers or server-side state.

## Migration Plan

1. Freeze T3/T2 contract and mutation failures.
2. Add the isolated envelope codec and source-provenance plumbing.
3. Emit complete hidden blocks and decode them on Claude→Responses only.
4. Run full regression, captured replay, backend field probe, and real-client multi-turn acceptance.
5. Publish both AOT executables and document the durable protocol fact.

Rollback is a code revert; existing sessions without the carrier remain stateless as before. A session containing an unsupported newer carrier fails closed rather than silently corrupting reasoning state.

## Open Questions

- Whether gpt-5.6 siblings accept the same replay minimum; no sibling claim is made without individual probes.
- Whether Claude Code preserves the carrier across manual compaction/resume beyond an active assistant tool trajectory; this change guarantees only the live-proven trajectory and can extend after a separate experiment.
