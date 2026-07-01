# Implementation Tasks

## 1. Configuration & options

- [x] 1.1 Add `ToolLeakGuardOptions` in `Hosting/Options/` with `Enabled` (default true), `PreserveStream` (default true), `Signal` (enum `{OverloadedError, ApiError}`, default `OverloadedError`), `ScanThinking` (default true), `MaxScanChars` (default 10000).
- [x] 1.2 Add the `Pipeline:Detectors:ToolLeakGuard` section to `appsettings.json` with per-knob `_Xxx` comments (copy the block from design.md D5).
- [x] 1.3 Bind and register the options in the hosting setup (mirror `UpstreamRetryOptions`/`ResponseModelRewriteOptions` registration); add to `JsonContext` only if a new DTO requires source-generated serialization.

## 2. Detection core (automaton, delivery-agnostic, unit-testable)

- [x] 2.1 Implement the single-pass leak automaton: fixed token set (`` ``` ``, `<invoke name="`, `">`, `<parameter`, `</parameter>`, `</invoke>`), per-block O(1) state (`inFence`, `invokeOpen`, bounded `name` capture, `paramOpen`/`paramClose`, node). Fires on `</invoke>` iff `invokeOpen && paramClose≥1 && paramOpen==paramClose && name∈tools[] && !inFence`. No content retention, no regex.
- [x] 2.2 Implement the shared error-object construction: given `Signal`, produce the Anthropic error type string (`overloaded_error`/`api_error`) and the corresponding HTTP status (529/500). Reuse existing `ErrorResponse`/`ErrorBody` where possible.
- [x] 2.3 Exhaustive automaton unit tests (contract-derived, mutation-checked). The automaton is the highest-risk component — cover all of:
  - **Positives**: the 5 real trace shapes as fixtures; a minimal closed `<invoke name="X"><parameter name="p">v</parameter></invoke>`; multiple `<parameter>`s; `<invoke>` preceded by arbitrary prose and the drifting token (`court`, `call`, none).
  - **Splitting (the core property)**: the SAME leak fed with EVERY possible split boundary — char-by-char (each character its own delta), split mid-token (`<inv`|`oke`), split inside the name (`Re`|`ad`), split inside `</invoke>`, split inside the fence marker (`` ` ``|`` `` ``). All must detect identically. Property-style: for a known leak string, assert detection is invariant across all N split points.
  - **Failure-edge / restart**: `<<invoke name="X">…` (naive reset-to-0 would miss it — MUST detect); `<inv<invoke name="X">…`; repeated false-starts `<in<in<invoke…`; a partial `<invoke name=` that never closes then a real one later in the same block.
  - **Negatives**: unbalanced (`<invoke>`>`</invoke>`); `<invoke>` with zero closed `<parameter>`; open `<parameter` never closed; tool name NOT in `tools[]`; fenced (```` ``` ````) closed invoke; nested/oddly-placed fences (`` ` ``, ``` `` ```, ```` ``` ````) toggling correctly; thinking block with `ScanThinking=false`.
  - **Boundary / reset**: signature straddling two blocks (open in block A, close in block B) → NOT detected; state fully reset on `content_block_start`; a clean block after a rejected partial in the previous block.
  - **Name capture bound**: a `<invoke name="` followed by a very long unterminated name → fail-open (pass through), no unbounded buffer.
  - Mutation-check each: break the product automaton (e.g. remove the failure edge, drop the `paramOpen==paramClose` check, ignore fences) and confirm the relevant test goes red.

## 3. Detection framework + tool-leak detector

- [x] 3.1 Define `IResponseDetector` with per-request lifecycle (instantiated fresh per request — no shared streaming state), exposing `Name`, `Enabled`, a streaming entry `InspectEvent(event, ctx)` and a buffered entry `InspectBuffered(body, ctx)`. Define `DetectionAction` with `Abort(signal)`, `DropEvent`, `RewriteEvent(...)` implemented; `RewriteBlock(...)` defined but not implemented (future JSON-repair extension point). Register detectors via a per-request factory (not `AddSingleton`); stateless deps (`ILogger<T>`, options) still from DI.
- [x] 3.2 Implement `ResponseInspectionStage : IResponseStage<MessagesRequest>`: parse the SSE sequence once, track block index/type and fence state, fan events + per-block text spans out to the ordered detector list; render `Abort`/`DropEvent`/`RewriteEvent`. Also handle the buffered path for buffered-capable detectors. No-op (no allocation) when no detector is enabled.
- [x] 3.3 Implement `ToolLeakDetector : IResponseDetector` wrapping the 2.1 automaton; subscribes to text/thinking blocks (thinking gated by `ScanThinking`); returns `Abort(signal)` on detection.
- [x] 3.4 Register the stage + detectors in the response pipeline; DI-register detectors so adding one is register-only. Reproduce the existing order (DONE-filter before model-rewrite).
- [x] 3.5 Streaming render of `Abort`: relay events unchanged until an action fires, then inject an SSE `error` event with the constructed signal and stop enumerating; leave HTTP status untouched. Add a distinct Debug/Warning log line and a summary-log field (`toolLeakDetected`).

## 4. Migrate existing stages into the framework (consolidate now)

- [x] 4.1 Reimplement `DoneFilterStage` as `DoneFilterDetector` (streaming `DropEvent` on the `message`/`[DONE]` event, still pushing to `DroppedEvents`). Remove the standalone stage + its DI/pipeline registration.
- [x] 4.2 Reimplement `ResponseModelRewriteStage` as `ModelRewriteDetector`, covering BOTH streaming (`RewriteEvent` on `message_start`) AND buffered (rewrite top-level `model`) paths. Remove the standalone stage + its DI/pipeline registration.
- [x] 4.3 Confirm the response stream is now wrapped ONCE (single `ResponseInspectionStage`) and the detector order matches the prior stage order.
- [x] 4.4 Regression gate: `ResponseModelRewriteStageTests` (and any DoneFilter coverage) pass unchanged against the migrated detectors, or are re-expressed against the framework with the same assertions — byte-identical `[DONE]` filtering and model rewriting.

## 5. Buffered delivery path (`PreserveStream=false`)

- [x] 5.1 In `ResponseInspectionStage`, when `PreserveStream` is false, drain the upstream `EventStream` into memory, reconstruct per-block content, run detectors.
- [x] 5.2 On dirty: set `ctx.Response.Status` (529/500 from signal), set `ctx.Response.BufferedBody` to the Anthropic error body, flip `Mode` to `Buffered`. On clean: replay buffered events as the stream (or hand back a buffered body) so the endpoint delivers normally.
- [x] 5.3 Adjust `ClaudeCodeMessagesEndpoint` so a stage that flipped `Mode`/`Status` before headers are sent takes the buffered write path. Guard strictly so the default (`PreserveStream=true`) leaves the streaming relay untouched.

## 6. Tests (contract-derived)

- [x] 6.1 Stream path: given a stream with a closed in-tools unfenced `<invoke>` in a text block → asserts exactly one injected `error` event of the right type, no further upstream events, status stays 200. Mutation-check.
- [x] 6.2 Stream path negatives: unbalanced/prose, fenced, fictitious tool name, `ScanThinking=false` thinking block → stream passes through byte-for-byte unchanged.
- [x] 6.3 Per-block behavior: leak split across many deltas detected on close; block-boundary non-assembly; automaton retains no content.
- [x] 6.4 `Enabled=false` → stage/detectors inert (no scanning, no allocation) — assert via a passthrough-identity test.
- [x] 6.5 Buffered path: dirty → HTTP 529/500 + error body, no leaked content; clean → unchanged body.
- [x] 6.6 Signal mapping: `OverloadedError`→overloaded_error/529, `ApiError`→api_error/500 in both delivery modes.
- [x] 6.7 Migration regression: `[DONE]` still dropped (and recorded in `DroppedEvents`); model rewrite still applied in both streaming (`message_start`) and buffered paths — byte-identical to pre-migration.

## 7. Verification & docs

- [x] 7.1 Run `dotnet test --filter "Category!=Integration"` green.
- [x] 7.2 AOT sanity: publish per the CLAUDE.md PowerShell block; confirm no reflection-JSON warnings and the exe still builds (no new trim/AOT breakage from the new DTO/automaton).
- [x] 7.3 Fold the durable architectural fact (the leak, the structural signature, the detection framework) into `docs/` where it belongs; keep `CLAUDE.md`/`AGENTS.md` as constitution (no status).
