## 1. Contract Tests

- [x] 1.1 Replace the native-Codex no-keepalive assertion with contract-first tests that require silence-triggered complete ping events and fail against the current product code.
- [x] 1.2 Add T4 native-fidelity tests proving an injected ping bypasses semantic grouping, preserves surrounding upstream events, and retains bridge-origin identity.
- [x] 1.3 Add endpoint/audit and timeout tests covering injected trace marking, disabled behavior, no activity before the first upstream event, and an unchanged upstream-idle deadline.

## 2. Core Implementation

- [x] 2.1 Enable the existing `StreamIdleReader` keepalive deadline for native `/codex` Responses streams without adding a second timer or pending read.
- [x] 2.2 Render the identity-marked complete ping at the Codex T4 edge before native semantic-fidelity accounting.
- [x] 2.3 Mark Codex downstream keepalives as injected and update strategy/option comments for both client protocols.

## 3. Documentation

- [x] 3.1 Update `docs/pipeline-design.md`, `docs/timeout-chain.md`, and `appsettings.json` with the Codex parsed-event watchdog, complete-data-event requirement, and shared deadline semantics.

## 4. Real Client Harness

- [x] 4.1 Add a deterministic silent Responses upstream that resumes with a real Codex tool call and then completes the tool-result turn.
- [x] 4.2 Add a `Kind=ClientBehavior` real Codex case with a shortened isolated provider idle timeout, a shorter bridge keepalive interval, and a longer bridge upstream-idle budget.

## 5. Startup Timeout Report

- [x] 5.1 Add a best-effort Codex timeout reader for the active global provider, including the 300-second source-confirmed default.
- [x] 5.2 Extend the startup report with separate Claude Code and Codex idle rows and keepalive-aware termination authority.
- [x] 5.3 Add contract tests for explicit/default/unknown Codex values, active/inactive keepalive calculations, and non-fatal startup behavior.

## 6. Verification

- [x] 6.1 Run focused keepalive, Codex endpoint, native-fidelity, and timeout unit tests, including the mutation-first red/green check.
- [x] 6.2 Build the bridge and run the targeted real Codex behavior case through a bridge subprocess on a non-default port.
- [x] 6.3 Read the new manifest, bridge trace, tool-call/output pair, and Codex `logs_2.sqlite`; require completion with no idle-timeout or router fatal.
- [x] 6.4 Run the full unit-test project and relevant repository compatibility checks.
