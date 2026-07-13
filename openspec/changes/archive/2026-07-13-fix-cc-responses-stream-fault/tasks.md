## 1. Contract and Architecture

- [x] 1.1 Update `docs/pipeline-design.md` before framework code to state that mid-stream failure framing is selected by the downstream client edge, including cross-model `/cc` → Responses routing.
- [x] 1.2 State the test contracts in code comments before implementation: Claude Code receives a retryable Anthropic error, Codex receives exactly one `response.failed`, private failure markers never cross `/cc`, and all fault summaries remain truthful.

## 2. Contract-First Regression Tests

- [x] 2.1 Add an endpoint-level `/cc` cross-route test whose Responses IR stream emits partial text and then throws `UpstreamTimeoutException(StreamIdle)`; assert partial text is followed by exactly one configured `event:error`, with no private `stop_reason: "error"` and no synthetic normal `message_stop`.
- [x] 2.2 Add the corresponding `/cc` `StreamIdleAction=Truncate` test; assert no error event, private marker, or synthetic normal terminal is emitted, while prior content survives.
- [x] 2.3 Add a Codex endpoint/T4 test for the same throwing IR stream; assert exactly one `response.failed`, no Anthropic error envelope, already-started status `200`, and no duplicate terminal.
- [x] 2.4 Add successful-stream parity tests proving that a normal Responses terminal produces the same downstream event bytes with the new fault path dormant.
- [x] 2.5 Add observability assertions for `/cc` → Responses timeout: summary `upstream_timeout=stream_idle`, non-empty error, partial raw `upstream-resp` with error, and client-native `inbound-resp` bytes.
- [x] 2.6 Add coverage for an explicit upstream `response.failed` on both client edges, grounded in a captured failure code/shape, so it cannot leak the private marker to Claude Code.
- [x] 2.7 Mutation-check the new tests by temporarily restoring the current swallow-plus-`FlushTerminal(failed: true)` behavior and confirm the Claude Code contract tests fail.

## 3. Stream Fault Propagation

- [x] 3.1 Refactor `CopilotResponsesStrategy` so mid-stream read exceptions dispose upstream resources and propagate through the IR event stream instead of being converted to a normal IR terminal.
- [x] 3.2 Remove or narrow the `ErrorStopReason` path so it is impossible for a bridge-private failure marker to leave through `ClaudeCodeOutboundAdapter`; fail closed at the Claude edge if a private marker nevertheless appears.
- [x] 3.3 Convert explicit upstream `response.failed` into a bounded typed stream failure carrying only safe diagnostic metadata, and route it through the same downstream-boundary mechanism.
- [x] 3.4 Preserve raw upstream capture through the final received event and ensure exception propagation does not leave a pending read, stream, response, or timeout task undisposed.

## 4. Client-Protocol Error Surfaces

- [x] 4.1 Reuse the existing Claude endpoint stream-idle policy for Responses-backend faults: `Retry` writes the configured Anthropic retryable error once; `Truncate` writes nothing further; neither emits a normal terminal.
- [x] 4.2 Keep T4's catch-and-flush behavior for Codex, ensuring it writes one `response.failed` before rethrowing the fault for endpoint accounting.
- [x] 4.3 Make the Codex endpoint timeout catch response-start-aware so a rethrown mid-stream timeout retains status `200` and records the failure, while a first-byte timeout remains `504`.
- [x] 4.4 Consolidate or remove the `BridgeResponse.UpstreamStreamFault` side channel where propagation supersedes it, without losing observability for any path that intentionally catches a fault.
- [x] 4.5 Add contract coverage and buffered T3/T4 translation for Claude Code's `stream:false` fallback on a Responses route, including text and tool-use bodies; keep response detectors on IR and preserve upstream error envelopes.
- [x] 4.6 Route generic Responses transport faults through the same downstream client boundary policy, and fail closed when a successful buffered Responses body cannot enter IR.

## 5. Observability and Configuration Integrity

- [x] 5.1 Populate `summary.Error` and `summary.UpstreamTimeout` from the actual relay exception on `/cc` → Responses and Codex paths before the summary is logged.
- [x] 5.2 Record the exception on `upstream-resp` and the actual client-protocol failure/truncation on `inbound-resp`, while preserving the exact partial raw upstream bytes.
- [x] 5.3 Verify logs contain one bounded warning with trace id, phase, exception type, and elapsed idle time, but no prompt, tool arguments, or generated response text.
- [x] 5.4 Leave the `StreamIdleTimeoutSeconds=60` default and existing operator knobs unchanged; document that timeout tuning is separate because the incident cannot establish whether upstream would have resumed after cancellation.
- [x] 5.5 Update the client-autoconfiguration capability contract so it requires removing the legacy fallback-disable environment key.

## 6. Automated Verification

- [x] 6.1 Run the focused timeout, stream robustness, endpoint, trace-capture, and summary tests; investigate every contract failure rather than weakening assertions.
- [x] 6.2 Run `dotnet test tests/CopilotBridge.UnitTests` and the solution-wide non-integration suite.
- [x] 6.3 Run the captured-byte/API-contract regression set and confirm successful `/cc`, `/codex`, T3, and T4 wire shapes remain unchanged.
- [x] 6.4 Publish the Windows Native AOT binary with the repository's verified toolchain recipe and confirm no trimming warnings or material binary-size regression.

## 7. Real Claude Code Acceptance

- [x] 7.1 Extend the `CcToGpt` `Kind=ClientBehavior` scenario with a controllable, path-exercising Responses stream that emits partial commentary and then stalls/faults after headers; a normal live run that never reaches the fault does not satisfy this task.
- [x] 7.2 Drive real headless `claude.exe` through the subprocess bridge on a complex multi-step task requiring tools after the injected fault, using a non-8765 port.
- [x] 7.3 Use the `real-client-verify` workflow to inspect the Claude transcript/run manifest and prove: the partial attempt is not committed as a completed turn, another model request occurs, tool execution succeeds after retry/fallback, and the task reaches its success canary.
- [x] 7.4 Inspect the same request-id traces and prove the Claude-facing stream contains exactly one retryable `event:error`, no `bridge_*` marker, no `stop_reason: "error"`, and no synthetic normal `message_stop`; record the manifest and trace ids in this change.

## 8. PR Review Follow-ups

- [x] 8.1 Add generic transport-fault endpoint coverage for both Claude and Codex client-native failure framing.
- [x] 8.2 Fail the deterministic real-client scenario fast when a Release bridge would ignore its Debug-only test-upstream override.
- [x] 8.3 Reconcile the durable AOT size record and PR verification metadata to the same measured artifact.
- [x] 8.4 Treat clean EOF after any nonterminal Responses activity as a bounded fault and verify both downstream protocol surfaces.
- [x] 8.5 Validate the actual selected CLI build configuration before enabling the Debug-only deterministic upstream.
- [x] 8.6 Update durable design links to the date-prefixed archived OpenSpec paths.
