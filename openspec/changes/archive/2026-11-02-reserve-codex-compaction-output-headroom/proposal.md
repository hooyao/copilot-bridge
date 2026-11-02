# Proposal

## Why

A production Codex session remained below the advertised 892,000-token compaction threshold, then accumulated a long reasoning response without final usage before the next request exceeded Copilot's prompt limit. The current threshold does not reserve space for one maximum-size response to return as history.

## What Changes

- Cap the existing automatic compaction threshold by maximum prompt minus maximum output minus a 16,000-token policy margin whenever that budget supports a positive whole-thousand threshold.
- Preserve truthful context capacities, reviewed fallback behavior, model availability, and client-owned catalog fields; retain the existing threshold when a full output reserve cannot fit.
- Verify the projected contract, boundary cases, actual Codex catalog consumption, automatic compaction, and subsequent tool execution.
- Keep reasoning-output guards and failed-stream history recovery outside this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-model-catalog`: Reserve one output's history growth plus a policy margin in the automatic compaction threshold where representable.

## Impact

Changes the catalog projector, catalog contract tests, a focused real-Codex behavior scenario, and capacity documentation. No new dependencies, inference wire fields, authentication changes, or deployment of the user's running bridge.
