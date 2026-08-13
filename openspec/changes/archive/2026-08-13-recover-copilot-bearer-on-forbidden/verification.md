## Verdict

PASS. The first authenticated CAPI 403 now rejects one immutable Copilot lease
generation, publishes or reuses a newer generation, and replays the exact request
once. Persistent 403 remains terminal after that replay. Live model-overlay failures
are suppressed for the exact configured cooldown while the Codex catalog stays safe.

## Production incident evidence

- Source: logs and trace copied from the affected remote machine; the local bridge
  was not treated as the incident source.
- Native Codex inference response: `2026-08-13T12:41:13.8354094Z`, trace sequence
  495, HTTP 403, body `forbidden\n`, Copilot Kubernetes headers, service request id
  `23a0f2ca-9510-4b30-8bd5-1d6fa931ce7f`.
- Codex displayed five failed attempts.
- Live `/models` overlay refreshes then received 403 at approximately 20:41:41,
  20:44:42, and 20:47:43 local time.
- The scheduled Copilot bearer refresh succeeded at 20:48:20 and published
  generation 8.
- Restarting the bridge recovered immediately. That observation isolates the stale
  state to the process-local Copilot bearer rather than the persisted GitHub
  credential or the request body.

## Contract and mutation verification

- Focused auth/catalog/options/endpoint/logging tests: 133 passed.
- Full unit suite: 1,654 passed, 0 failed, 0 skipped.
- Captured real-Codex-byte authentication contract: first 401 and first 403 both
  replayed byte-identically; 2 passed.
- Mutation checks all reddened the intended contract before restoration:
  - replay body/endpoint corruption: 6 failures;
  - transient retry-budget reset: 2 failures;
  - removal of the single-auth-replay bound: 4 failures;
  - removal of the catalog cooldown gate: 2 failures.
- After restoration, the combined focused auth/catalog set passed 62/62.
- `AgentRepositoryCompatibilityTests`: 4/4 passed for the `.agents`/`.claude`
  real-client skill mirrors.

## Real Codex verdict

- Manifest:
  `tests/behavior-runs/manifests/codex-one-shot-auth-403-recovery-20260813-171117-664.json`
- Client/model/route: real Codex app-server 0.147.0-alpha.6.6,
  `gpt-5.6-sol`, native `/codex` Responses route.
- Lifecycle log from the manifest's `bridgeLogPath`, all on request sequence 1:
  - one Debug-only injected CAPI 403 for `POST /responses`, generation 1;
  - one `copilot_403` bearer refresh, outcome success, generation 2;
  - one authentication replay, HTTP 200;
  - no terminal policy/entitlement classification.
- Client execution:
  - four real `custom_tool_call` `exec` calls had matching
    `custom_tool_call_output` items with the same call ids;
  - the computation returned 5050, three separate file commands completed, and the
    final answer contained `5050` plus `codex-auth-replay-canary-64017`;
  - stdout contained no abort.
- Codex-owned SQLite window: 680 rows, 0 router/dispatch fatals, 0 ERROR rows,
  0 retry rows.
- Verdict: PASS by the real-client rubric, not by bridge status alone.

## Real Claude Code verdict

- Manifest:
  `tests/behavior-runs/manifests/cc-native-one-shot-auth-403-recovery-20260813-171224-168.json`
- Client/model/route: real `claude.exe`, `claude-opus-5`, native `/cc` Messages
  passthrough.
- Lifecycle log from the manifest's `bridgeLogPath`, all on request sequence 1:
  - one Debug-only injected CAPI 403 for `POST /v1/messages`, generation 1;
  - one `copilot_403` bearer refresh, outcome success, generation 2;
  - one authentication replay, HTTP 200;
  - no terminal policy/entitlement classification.
- Claude transcript: 3 `tool_use` events, 3 matching `tool_result` events, final
  successful result containing `cc-auth-replay-canary-49271`, no error or abort.
- The per-run trace accumulated all three tool-use/result pairs on subsequent
  Messages requests.
- Verdict: PASS from the real Claude transcript plus trace.

## Catalog cooldown real-process verification

`CodexCatalogFailureCooldownProcessTests` booted the real CLI/config/DI process with
a two-second cooldown and a persistent forced live `/models` failure. Thirteen
downstream catalog polls all returned HTTP 200 with a non-empty safe baseline. The
first five polls caused one failed overlay attempt and one warning; eight concurrent
polls after expiry caused exactly one further attempt and one warning. Test passed in
7 seconds.

## Build, spec, and Native AOT

- `dotnet build CopilotBridge.slnx`: succeeded, 0 warnings, 0 errors.
- Release CLI build: succeeded, 0 warnings, 0 errors.
- The local NuGet vulnerability service was unavailable (`NU1900`); an explicit
  cache-only restore with `NuGetAudit=false` was used only for the offline local
  zero-warning verification. Normal CI retains its network-backed audit.
- `openspec validate recover-copilot-bearer-on-forbidden --strict`: valid.
- Fresh Windows x64 Native AOT package:
  - `copilot-bridge.exe`: 14,755,328 bytes;
  - `copilot-updater.exe`: 5,019,136 bytes;
  - stock `appsettings.json` present;
  - native `copilot-bridge.exe --version` smoke passed;
  - Release IL and native executable contain no
    `COPILOT_BRIDGE_TEST_FORCE_CAPI_403_ONCE` activation path or marker.
- Size delta recorded in `docs/size-history.md`: +55,808 bytes / +54.5 KiB /
  +0.38% against the pre-change local artifact; no dependency was added.
