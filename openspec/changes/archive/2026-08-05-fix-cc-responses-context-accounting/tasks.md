## 1. Contract and Evidence

- [x] 1.1 Update `docs/pipeline-design.md`, `docs/routing.md`, and `docs/copilot-api-research.md` with the shared count/message route-planning contract, transformed-body count behavior, calibration semantics, and narrow Claude error rewrite.
- [x] 1.2 Add sanitized captured-byte API-contract fixtures for the production-shaped Claude request and record its source count, transformed-body count, target admission boundary, and expected conservative lower bound without committing sensitive prompt content.
- [x] 1.3 Extend the live Copilot probe corpus with minimal auxiliary, long-history, tool-heavy, and near-boundary Responses request shapes. For every datum, hash/identify the exact canonical T2 bytes used by both the Anthropic-named count endpoint and the equivalent test-only Responses usage/admission oracle; reject cross-body comparisons.
- [x] 1.4 Freeze versioned per-model calibration records and the conservative unknown-model fallback from the paired live corpus, documenting evidence ids, observed under-count, safety reserve, and bounded over-count, including a maximum over-count for minimal auxiliary inputs.

## 2. Shared Route Planning

- [x] 2.1 Add contract-first unit tests proving equal route inputs produce the same first-match Location, optional effort mapping, target model/profile, and backend selection for messages and count-tokens; cover model, header/beta match inputs, missing optional inputs, first-match/no-chain, and concurrent request isolation.
- [x] 2.2 Extract a side-effect-free route-planning seam shared by the Claude messages and count-tokens paths while preserving current messages behavior.
- [x] 2.3 Add the AOT/source-generated count parsing contract needed by the Responses branch and reuse shared T1/T2 translation so endpoint bytes equal a direct build from that same count input. Cover messages-only, tools plus Claude's dummy message, SDK beta tokens in the actual `anthropic-beta` header, count-time thinking, recursive Agent filtering, and applicable drops/coercions; prove absent system/output/stream/effort fields remain absent, and token-bearing fields that are neither modeled nor an explicit target T2 drop fail instead of disappearing silently.
- [x] 2.4 Mutation-check route parity by deliberately breaking shared routing and T2 reuse and confirming the contract tests fail before restoring the implementation.

## 3. Route-Aware Count Tokens

- [x] 3.1 Preserve byte-for-byte inbound request and upstream response passthrough for count requests that resolve to Copilot Anthropic.
- [x] 3.2 For a Copilot Responses target, post the exact shared T2 body to `/v1/messages/count_tokens` exactly once and explicitly propagate transformed-body count failures without source-count fallback.
- [x] 3.3 Implement an AOT-safe, checked, monotonic Responses admission estimator with exact-model calibration, conservative fallback, safe numeric saturation, and missing-calibration warnings. Reject malformed, missing, negative, fractional, non-numeric, and out-of-range upstream `input_tokens` values explicitly.
- [x] 3.4 Return the calibrated admission estimate in the Anthropic count response while recording requested model, resolved model, raw count, calibration id/reserve, and returned count in bounded summaries.
- [x] 3.5 Add trace coverage proving the inbound Anthropic body, upstream T2 body, raw Copilot count response, and calibrated downstream response remain distinct, with no added prompt content in ordinary logs.
- [x] 3.6 Add estimator table/property tests for exact-model calibration, unknown-model warning/fallback, monotonicity, saturation/no wraparound, malformed count failures, and minimal auxiliary-input bounded over-count without synthetic whole-turn reserves.

## 4. Claude Context Recovery

- [x] 4.1 Add a buffered-endpoint classifier matrix for the exact confirmed Copilot context 400 with its observed JSON-under-`text/plain` content type and every near miss: non-400 status, wrong/missing code, wrong/missing message, sentence only in an unrelated field, native `/codex`, native Anthropic `/cc`, malformed JSON, body above the bounded classifier-inspection limit, and a small request proving classification has no request-size heuristic or application/json dependency.
- [x] 4.2 Rewrite only a confirmed HTTP 400 `invalid_request_body` context-window rejection on `/cc` resolved to Responses into an Anthropic `invalid_request_error` containing `prompt is too long`, without inventing token counts.
- [x] 4.3 Preserve the original upstream error in tracing and record the rewritten Anthropic envelope only at the inbound-response boundary.
- [x] 4.4 Mutation-check the classifier by disabling each scope guard and the recognized phrase in turn and confirming the relevant tests fail before restoring the implementation.

## 5. Verification

- [x] 5.1 Run the focused unit suite and captured-byte `Kind=ApiContract` tests, including native endpoint byte passthrough/status preservation, complete route/header/profile parity, exact count-input T2 construction and one-count/zero-generation calls, recursive Agent-filter parity, estimator edge cases, no-history-mutation, the complete error matrix, and raw/rewrite trace boundaries.
- [x] 5.2 Run the live paired Copilot calibration and near-boundary probes plus sanitized production-shape replay; confirm every guarded estimate is at least the target-equivalent input usage observed for the exact same T2 bytes and record both under-/over-count.
- [x] 5.3 Publish the Windows Native AOT bridge with the repository wrapper, verify source-generated JSON coverage and output size, then launch a Release/AOT artifact with `COPILOT_BRIDGE_TEST_UPSTREAM_BASE_URL` set and prove no request reaches the test listener.
- [x] 5.4 Add a `CcToGptContextRecovery` deterministic upstream/scenario using a real Debug bridge subprocess. Accept and record any small real count request without making its presence a prerequisite for recovery, inject the exact context 400 on the first qualifying tool-bearing `/responses` request, identify and serve the compact-summary turn, then issue Bash/Read and a final canary; keep auxiliary title traffic outside the main phase counter.
- [x] 5.5 Extend `ClaudeProcess` only for this case to use an isolated `CLAUDE_CONFIG_DIR` and retain session persistence (or first prove stream-json emits equivalent compact evidence). Run real headless `claude.exe` and require its own transcript to contain `compact_boundary(trigger=auto)`, a retry and `tool_use`→`tool_result` after the boundary, the final canary, and no internal T3 markers.
- [x] 5.6 Review traces together with client evidence: raw Copilot 400 at `upstream-resp`, rewritten prompt-too-long at `inbound-resp`, no production count call to `/responses`, post-compact target/tool execution, and no behavior change outside Claude-to-Responses. Treat exit zero, request count, bridge 200, trace success, or canary without a client compact boundary as insufficient.
