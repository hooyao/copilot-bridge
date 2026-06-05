# "Invalid tool parameters" — root cause: upstream omits a required tool field

**Status**: root-caused; bridge exonerated by unit + raw-stream evidence
**Symptom**: Claude Code TUI shows `⎿ Invalid tool parameters` after the
assistant tries to ask a question (e.g. "...有 3 个参数只有你能决定,确认后我直接开写:").
The assistant turn's `AskUserQuestion` tool call is rejected before the user
ever sees the question.
**Date**: 2026-06-05

## TL;DR

The streamed `AskUserQuestion` tool call arrives with the **`question` field of
each question object missing**. The bridge's input schema for `AskUserQuestion`
marks `question` as required, so Claude Code rejects the whole tool call with
"Invalid tool parameters".

**The field is missing in Copilot's raw upstream bytes — the bridge relays it
faithfully.** This is intermittent, non-deterministic output corruption from
opus-4.8's own tool-call generation on Copilot, not a bridge streaming bug.

## Evidence chain

### 1. Production audit: the loss is intermittent

Across the 6 `AskUserQuestion` tool calls the bridge produced in one session
(reassembling each from its `inbound-resp` SSE trace):

| trace | questions | questions WITH `question` field |
|---|---|---|
| 0016 | 3 | 2  ← one missing |
| 0023 | 3 | 3 |
| 0041 | 1 | 1 |
| 0043 | 1 | 1 |
| **0079** | 2 | **0**  ← all missing (the reported failure) |
| 0082 | 2 | 2 |

Same bridge binary, same `SseParser`, same code path — yet the field is present
in 4, partial in 1, absent in 1. **A deterministic bridge bug cannot produce a
non-deterministic distribution.**

### 2. Unit test: the bridge round-trip preserves the field

`tests/CopilotBridge.UnitTests/SseRoundTripTests.cs` feeds Anthropic-shaped
tool-call SSE through the exact path the bridge uses
(`SseParser.Create(stream).EnumerateAsync()` → `WriteSseEventAsync`
re-serialize → re-parse) and asserts the `question` field survives byte-for-byte
across:

- fragment boundaries that split mid-key right before `question`,
- CRLF line endings (Copilot emits `\r\n`),
- an empty first `partial_json` fragment (the trace's `frag[0]` was `""`).

All pass — the bridge never drops the field for any well-formed input.

### 3. Raw-stream replay: the field is missing in Copilot's own bytes

`tests/CopilotBridge.Playground/InvalidToolParamsReplayTests.cs` forces opus-4.8
to emit an `AskUserQuestion` call (via `tool_choice`) and reads the response with
a raw `StreamReader` — **bypassing `SseParser` entirely**. It reassembles the
`input_json_delta.partial_json` fragments straight from the wire and checks for
the `question` token.

When the field is missing in the reassembled raw stream, the omission happened
**before** any bridge parsing — i.e. Copilot put incomplete tool JSON on the
wire. (The omission is low-frequency; a clean 8-run batch can come back 8/8
present. The production rate was ~1–2 in 6.)

### Why the earlier "history-only" guess was wrong

A first pass concluded the corrupted tool calls were replayed history from an
older bridge build. That was disproven: `toolu_01DQNsbWJr` (the exact failure
the user hit) appears in `20260605-145016-0079-inbound-resp.json` — a response
produced by the **current** build. The bug is live; it's just upstream.

## What the bridge does / doesn't do

- The `/cc/v1/messages` streaming path parses Copilot's SSE into events and
  re-serializes them downstream. This is **not** raw byte passthrough, but it is
  **field-preserving** (proven by `SseRoundTripTests`). `ResponseModelRewriteStage`
  only touches `message_start`'s `model` field and is a no-op when the requested
  and resolved model match (always true for opus-4.8 direct).

## Options

This is upstream output corruption, so a true fix is not available to the bridge.
Possible mitigations, in increasing intrusiveness:

1. **Do nothing (recommended).** The blast radius is one rejected tool call;
   Claude Code surfaces "Invalid tool parameters" and the user/agent retries.
   The bridge stays faithful to what Copilot emits. Low frequency.
2. **Detect-and-log.** Add an optional response-side check that parses completed
   `tool_use` blocks and logs a structured warning when a tool call's reassembled
   input fails the tool's declared `input_schema` `required` set. Observability
   only — does not alter the bytes. Useful to measure the real rate in prod.
3. **Repair (NOT recommended).** Synthesizing a missing `question` value would
   fabricate user-facing content and risk masking a deeper upstream regression.
   Faithfulness wins, same principle as the resume-`[1m]` decision in
   `docs/context-window.md`.

## Files

- `tests/CopilotBridge.UnitTests/SseRoundTripTests.cs` — proves the bridge
  round-trip preserves tool-call fields.
- `tests/CopilotBridge.Playground/InvalidToolParamsReplayTests.cs` — measures
  the upstream `question`-field omission rate from raw bytes.
