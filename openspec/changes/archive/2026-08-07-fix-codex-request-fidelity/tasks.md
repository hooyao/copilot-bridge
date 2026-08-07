## 1. Architecture Contract and Baseline

- [x] 1.1 Update `docs/pipeline-design.md` first with request-side provider provenance, source-push/destination-pull ownership, destination merge order, and explicit mutation authority.
- [x] 1.2 Update `docs/codex-implementation-design.md` and `docs/codex-protocol-research.md` with the 233-turn audit and paired live facts for reasoning context, item metadata, developer positioning, native output arrays, and invalid message ids.
- [x] 1.3 Add contract-first unit cases for `reasoning.context`, developer-message position, valid message metadata, structured native function output, complete reasoning state, and future siblings on known item types; confirm each test fails against the pre-fix product behavior.

## 2. IR and Source DTO Carriage

- [x] 2.1 Add optional provider extensions to `MessageParam`, register every new AOT/source-generated JSON shape, and prove an empty extension remains inert on the Claude hot path.
- [x] 2.2 Make known Responses request/item/content DTOs retain unmodeled sibling fields without reflection, including unknown top-level and reasoning fields.
- [x] 2.3 Define a bounded OpenAI provider record for ordered source input items and their semantic system/message/content projections, replacing the unknown-only `passthrough_items` special case.
- [x] 2.4 Add low-level round-trip tests proving provider records and extension dictionaries survive copies/stages without leaking onto ordinary Anthropic JSON.

## 3. T1 Source Push

- [x] 3.1 Push complete request-level Responses extras, including `reasoning.context`, into the OpenAI request extension while keeping effort semantic.
- [x] 3.2 Push user/assistant message metadata and future siblings into message extensions and content-part extras into part extensions.
- [x] 3.3 Project developer/system messages into semantic system blocks while recording their raw item, original input position, and block provenance in the IR.
- [x] 3.4 Push function-call, function-output, and reasoning extras at part level; retain native function-output JSON as opaque provider data.
- [x] 3.5 Prove T1 output is independent of the eventual route/destination and contains no target-specific branch.

## 4. T2 Destination Pull and Merge

- [x] 4.1 Merge request/reasoning extras into the generated Responses envelope without allowing them to overwrite current semantic or destination-controlled fields.
- [x] 4.2 Restore ordinary message metadata/future siblings and content-part extras from IR while preserving semantic edits made by shared stages.
- [x] 4.3 Restore developer/system source items at their original `input[]` positions, suppress only their corresponding top-level instruction copies, and patch compatible semantic text edits without restoring stale content.
- [x] 4.4 Re-emit native opaque `function_call_output.output` with its original JSON kind/value and retain function/reasoning item extras.
- [x] 4.5 Keep Claude Code→Responses semantic tool-result/image translation unchanged and prove T2 never checks source/client identity.

## 5. Explicit Destination Mutations and Observability

- [x] 5.1 Preserve valid `msg*` message ids and narrowly omit only live-rejected message ids such as `item_0`, retaining every unrelated item field.
- [x] 5.2 Apply routing/profile coercions after provider restoration so model/effort changes and rejected fields/tools cannot be undone by the carrier.
- [x] 5.3 Add bounded stable request-mutation codes to builder results and request summaries without logging values or allocating a detail collection when diagnostics are off.
- [x] 5.4 Add conflict/corruption tests that fail closed to current semantic output, never emit duplicate JSON keys, and report the exact downgrade.

## 6. Independent Fidelity Net

- [x] 6.1 Replace the request half of `CodexResponseCorpusReplayTests` with inbound-body contract comparison plus an explicit reviewed route/profile/protocol mutation allowlist; never use captured upstream output as the oracle.
- [x] 6.2 Run the 233-turn production corpus and require zero undeclared differences, preserving item order and every retained JSON value.
- [x] 6.3 Mutation-check every new preservation leg by disabling it individually and proving the focused/corpus test goes red before restoring product code.
- [x] 6.4 Add permanent captured-byte ApiContract probes for accepted `all_turns`, valid metadata/developer positioning, accepted native output arrays, and rejected non-`msg` message ids; extend the live rewrite sweep so backend drift makes the coercion red.
- [x] 6.5 Assert recursively that OpenAI IR records and `bridge_*` markers never cross either client wire boundary.

## 7. Verification and Delivery

- [x] 7.1 Run focused Codex request/response invariant tests, the full unit suite, and solution-wide non-integration tests.
- [x] 7.2 Run the relevant ApiContract captured-byte/live probes and confirm every target response is evidence-bearing rather than a bridge-only 200.
- [x] 7.3 Run real `Codex_XhighReasoningAndCustomExec_PreservesNativeResponse_ForVerdict` through a non-8765 bridge and apply the real-client verdict: namespaced function plus custom exec round trips, final canary, no abort, and zero SQLite router/error rows.
- [x] 7.4 Run the Claude Code→gpt ClientBehavior case to prove cross-protocol translation, marker isolation, and tool execution remain intact.
- [x] 7.5 Publish Windows Native AOT with `build-aot.bat`, confirm zero trim/AOT warnings, inspect binary size, and update `docs/size-history.md` if the release binary changes materially.
- [x] 7.6 Verify OpenSpec apply status, review the final diff for unrelated/user-owned files, and leave the focused branch ready for the repository's archive-before-PR ship workflow.

## 8. PR Review Follow-ups

- [x] 8.1 Apply destination message-id validation and mutation accounting to ordered developer/system passthrough items, including the raw fallback path.
- [x] 8.2 Preserve explicit JSON null separately from absence for top-level `reasoning.context`, with focused and live contract coverage.
