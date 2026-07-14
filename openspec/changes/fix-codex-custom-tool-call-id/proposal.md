## Why

Codex desktop (gpt-5.6, `/codex/responses`) 400s on the turn AFTER an `exec`
call with:

```
Invalid 'input[N].id': 'item_1'. Expected an ID that begins with 'ctc'.
```

Root cause — **the IR round-trip is not id-preserving for `custom_tool_call`.**
The Responses↔Responses path (codex/gpt-5.6) is essentially a passthrough that
detours through the Anthropic-shaped IR; anything the upstream emitted and Codex
echoes back must survive that round-trip byte-consistent. It does not for the
`custom_tool_call` item id:

- **T3 (upstream resp → IR)** read only `call_id`/`name` and used `call_id` as the
  IR `tool_use` block id. Copilot's real `custom_tool_call` item id — a
  `ctc_`-prefixed Responses item id — was **dropped**.
- **T4 (IR → Codex-facing resp)** had no original id to emit, so it fabricated
  `_itemId = "item_" + _outputIndex` → `item_1`.
- Codex stored `item_1`, echoed it on the next turn; T1 classified the
  `custom_tool_call` as an unmodeled `ResponsesUnknownItem` → verbatim passthrough;
  T2 wrote it through unchanged → `item_1` reached Copilot → **400**.

`message` and `function_call` echoes never hit this: both are rebuilt from typed
fields on the T2 side and never write an `id`, so a fabricated/echoed id can't
leak. Only `custom_tool_call` leaked, because it rides the verbatim passthrough
lane.

Grounded in the wire (request-traces 2026-07-14): the real upstream id is
`ctc_0679bd5b…` (captured on `022622-0499`), and it appears ONLY on the
`custom_tool_call_input.delta`/`.done` events' `item_id` — the
`output_item.added`/`.done` ids are rolling encrypted blobs. Deterministic, not
flaky: 6 `ctc`-prefix 400s across 869 morning responses, first at `022629-0500`,
the very next request after the exec was generated. Copilot checks the `ctc`
PREFIX only (its own output id is a fresh `ctco_<uuid>` it accepts), so a
conforming id need not equal the original.

## What Changes

- **T3 captures the real `ctc_` id.** `ResponsesToAnthropicStream` reads the
  `item_id` off the `custom_tool_call_input.delta`/`.done` events (gated on the
  `ctc` prefix — only a real Copilot id is the value the echo must reproduce) and
  rides it out on the `content_block_stop` event as the bridge-internal marker
  `bridge_custom_tool_call_id`. Mirrors the existing `reasoning_id` /
  `bridge_tool_namespace` marker pattern. Buffered `BufferedResponsesToAnthropic`
  does the same on the tool_use block.
- **T4 emits a `ctc`-prefixed id.** `IrToResponsesOutboundAdapter` gives the
  `custom_tool_call` a deterministic `ctc_<call-id-suffix>` synthesis on the
  in-progress `output_item.added` (the real id isn't known until block-stop), then
  overrides the COMPLETED item's id with the real captured `ctc_` id from the
  marker. Both begin with `ctc`, so the echo is accepted whichever id Codex keys
  off; `added ≠ done` mirrors real upstream. A plain function/message item is
  unaffected (keeps `item_N`, which T2 rebuilds without an id). Buffered
  `BufferedAnthropicToResponses` mirrors this.
- **No marker leak.** `ClaudeCodeOutboundAdapter` scrubs the new
  `bridge_custom_tool_call_id` from the `content_block_stop` event (streaming) and
  the tool_use block (buffered) on the CC→gpt route, so it never reaches
  `claude.exe` — same rule as the other two bridge-internal markers.

- **Behavior change (a bug fix, not a break):** a `custom_tool_call` echoed on a
  follow-up turn no longer 400s; the exec loop survives past turn 1 on the Codex
  desktop path.

## Impact

- Affected specs: `codex-responses-endpoint`.
- Affected code: `Pipeline/Strategies/Codex/ResponsesToAnthropicStream.cs`,
  `Pipeline/Strategies/Codex/BufferedResponsesToAnthropic.cs`,
  `Pipeline/Adapters/Codex/IrToResponsesOutboundAdapter.cs`,
  `Pipeline/Adapters/Codex/BufferedAnthropicToResponses.cs`,
  `Pipeline/Adapters/ClaudeCode/ClaudeCodeOutboundAdapter.cs`.
- Tests: new `CodexCustomToolCallIdRoundTripTests`; additions to
  `ClaudeCodeBufferedResponsesAdapterTests`.
- No new dependency, no wire change for `message`/`function_call`/text paths
  (byte-identical); the only wire delta is the `custom_tool_call` item id, now
  `ctc`-prefixed instead of `item_N`.
