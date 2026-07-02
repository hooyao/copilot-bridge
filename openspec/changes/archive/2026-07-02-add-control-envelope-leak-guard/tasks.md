# Implementation Tasks

## 1. Automaton

- [x] 1.1 Add `ControlEnvelopeLeakAutomaton` (KMP-based, char-fed, O(1) state,
  retains no content) with fence tracking, `Tripped` latch, and `MatchedSubject`.
- [x] 1.2 Sub-matchers: `TaskNotificationMatcher` (closed `<task-id>` + one proof
  child), `AttributeEnvelopeMatcher` (teammate/channel/cross-session, non-empty
  required attribute), `TickMatcher` (non-empty inner). Bounded fail-open buffers.
- [x] 1.3 `<channel>` matcher must not trip on `<channel-message>` (exact close tag).

## 2. Detector wiring

- [x] 2.1 `ToolLeakDetector` holds both automata; reset both at
  `content_block_start` with the same `trackFences` selection.
- [x] 2.2 Feed each scanned character to both; abort on either trip.
- [x] 2.3 Broaden the detection-point Warning field from `tool=` to `subject=`
  (tool name or control-envelope subject); keep exactly one Warning, never log
  leaked content.

## 3. Tests (contract-derived, mutation-checked)

- [x] 3.1 `ControlEnvelopeLeakAutomatonTests`: positives for all five envelopes;
  `MatchedSubject` naming; every-split-point invariance; char-by-char; overlapping
  restart; fenced-not-leak + fences-not-tracked-detects; per-block reset; unclosed
  and missing-proof negatives; empty tick; `<channel-message>` guard; bounded
  fail-open; multiple envelopes in one block.
- [x] 3.2 `ResponseInspectionStageTests`: streaming + buffered task-notification
  abort; teammate-message abort; disabled inert; thinking-disabled not detected;
  detection Warning names subject/block/action and not the leaked text.
- [x] 3.3 Mutation-check the new contract tests (proof requirement, tick
  emptiness, fence gating, detector wiring) go red when the product code is broken.

## 4. Docs / specs

- [x] 4.1 `tool-leak-guard` spec: broaden purpose; add control-envelope detection
  requirement + scenarios; preserve the `<invoke>` contract.
- [x] 4.2 `observability` spec: broaden the logging requirement to name the leaked
  subject (tool or control envelope); keep "exactly one Warning" / "no leaked
  content".
- [x] 4.3 `docs/pipeline-design.md` §6.1; `README.md` (auto-repair + diagnostics
  wording); `ToolLeakGuardOptions` XML comments.

## 5. Verification

- [x] 5.1 `dotnet test tests/CopilotBridge.UnitTests` green; build clean (0 warnings).
- [x] 5.2 Solution-wide `dotnet test --filter "Category!=Integration"` green.
