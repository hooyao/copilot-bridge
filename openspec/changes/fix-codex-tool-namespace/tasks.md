# Tasks — round-trip a tool's namespace through T1/T2/T3/T4 (gpt-5.6 namespaced tools)

## 1. Ground the contract
- [x] 1.1 Confirm the failure from the real trace: a follow-up turn echoes a prior
  `collaboration.list_agents` call as a `function_call` WITHOUT `namespace` →
  Copilot `buffered status=400` "Missing namespace for function_call 'list_agents'"
  (`request-traces/20260711-111649-0010`).
- [x] 1.2 Confirm the source shape from the real 0009 upstream stream: Copilot puts
  `"namespace":"collaboration"` on the `output_item.added`/`.done` function_call item.
- [x] 1.3 Ground it in the authoritative spec, not the error: openai/codex
  `ev_function_call_with_namespace` fixture + vercel/ai SDK
  (`providerMetadata.openai.namespace`).
- [x] 1.4 Live-replay: real 400-ing bytes verbatim → 400; same bytes with namespace
  injected on the echo → 200 (necessary AND sufficient) — `NamespaceRealReplayProbe`.

## 2. Fix (both directions, four tiers)
- [x] 2.1 Model: add optional `Namespace` to `ResponsesFunctionCallItem`.
- [x] 2.2 T3: `OnOutputItemAdded` reads the item `namespace` → stamps
  `bridge_tool_namespace` on the tool_use content_block (IR-internal marker).
- [x] 2.3 T4: `OnBlockStart` lifts `bridge_tool_namespace`; `FunctionCallItem` emits
  `"namespace"` on the Codex-facing function_call item; marker never leaks.
- [x] 2.4 T1: `function_call` case stashes a non-default namespace into the part bag
  (`BuildFunctionCallPartBag`, unified with the grammar-text marker).
- [x] 2.5 T2: `ToolUseBlockParam` emit writes `"namespace"` from the bag
  (`TryGetToolNamespace`); default-namespace tools emit no field (byte-identical).

## 3. Tests (from contract, mutation-checked)
- [x] 3.1 `CodexNamespaceRoundTripTests` (6): T3→T4 delivers namespace to Codex;
  default-namespace emits none; marker non-leak; T1→T2 re-emits on echo; default echo
  emits none; full bidirectional survival.
- [x] 3.2 Mutation-check: disabling the T4 emit reddens the response-side tests;
  disabling the T2 emit reddens the request-side tests; the absence-asserting tests
  correctly stay green.
- [x] 3.3 `NamespacedToolEchoProbe` + `NamespaceRealReplayProbe` (live probes).
- [x] 3.4 `CodexNamespaceEchoHeadlessTests`: real 667-message inbound (namespace
  present) → real bridge → real Copilot → 200 + audit shows namespace forwarded.
- [x] 3.5 Full unit suite green (843).

## 4. Real-client verification (CLAUDE.md 🔴 directive)
- [x] 4.1 Real bytes end-to-end: the actual captured turn through the FIXED bridge to
  real Copilot → 200, no "Missing namespace", audit confirms namespace forwarded
  (`CodexNamespaceEchoHeadlessTests`).
- [ ] 4.2 **UNVERIFIED (needs the user):** a real DESKTOP codex.exe multi-agent session
  (spawn_agent/list_agents) against the redeployed bridge, reproducing the turn-2 echo.
  `codex exec` CLI does not emit collaboration/namespaced tools — only the desktop app
  does — so this leg cannot be driven headlessly. The AOT exe is rebuilt and deployed to
  8765 for the user to test.

## 5. Ship
- [x] 5.1 Review changed code with the 5-agent code-review skill (namespace round-trip
  reviewed as part of the 0.4.13-beta batch; findings — the CC→gpt marker leak that also
  affects `bridge_tool_namespace`, and the `ToolCallItem` namespace-on-custom handling —
  addressed in `fix-codex-exec-custom-tool-call`).
- [ ] 5.2 `/ship-pr` → 0.4.13-beta.
