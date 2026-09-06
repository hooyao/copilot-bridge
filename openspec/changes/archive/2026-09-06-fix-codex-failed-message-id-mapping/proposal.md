## Why

The stable Codex message-id fix still reconstructs a failed terminal by matching the synthesized `output[]` array position to Copilot's native `output_index`. When a reasoning item precedes a completed message, T4 intentionally omits that reasoning carrier from synthesized output, shifts the message to slot zero, and can emit a fresh `item_0` identity instead of the canonical message id already streamed to Codex.

## What Changes

- Preserve each synthesized completed output item's native Responses output index independently of its compacted terminal array position.
- Use that provenance when normalizing message ids in a synthesized `response.failed` terminal.
- Add a regression for `reasoning[0] -> message[1] -> response.failed` and mutation-check it against the faulty positional lookup.
- Exercise the failed-stream path through a real headless Codex client and inspect client-owned dispatch evidence.
- Make the mirrored `ship-pr` status scripts surface current-head Copilot review bodies containing suppressed findings so they cannot be mistaken for a zero-comment approval.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-response-fidelity`: Clarify that a synthesized failed terminal must retain canonical message identity even when omitted native output items change terminal array positions.

## Impact

- `IrToResponsesOutboundAdapter` synthesized terminal bookkeeping and message-id normalization.
- Codex response-fidelity unit/API-contract and real-client behavior evidence.
- Mirrored `.agents` and `.claude` `ship-pr` status scripts plus repository compatibility tests.
- No public configuration, request shape, or successful native-stream behavior changes.
