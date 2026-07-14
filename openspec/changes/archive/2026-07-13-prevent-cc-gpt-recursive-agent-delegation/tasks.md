## 1. Contract and configuration

- [x] 1.1 Update `docs/pipeline-design.md` before framework code with the sub-agent signal, translation-only filter boundary, default, and unaffected paths.
- [x] 1.2 Create and strictly validate the OpenSpec proposal, design, and capability requirements.
- [x] 1.3 Add and bind the default-on `Pipeline:CcToResponses:PreventRecursiveAgentDelegation` option with an operator-facing `appsettings.json` explanation.

## 2. Translation implementation

- [x] 2.1 Snapshot Claude Code sub-agent identity from the inbound agent-id header before outbound header sanitization.
- [x] 2.2 Thread the configured per-request filter decision into T2 without mutating the IR.
- [x] 2.3 Filter the exact `Agent` tool in the typed Claude Code tool writer and preserve valid forced-tool-choice behavior.
- [x] 2.4 Log a warning only when `Agent` is actually removed, with the explicit configuration recovery path.

## 3. Contract-first tests

- [x] 3.1 Add request-builder contracts for enabled first-generation/deeper sub-agents, enabled roots, disabled sub-agents, surviving siblings, and forced tool choice.
- [x] 3.2 Add configuration binding/default coverage and prove native Codex bag tools remain unchanged.
- [x] 3.3 Mutation-check the guard by disabling its product-code predicate and confirm the new contract test fails.

## 4. Automated verification

- [x] 4.1 Run focused guard/config tests, then the full unit-test project and solution-wide non-integration suite.
- [x] 4.2 Run the relevant captured-byte/API-contract regression set for CC-to-GPT and native Codex tool translation.
- [x] 4.3 Build the bridge and inspect the final diff for Native AOT, source-generated JSON, path isolation, and unrelated-file safety.

## 5. Real Claude Code acceptance

- [x] 5.1 Add a `Kind=ClientBehavior` CC-to-GPT case whose root delegates and whose child is explicitly asked to delegate again while completing a bounded tool task.
- [x] 5.2 Drive real headless `claude.exe` through a bridge subprocess on a non-8765 port and capture a new manifest/transcript/trace.
- [x] 5.3 Apply `real-client-verify`: prove the root `Agent` executes, the child upstream request lacks `Agent`, remaining child tools execute, the final turn completes, and no bridge-internal marker leaks.
