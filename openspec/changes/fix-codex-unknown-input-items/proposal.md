## Why

The Responses `input[]` union is **open-ended**: gpt-5.6's multi-agent, tool-search,
and compaction features keep adding item types. openai/codex `ResponseItem.ts` lists
`agent_message`, `tool_search_call`, `custom_tool_call`, `custom_tool_call_output`,
`web_search_call`, `image_generation_call`, `compaction`, `context_compaction`,
`local_shell_call`, … — far beyond what the bridge models.

The bridge deserialized `input[]` with a **closed** `[JsonPolymorphic]` whitelist
(default `UnknownDerivedTypeHandling = FailSerialization`), so ANY item type not in
the whitelist threw `Polymorphism_UnrecognizedTypeDiscriminator` → **HTTP 400, before
T1 even ran**. This is the root cause of a whole family of "the user hits it every
run, the tests never catch it" bugs — because a test written by someone who doesn't
know a type exists will never include it. Four gpt-5.6 tool bugs shipped this way; the
fifth was `agent_message` (a multi-agent inter-agent message: `Polymorphism_
UnrecognizedTypeDiscriminator, agent_message Path: $.input[17]`), live-reproduced on a
real codex.exe multi-agent session (`request-traces/20260711-124357-0014`).

The methodological failure was verifying with a single synthetic sample instead of the
real corpus. The fix therefore has two parts: a code fix that ends the whack-a-mole,
and a verification gate that replays the whole real corpus.

## What Changes

- **Universal unknown-item passthrough (the root-cause fix).**
  `ResponsesInputItemListConverter` (a custom `JsonConverter` on
  `ResponsesRequest.Input`) reads each item as a `JsonElement`, peeks its `type`, and:
  a KNOWN type binds through the source-generated polymorphic metadata (unchanged); an
  UNKNOWN type is captured whole as a new `ResponsesUnknownItem` (opaque, byte-faithful)
  and re-emitted VERBATIM by T2 — never rejected. Array ORDER is preserved.
- **`agent_message` modeled explicitly (belt-and-suspenders).** It's in live traffic and
  carries an `encrypted_content` blob, so it gets a typed `ResponsesAgentMessageItem`
  (`author`/`recipient`/opaque `content`), carried and re-emitted byte-faithfully
  (`WriteRawValue(GetRawText())` — the encrypted bytes are never mutated). Authoritative
  schema: openai/codex `ResponseItem.ts` + `AgentMessageInputContent.ts`.
- **Order-preserving re-emission.** T1 records each passthrough item's position (the
  count of IR messages before it) in a `passthrough_items` bag array; T2 re-inserts each
  at that point in the outbound `input[]`, so an `agent_message` between two messages
  stays between them.
- **Verification gate (the process fix).** `CodexInboundCorpusReplayTests` replays EVERY
  real `/codex/responses` inbound body from a live session's `request-traces` through the
  actual deserializer + T1→T2, asserting none throws `Polymorphism_
  UnrecognizedTypeDiscriminator`, and enumerates every item type seen. A single sample is
  no longer treated as full verification.

## Impact

- Ends the per-type 400 whack-a-mole: any future unmodeled `input[]` type is forwarded,
  not rejected.
- No change to Claude Code (`/cc`) — the converter is on the Codex `ResponsesRequest`.
- No change to modeled types — known items bind exactly as before (byte-identical).
- `encrypted_content` (agent context ciphertext) is carried byte-faithfully.

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: T1 SHALL accept ANY `input[]` item type — modeled ones
  bind to their records, unmodeled ones are captured opaquely — and T2 SHALL re-emit
  every item VERBATIM and IN ORDER, so an unmodeled or new item type is forwarded to
  Copilot instead of 400'ing the request with `Polymorphism_UnrecognizedTypeDiscriminator`.
  `agent_message` (with its `encrypted_content`) SHALL round-trip byte-faithfully.
