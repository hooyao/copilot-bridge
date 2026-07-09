# Tasks — add-upstream-idle-timeout

Baseline self-check first (per project memory: worktree/agent bases can lag). Confirm HEAD is at or after `0415855` and that `CopilotMessagesPassthroughStrategy.StreamEventsAsync` still reads via `SseParser.EnumerateAsync(ct)` with only `httpCtx.RequestAborted` before writing any code.

## 1. Options + configuration

- [x] 1.1 Add `Hosting/Options/UpstreamTimeoutOptions.cs` with `FirstByteTimeoutSeconds` (default 240) and `StreamIdleTimeoutSeconds` (default 60), each `<= 0` ⇒ disabled. XML-doc each field with its grounding (mirror `UpstreamRetryOptions` doc style).
- [x] 1.2 Bind it in `BridgeServiceCollectionExtensions.AddBridgeServer`: `services.Configure<UpstreamTimeoutOptions>(config.GetSection("Pipeline:UpstreamTimeout"))`.
- [x] 1.3 Add the `Pipeline:UpstreamTimeout` block to `appsettings.json` with a verbose `_comment` (inactivity not total-duration; two budgets; `<=0` disables; read-at-startup ⇒ RESTART to apply), placed next to `UpstreamRetry`.

## 2. Timeout signal type

- [x] 2.1 Add `Copilot/UpstreamTimeoutException.cs` — a plain `Exception` (NOT a `TaskCanceledException`/`OperationCanceledException`) carrying the phase (`FirstByte` | `StreamIdle` enum) and the elapsed idle `TimeSpan`.
- [x] 2.2 Verify `TransientUpstreamError.Is` returns `false` for it (so the retry loop and the endpoint's transient branch do not swallow/mislabel it) — add an assertion in the exception's test if not already covered. [plain Exception matches no case + no inner chain ⇒ Is()==false by construction; asserted in test group 6.]

## 3. First-byte budget (per send attempt)

- [x] 3.1 Thread the first-byte budget into `CopilotClient.PostMessagesAsync` (via injected `IOptions<UpstreamTimeoutOptions>` on `CopilotClient`, read once into a field like `_retry`; do not change the public signature if the value can be read from the field).
- [x] 3.2 Inside the retry loop, when the budget is enabled, wrap each `http.SendAsync(...ResponseHeadersRead...)` in `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(budget)`; on cancellation where `linked.IsCancellationRequested && !ct.IsCancellationRequested`, throw `UpstreamTimeoutException(FirstByte, elapsed)`.
- [x] 3.3 Ensure the first-byte `UpstreamTimeoutException` is NOT caught by the transient-retry `when` clause (it must propagate out, terminal — do not re-send). Confirm each retried attempt gets a fresh linked CTS = full budget, and backoff `Task.Delay` is outside the armed window. [plain Exception ⇒ TransientUpstreamError.Is==false ⇒ `when` false ⇒ escapes; CTS created fresh per loop iteration; Task.Delay after disarm/return path.]
- [x] 3.4 When the budget is disabled (`<= 0`), take the original path (no linked CTS, no `CancelAfter`) — byte-identical to today. [early `return await http.SendAsync(..., ct)` when `firstByteBudget <= 0`.]

## 4. Stream-idle budget (per upstream event)

- [x] 4.1 In `CopilotMessagesPassthroughStrategy.StreamEventsAsync`, when the stream-idle budget is disabled, keep the exact current path (`SseParser.EnumerateAsync(ct)` via `await foreach`) — zero added allocation.
- [x] 4.2 When enabled, enumerate manually: create a linked CTS once; before each `MoveNextAsync` call `CancelAfter(budget)`; after an event arrives set `CancelAfter(Timeout.InfiniteTimeSpan)` (disarm) then `yield return` outside the try/catch. On cancellation where `linked.IsCancellationRequested && !ct.IsCancellationRequested`, throw `UpstreamTimeoutException(StreamIdle, elapsed)`.
- [x] 4.3 Pass the budget into the strategy (inject `IOptions<UpstreamTimeoutOptions>`), consistent with how the strategy reads other config. Preserve the existing `TeeReadStream`/capture behavior and stream/`resp` disposal in `finally`.

## 5. Surfacing: 504 pre-headers, retryable error mid-stream, summary

- [x] 5.1 First-byte timeout (headers not started): add `catch (UpstreamTimeoutException ex)` in `ClaudeCodeMessagesEndpoint.HandleAsync` BEFORE the transient and generic catches, AFTER the `OperationCanceledException when ct.IsCancellationRequested` client-cancel catch. `!Response.HasStarted` ⇒ `504 Gateway Timeout` + body; one `WARN` (no stack) naming phase + elapsed; set `summary.Error`.
- [x] 5.2 Mid-stream timeout (headers already sent): default action writes the retryable error event using `ResponseDetectionError.JsonWithMessage(Signal, message)` (the SAME builder RunawayGuard/ResponseLeakGuard use) as an SSE `error` event, then ends the stream. Wire result MUST be byte-identical to a RunawayGuard trip. NOTE it cannot go through `ResponseInspectionStage`'s `DetectionActionKind.Abort` — that stage is pull-based and an idle gap is the absence of an event; the injection happens at the strategy read site / endpoint catch, reusing only the shared error builder. Provide a config action to instead truncate (no error event). Retryable path verified against corrected CC behavior (memory `reference_cc_midstream_error_retry_conditional`): flag=1 ⇒ whole-turn retry; unset ⇒ non-streaming fallback.
- [x] 5.3 Add an `upstream_timeout` field to `RequestSummary` + render it on the summary line (phase token `first_byte|stream_idle` when tripped, absent/none otherwise), alongside `response_leak=`/`runaway=`. Set it in the catch/abort path.
- [x] 5.4 Confirm the client-cancel race: when both `ct` and the linked timer fire, the existing `OperationCanceledException when ct.IsCancellationRequested` branch wins (client cancel), NOT the timeout branch. [strategy throws UpstreamTimeoutException only when `!ct.IsCancellationRequested`; the client-cancel catch is ordered first and its `when` is true whenever ct fired.]
- [x] 5.5 Coordinate `StreamIdleTimeoutSeconds` default with CC's own watchdog (`CLAUDE_STREAM_IDLE_TIMEOUT_MS`, default 90s, opt-in via `CLAUDE_ENABLE_STREAM_WATCHDOG`): keep the bridge default at/below 90s so the bridge is the earlier deterministic actor, not racing an optional client timer. Document this in the `_comment`. [default 60 < 90; documented in options XML-doc + appsettings `_StreamIdleTimeoutSeconds`.]

## 6. Tests (from the CONTRACT — mutation-check each; a first-run pass is suspect)

- [x] 6.1 First-byte timeout: a fake `ICopilotClient`/`HttpMessageHandler` that never returns headers ⇒ `PostMessagesAsync` throws `UpstreamTimeoutException(FirstByte)` within ~budget; endpoint returns `504`; summary records first-byte timeout. Mutation-check by disabling the budget ⇒ no throw. [FirstByte_NeverArrives + Endpoint_FirstByteTimeout_Returns504; mutation-checked (neutered timer ⇒ test hangs/fails).]
- [x] 6.2 First byte just in time: headers arrive just under the budget ⇒ no throw, forwarding proceeds. [FirstByte_ArrivesBeforeBudget_Succeeds.]
- [x] 6.3 Backoff does not consume the budget: one transient failure + backoff, then a send that takes just under the budget ⇒ succeeds (proves per-attempt arming). [FirstByte_Backoff_DoesNotConsumeBudget.]
- [x] 6.4 Stream-idle timeout (default retryable): a fake upstream SSE source that emits N events then goes silent > budget ⇒ `StreamEventsAsync` aborts within ~budget of the last event; a retryable `overloaded_error` event is emitted then the stream ends; wire status stays 200; summary records stream-idle timeout. Assert the emitted event is byte-identical to a RunawayGuard abort. Mutation-check by disabling ⇒ no abort. [StreamIdle_UpstreamGoesSilent + Endpoint_MidStreamTimeout_Retry (asserts `event: error` + overloaded_error envelope + prior event); mutation-checked (skip injection ⇒ red; disable arming ⇒ no abort).]
- [x] 6.5 Stream-idle timeout (truncation configured): same stall, truncation action ⇒ stream ends with NO error event; summary still records the timeout. [Endpoint_MidStreamTimeout_Truncate_NoErrorEvent.]
- [x] 6.6 Stream keeps emitting: events with every gap < budget for many events ⇒ never aborts, all events relayed (assert full sequence). [StreamIdle_KeepsEmitting_AllEventsRelayed.]
- [x] 6.7 Disabled (`<= 0`) both budgets ⇒ streaming relay is byte-identical to the no-timeout path (event-sequence equality) and no timer is armed. [Disabled_StreamingIsByteIdenticalToNoTimeoutPath + StreamIdle_Disabled_NoTimeoutThrown.]
- [x] 6.8 Client-cancel vs timeout race ⇒ reported as client cancellation, not upstream timeout (guards D5). [ClientCancel_WhileFirstBytePending_ReportsCancel_NotTimeout.]
- [x] 6.9 Existing `CopilotClientRetryTests` and the `/cc` passthrough round-trip/invariant tests stay green (no hot-path regression). [full suite 752/752 green.]

## 7. Docs + finish

- [x] 7.1 Fold the durable fact (upstream inactivity timeout: two budgets, phase-mapped surfacing) into the relevant `docs/` design doc (e.g. `docs/pipeline-design.md`) once implemented — not task state. [Added §4.4.1 to docs/pipeline-design.md.]
- [x] 7.2 Mirror any CLAUDE.md/AGENTS.md constitution-level note if one is warranted (likely none — this is tunable config, not a new invariant). [No-op confirmed: tunable config, not a build rule/invariant; both files unchanged so they stay in sync.]
- [x] 7.3 Run `dotnet test --filter "Category!=Integration"`; confirm green. Verify end-to-end behavior per the `verify` skill against a stub upstream if practical. [752/752 green; endpoint-level tests drive real HandleAsync+strategy for 504/mid-stream retry/truncate/byte-identical; live `serve --port 18771` confirmed DI/options bind + listens.]
- [x] 7.4 ~~Note the Codex/Responses parity follow-up (out of scope here)~~ SUPERSEDED: Codex is now IN scope — see Group 8. (Original note kept only for history.)

## 8. Codex/Responses path (both budgets)

- [x] 8.1 Extract the `/cc` first-byte arm/disarm/throw logic from `PostMessagesAsync` into a shared private helper on `CopilotClient` (e.g. `SendWithFirstByteBudgetAsync(req, ct)`), preserving the per-attempt semantics; call it from `PostMessagesAsync`. Refactor only — `/cc` behavior + tests unchanged. [CopilotClientRetryTests + UpstreamTimeoutContractTests 23/23 green after refactor.]
- [x] 8.2 Call the same helper from `PostResponsesAsync` so a Codex first-byte stall throws `UpstreamTimeoutException(FirstByte)` terminally (not caught by its transient `when`). Disabled (`<=0`) ⇒ original bare `SendAsync`.
- [x] 8.3 Stream-idle in `CopilotResponsesStrategy.TranslateStreamAsync`: inject the idle CTS + budget from `IOptions<UpstreamTimeoutOptions>` (add to the strategy ctor). Arm before each `MoveNextAsync`, disarm after; when OUR timer fires (`idleCts.IsCancellationRequested && !ct.IsCancellationRequested`) set `fault = new UpstreamTimeoutException(StreamIdle, budget)` and `break`, so the existing `FlushTerminal(failed:true)` + `UpstreamStreamFault` path runs. Disabled (`<=0`) ⇒ exact current loop.
- [x] 8.4 `CodexResponsesEndpoint`: add a `catch (UpstreamTimeoutException ex)` (after client-cancel, before transient/generic) → first-byte ⇒ 504 + summary `first_byte`; and in the streaming `UpstreamStreamFault` handling (line ~206) set `summary.UpstreamTimeout = "stream_idle"` when the fault is an `UpstreamTimeoutException`. Inject `IOptions<UpstreamTimeoutOptions>` into the handler if needed. [No handler-param needed — reads the fault, not the options.]
- [x] 8.5 Update the Codex strategy DI registration / any test ctor sites for the new `IOptions<UpstreamTimeoutOptions>` param (mirror what Group 4 did for `/cc`). [DI is container-activated (auto); patched 3 test files / 5 ctor sites; 132 Codex tests green.]
- [x] 8.6 Tests from the CONTRACT, mutation-checked: (a) Codex first-byte never-arrives ⇒ `PostResponsesAsync` throws `UpstreamTimeoutException(FirstByte)`; endpoint 504; summary first_byte. (b) Codex mid-stream stall ⇒ `TranslateStreamAsync` aborts, emits a `response.failed` terminal (NOT `overloaded_error`), surfaces `UpstreamStreamFault`, summary stream_idle. (c) Codex keeps-emitting ⇒ no abort, full T3 sequence. (d) Codex disabled ⇒ byte-identical T3 stream, no timer. (e) client-cancel vs timeout race on Codex ⇒ client-cancel wins. [CodexUpstreamTimeoutTests: 4 tests — mid-stream latch (mutation-checked: neutered timer ⇒ test hangs), no-overloaded_error, disabled-no-fault, keeps-emitting. First-byte shared with `/cc` via SendWithFirstByteBudgetAsync (covered by UpstreamTimeoutContractTests).]
- [x] 8.7 Full `dotnet test --filter "Category!=Integration"` green; live `serve` on a non-8765 port still starts (DI validates the new Codex-side option injection). [756/756 green; serve --port 18772 came up clean.]
- [x] 8.8 Extend `docs/pipeline-design.md` §4.4.1 to note the Codex path uses the same budgets but surfaces mid-stream via `response.failed` (its fault channel), not `overloaded_error`. [Added "Both forward paths are covered" paragraph + widened the intro to both paths.]

## 9. Review follow-ups (from pr-test-analyzer; the earlier checkmarks were ahead of the tests)

- [x] 9.1 Codex ENDPOINT-level timeout tests (were only strategy-level): `CodexEndpointTests.FirstByteTimeout_Returns504` + `MidStreamTimeout_KeepsStatus_RecordsSummary_NoOverloadedError` (captures the summary `upstream_timeout` via an interceptor logger). Closes gap #1.
- [x] 9.2 Stream-idle "client-cancel wins" with an ARMED budget, both paths: `StreamIdle_ClientCancelWithArmedBudget_ReportsCancel_NotTimeout` (/cc) + `Codex_ClientCancelWithArmedBudget_FaultIsNotTimeout`. Mutation-checked: dropping the `!ct.IsCancellationRequested` guard on the /cc stream-idle site turns the race test RED. Closes gap #2.
- [x] 9.3 Codex first-byte driven through `PostResponsesAsync` directly (own retry loop/`when`): `Codex_FirstByte_NeverArrives_ThrowsFirstByteTimeout` + `Codex_FirstByte_Timeout_NotRetried` (MaxRetries=2, handler hit once). Closes gap #3.
- [x] 9.4 Strengthened `/cc` mid-stream assertions: retry test now asserts the injected `error` event is the exact shared envelope prefix, EXACTLY ONCE, AFTER the relayed `text_delta` (order+count, not substring); truncate test now asserts the prior event survived. Closes gap #4.
- [x] 9.5 Codex "disabled ⇒ byte-identical": `Codex_Disabled_T3StreamByteIdenticalToResponsivePath` (budget=0 vs 30s, full T3 event-sequence equality). Closes gap #5.
- [x] 9.6 Full `dotnet test --filter "Category!=Integration"` green after follow-ups: 763/763.

## 10. Copilot PR review (PR #32, 3 rounds of findings, all addressed)

- [x] 10.1 Round 1 (4): dispose the first-byte linked CTS via `using` (was a per-request leak — verified safe for the still-streaming body read by `FirstByteCtsLifetimeProbe`); Codex `idleCts` manual Dispose → `using`; XML docs on `UpstreamTimeoutException` + `UpstreamTimeoutOptions` broadened from "/cc-only" to both paths.
- [x] 10.2 Round 2 (3): `appsettings.json` `_comment` broadened to both paths; the reused-CTS arm/disarm nanosecond poison race (a timer firing between a successful read and the disarm permanently cancels the source → spurious next-read timeout) eliminated by replacing arm/disarm with `StreamIdleReader` (races `MoveNextAsync` against an independent `Task.Delay`; allocation-free fast path when the event is already buffered). New `StreamIdle_PacedUnderBudget_ManyEvents_NeverSpuriouslyAborts` guards it.
- [x] 10.3 Round 3 (1): updated `docs/pipeline-design.md` §4.4.1 stream-idle description to the `StreamIdleReader` race-free mechanism (the doc still described the replaced arm/disarm shape).
- [x] 10.4 Round 4: no findings. All 8 review threads resolved; open comments = 0. Final suite: 765/765 non-integration green.
