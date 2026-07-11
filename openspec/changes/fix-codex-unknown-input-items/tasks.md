# Tasks — universal unknown-input-item passthrough + agent_message (gpt-5.6)

## 1. Ground the contract
- [x] 1.1 Reproduce the live 400: real codex.exe multi-agent session →
  `Polymorphism_UnrecognizedTypeDiscriminator, agent_message Path: $.input[17]`
  (`request-traces/20260711-124357-0014`, before T1 runs → `requested=?` `duration_ms=4`).
- [x] 1.2 Enumerate the whole corpus: every `input[]` item type across all codex inbound
  this session (message/function_call/function_call_output/additional_tools/agent_message;
  nested parts input_text/output_text/encrypted_content).
- [x] 1.3 Authoritative schema from openai/codex `ResponseItem.ts` +
  `AgentMessageInputContent.ts`: the union is open-ended (10+ types); agent_message =
  {type,id?,author,recipient,content:[input_text|encrypted_content]}.

## 2. Fix (root cause + belt-and-suspenders)
- [x] 2.1 `ResponsesInputItemListConverter`: custom converter on `ResponsesRequest.Input`
  — known type → source-gen polymorphic bind; unknown → `ResponsesUnknownItem` (opaque).
  Order preserved. AOT-clean (no reflection).
- [x] 2.2 Model `ResponsesAgentMessageItem` (author/recipient/opaque content) +
  register the `agent_message` discriminator.
- [x] 2.3 `ResponsesUnknownItem` record (Type + Raw JsonElement); register in JsonContext.
- [x] 2.4 T1: collect agent_message + unknown items into an ordered `passthrough_items`
  bag array recording each one's position (IR-message count before it). encrypted_content
  carried via `WriteRawValue(GetRawText())`.
- [x] 2.5 T2: re-insert passthrough items IN ORDER at their recorded positions in the
  outbound `input[]`; skip `passthrough_items` as a top-level bag field.

## 3. Tests (from contract, mutation-checked)
- [x] 3.1 `CodexUnknownItemPassthroughTests` (4): agent_message verbatim + encrypted blob
  intact; unknown type (tool_search_call) doesn't throw + carried; order preserved
  (agent_message between messages); trailing unknown (compaction) emitted at end.
- [x] 3.2 Mutation-check: converter unknown-branch throw reddens the unknown-type tests;
  the agent_message/order tests (modeled type) correctly stay green.
- [x] 3.3 Full unit suite green (847).

## 4. Verification gate (the process fix — no more single-sample "done")
- [x] 4.1 `CodexInboundCorpusReplayTests`: replayed 1213 real `/codex/responses` inbound
  bodies (from 9298 trace files) through deserializer + T1→T2 — 0 failures, every item
  type (message/function_call/function_call_output/additional_tools/agent_message) covered,
  including the 2 agent_messages that 400'd pre-fix.
- [x] 4.2 Real bytes end-to-end: replayed the actual agent_message 400 request through the
  FIXED bridge → real Copilot → 200, response.completed, no Polymorphism 400, and the audit
  shows encrypted_content forwarded byte-intact (`CodexAgentMessageHeadlessTests`).
- [ ] 4.3 **UNVERIFIED (needs the user):** a real DESKTOP codex.exe multi-agent task
  (spawn_agent → agent_message round-trips) against the redeployed bridge. `codex exec` CLI
  doesn't drive the multi-agent collaboration path — only the desktop app does — so this
  leg can't be run headlessly. The AOT exe is rebuilt (publish/, 12.28 MB) for the user.

## 5. Ship
- [x] 5.1 code-review the diff (5-agent review, 0.4.13-beta batch). Findings addressed:
  `agent_message` typed model DELETED (required-field fragility) → rides `ResponsesUnknownItem`;
  `KnownTypes`↔`[JsonDerivedType]` drift now test-guarded; passthrough re-emit switched to
  `WriteRawValue(GetRawText())`; malformed `after` defaults to end-append. See
  `fix-codex-exec-custom-tool-call` for the full review-hardening set.
- [ ] 5.2 `/ship-pr` → 0.4.13-beta (folds in the namespace fix).
