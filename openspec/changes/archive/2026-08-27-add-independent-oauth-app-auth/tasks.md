## 1. External Contract and Test Baseline

- [x] 1.1 Verify the `copilot-bridge` OAuth App client ID, Device Flow setting, and token-expiration setting without changing or exposing a client secret.
- [x] 1.2 Add contract-first unit tests for stock/custom configuration selection, shared serve/auth-command binding, official v3 default login, custom ID/scope, version-4 persistence/provider validation, direct dispatch with zero token-exchange calls, and v1–v3 compatibility.
- [x] 1.3 Add contract-first tests for proactive version-4 rotation, rotated-pair persistence, bounded direct 401/403 recovery, terminal non-refreshable rejection, and secret-free diagnostics.
- [x] 1.4 Mutation-check the new tests by temporarily breaking provider/version/direct/recovery product decisions and confirming the targeted tests fail, then restore the product code.

## 2. Configurable OAuth Credential Implementation

- [x] 2.1 Add a prominent top-level Authentication config section and source-generated options/validation with `UseCustomAppId=false` and the requested default `CustomAppId`.
- [x] 2.2 Make both serve and standalone auth commands consume one provider-selection contract: false selects official Copilot Plugin v3; true selects configured custom Device Flow with `read:user` and no client secret.
- [x] 2.3 Add encrypted credential version 4 with a required recorded custom provider while preserving frozen version 1–3 parsing and behavior.
- [x] 2.4 Make CredentialService persist custom login as version 4 and refresh it with its recorded client ID under the existing atomic, cross-process-safe rotation transaction.
- [x] 2.5 Classify version 4 as direct CAPI authentication in credential leases and status output without inferring semantics from token prefixes or current config.

## 3. Direct Lease and Recovery

- [x] 3.1 Publish a version-4 access token directly with `https://api.githubcopilot.com`, scope its CAPI integration identity to `copilot-developer-cli`, preserve `vscode-chat` for older versions, and prove `/copilot_internal/v2/token` is never called.
- [x] 3.2 Schedule known-expiry direct leases early enough for CredentialService's refresh safety window while leaving unknown-expiry version-2 compatibility unchanged.
- [x] 3.3 Carry bounded refresh capability in the immutable Copilot lease and implement one forced version-4 rotation/replay for CAPI 401 or first 403.
- [x] 3.4 Preserve the existing terminal version-2/non-refreshable direct 401 behavior, generation-race safety, request bytes/headers, timeout scope, and total replay bound.

## 4. Verification Harness and Documentation

- [x] 4.1 Extend the subprocess behavior harness with an enabled custom-provider scenario, validated version-4 credential staging, and a real Codex multi-step tool-chain case.
- [x] 4.2 Update README and the authentication sections of pipeline design, Copilot API research, token storage, design decisions, CLI wording, and compatibility facts for project OAuth v4.
- [x] 4.3 Run focused authentication tests, the complete non-integration solution suite, and `AgentRepositoryCompatibilityTests`; fix product defects without weakening contract assertions.
- [x] 4.4 Authorize a fresh scratch version-4 credential, run the matching `Kind=ClientBehavior` case, and render PASS/FAIL from its exact manifest, trace, stdout, and Codex `logs_2.sqlite` window.

## 5. Native AOT Publication

- [x] 5.1 Publish the win-x64 Native AOT bridge and updater together using the verified Windows toolchain flow.
- [x] 5.2 Record and review both artifact sizes, scan the release outputs for accidental client-secret material, and update `docs/size-history.md` with measured values and verification evidence.
- [x] 5.3 Confirm the publish directory contains `copilot-bridge.exe`, `copilot-updater.exe`, and stock configuration with custom auth disabled, then report the exact artifact paths and any remaining verification limitations.

## 6. PR Review Follow-ups

- [x] 6.1 Reject the official Copilot Plugin client ID when custom direct authentication is enabled, with a contract-first mutation check.
- [x] 6.2 Surface GitHub's bounded `device_flow_disabled` code for a rejected device-code request, with a contract-first mutation check.
- [x] 6.3 Add and render the real Codex version-4 forced-first-403 rotation/replay scenario from the exact manifest and client dispatch log.
- [x] 6.4 Preserve version-owned dispatch when rejection recovery observes a cross-process replacement across the direct/exchanged provider boundary, with contract-first mutation checks in both directions.
- [x] 6.5 Isolate B14 behind a separately authorized, marker-confirmed, single-use v4 credential source so forced OAuth rotation cannot invalidate B13 or an installed credential.
