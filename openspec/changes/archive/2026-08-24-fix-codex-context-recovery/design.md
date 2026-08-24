## Context

The command-auth model catalog currently projects validated Copilot limits into Codex and chooses `auto_compact_token_limit = min(90% of total context, 97.5% of maximum prompt)`, rounded down to a whole thousand tokens. For the current 1,050,000 / 922,000 class this is 898,000. A production `gpt-5.6-sol` capture shows a tool- and encrypted-reasoning-heavy parent thread entering automatic pre-turn compaction only after the compaction request itself was no longer admitted.

Copilot returns that rejection before an SSE stream starts as HTTP 400 with `error.code=invalid_request_body` and the confirmed context-window sentence. Codex 0.147 and current upstream classify `ContextWindowExceeded` only from a Responses `response.failed` event whose error code is `context_length_exceeded`; the HTTP 400 becomes a generic bad request. Codex therefore never reaches its compaction recovery branch that trims oldest history and retries. The bridge already recognizes the exact Copilot error for the Claude Code edge, but deliberately excludes native `/codex`.

The bridge must remain Native AOT safe, preserve raw Copilot evidence, avoid silently editing request history, and leave every unrelated 400 untouched.

## Goals / Non-Goals

**Goals:**

- Give every valid uplifted model a per-model automatic-compaction threshold no greater than 85% of total context.
- Convert only the confirmed pre-stream native Codex context rejection into the Responses failed terminal Codex recognizes.
- Keep raw upstream and adapted downstream audits independently truthful.
- Prove compact/retry behavior through a real Codex client and its own log.

**Non-Goals:**

- Guess the exact token count of a rejected production request when Copilot supplies no usage.
- Truncate, summarize, reorder, or otherwise mutate client history inside the bridge.
- Rewrite arbitrary 400s, non-streaming native API calls, Claude Code responses, or non-Responses routes.
- Replace user-owned explicit `model_context_window` or `model_auto_compact_token_limit` settings in `config.toml`.

## Decisions

### 1. Project an 85%-of-total policy, retaining the prompt guard

`CodexCatalogProjector` will compute the compact limit as the smaller of 85% of validated total context and the existing 97.5% maximum-prompt guard, then round down to a whole thousand tokens. The result must remain positive and strictly below maximum prompt. For 1,050,000 total / 922,000 prompt the projected value becomes 892,000.

This remains a catalog-owned per-model default rather than a global value written by `config codex`; a global token number cannot represent 85% across both 1M-class and smaller models, and explicit user overrides remain user-owned. Command auth installed by `config codex` continues to be the mechanism that lets Codex fetch the projection.

Alternative: change only the prompt percentage. Rejected because the requested policy is relative to total model context and the independent prompt guard still protects models whose prompt ceiling is the tighter constraint.

### 2. Share the existing exact bounded Copilot context classifier

The existing status/vendor/body classifier will be reusable by both client edges while each edge keeps its own protocol adaptation. A match still requires HTTP 400, a resolved Copilot Responses backend, a bounded parseable JSON body, `error.code=invalid_request_body`, and the exact confirmed message. Raw substring matching remains forbidden.

Alternative: classify by request size or all 400s from `gpt-*`. Rejected because invalid tool schemas, model access failures, and malformed requests must not trigger destructive client compaction.

### 3. Adapt at the native Codex client edge

For a streaming `/codex/responses` request whose buffered upstream result matches the classifier, `CodexResponsesEndpoint` will preserve the raw upstream snapshot, then emit a client-facing HTTP 200 event stream containing exactly one `response.failed` terminal with `error.code=context_length_exceeded` and a bounded bridge-owned message. Upstream `Content-Type` and `Content-Length` will not leak into the adapted downstream headers. `X-Reasoning-Included` remains absent because the upstream result was not successful.

The endpoint is the correct boundary: changing `CopilotResponsesStrategy` would conflate raw upstream facts with client compatibility, while changing the shared IR would make a pre-stream transport rejection look model-generated. The endpoint already owns native Responses framing, response status, and downstream audit separation.

Alternative: return HTTP 400 with only a rewritten error code. Rejected because supported Codex versions classify non-2xx HTTP errors before the SSE parser and still surface a generic bad request. Alternative: have the bridge trim history. Rejected because conversation ownership belongs to Codex and its recovery loop already defines which items to remove.

### 4. Keep near misses byte- and status-faithful

Non-streaming requests, other paths/vendors/statuses/codes/messages, malformed JSON, and bodies over the classifier bound retain the original response. The Claude Code prompt-too-long adapter remains unchanged and distinct.

### 5. Verify the recovery contract at three levels

Contract-first unit and endpoint tests will cover the 85% calculation, exact adaptation, audit split, single-terminal framing, absence of the reasoning header, and near-miss passthrough. A minimized de-identified captured-byte ApiContract case will retain the production request metadata that identifies automatic pre-turn compaction without committing the user's history.

A deterministic ClientBehavior scenario will drive real Codex through a low-token-limit pre-turn compaction, inject the exact Copilot HTTP 400 on the first compact attempt, then accept the client's reduced retry and complete a real tool trajectory. PASS requires the per-run bridge trace plus Codex's own `logs_2.sqlite`: context failure recognized, compact retry observed with reduced history, matching tool call/output, no abort, and zero router/dispatch fatal.

## Risks / Trade-offs

- **[An adapted 400 hides the upstream HTTP status from the client]** -> Preserve status 400 and the exact body in `upstream-resp`; record status 200 plus the synthetic failed event only in `inbound-resp`.
- **[False-positive classification could discard valid history]** -> Require every exact classifier predicate and mutation-check every near-miss guard.
- **[85% still may not cover every future backend/tokenizer drift]** -> Retain the independent prompt guard and use the production capture to add a live boundary probe before claiming the threshold eliminates every overflow.
- **[A real client may retry one item at a time]** -> The behavior test verifies forward progress on a bounded deterministic history; the bridge does not second-guess Codex's trimming policy.
- **[Catalog caches delay adoption]** -> The changed projection produces a new ETag; restarted/refreshing command-auth clients pick it up, while explicit user overrides continue to take precedence.

## Migration Plan

No config-file schema or conversation migration is required. Deploy the bridge, re-run `config codex` only where command auth is not already present, and restart Codex so catalog refresh and client code both use the new behavior. Rollback restores the prior catalog formula and verbatim native 400 response; existing threads and caches remain readable.

## Open Questions

- The exact admission cliff for the supplied production body is not present in Copilot's error response. A live trimmed-prefix probe can measure it, but the user-selected 85% policy and exact recovery adapter do not depend on inventing that number.
