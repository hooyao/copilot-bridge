# Verification

## Official and live contract evidence

- OpenAI Docs: `gpt-6-astra` uses Responses for tool calls, supports `low/medium/high/xhigh/max`, and directs `none`/`minimal` migrations to `low`.
- Copilot discovery (2026-09-07): `/responses` and `ws:/responses`; 1,000,000 total, 872,000 prompt, 128,000 output tokens.
- `ResponsesProbe.Gpt6Astra_*`: liveness 200; accepted efforts `low/medium/high/xhigh/max`; rejected `none/minimal`; function/custom/web-search accepted; store/service-tier/image-generation rejected; sync and async custom tools retained the established event grammar; structured image function output returned 200/200 and answered `Red`.
- Real GPT-5.6 Codex request replay: only the model changed to Astra, and the complete request returned 200.
- Real Astra capture fidelity: the exact 79 KB final request returned 200 at `low`; changing only effort to `none` or `minimal` returned 400 with Astra's supported-value error.
- `B_ResponsesContract_SweepAssertAndDetectDrift`: regenerated and reviewed the 11-model snapshot, then passed again without regeneration in 4:46, including the two-way catalog comparison.

## Contract and build verification

- Mutation check: temporary defects in registry membership, Astra effort/default, the stock route target, and resolved-target catalog limits produced 12 focused failures; restoring each product fact returned all 32 focused assertions to green.
- `dotnet test tests/CopilotBridge.UnitTests`: 1,766 passed.
- `dotnet test CopilotBridge.slnx --filter "Category!=Integration"`: 1,766 passed; Playground correctly had no matching non-integration cases.
- `AgentRepositoryCompatibilityTests`: 4 passed.
- `openspec validate add-gpt-6-astra-routing --strict`: valid.
- `dotnet build CopilotBridge.slnx`: 0 warnings, 0 errors.

## Real Codex acceptance

### Direct Astra load-task smoke

`CODEX_SMOKE_MODEL=gpt-6-astra` drove real `codex.exe` through two `/responses` turns. Both returned 200, the request carried `additional_tools`, and the trace contained a matching `custom_tool_call` / `custom_tool_call_output` pair.

### Stock GPT-5.6-to-Astra route

- Client: real Codex app-server 0.153.3.
- Client identity/effort: `gpt-5.6-sol` / `none`.
- Resolved upstream: eight `gpt-6-astra` requests, every one at `low` effort and HTTP 200.
- Execution: seven `custom_tool_call:exec` calls each appeared with its matching output on a later request; the actual read returned `7260` and `codex-astra-route-canary-69073`.
- Client-facing response model: `gpt-5.6-sol` on every turn.
- Client stdout: completed final answer with both exact lines and no abort.
- Manifest-selected `logs_2.sqlite`: 349 rows, zero router/dispatch fatals, zero ERROR rows, zero retry rows.

Verdict: **PASS** under both real-client gates.

## PR review round 1

- Added a contract test for an earlier effort-dependent route shadowing a later Astra fallback. It failed on the reviewed implementation because the catalog incorrectly advertised Astra limits, then passed after fixed-model tri-state analysis made the ambiguous alias hidden.
- Changed the Astra >272k probe to select its own routed model capture instead of whichever GPT-5.6 request happened to be newest. The focused live run returned 200 with 310,307 reported input tokens.
- Re-ran the route-specific real Codex case after the catalog fix: seven Astra/low requests accumulated six matching custom-tool outputs; stdout completed with `7260` and the canary and no abort; the manifest-selected SQLite window contained 308 rows with zero router/dispatch fatals, ERROR rows, or retries.

## Native AOT

- `copilot-bridge.exe`: 14,852,608 bytes; `--version` executed successfully.
- `copilot-updater.exe`: 5,019,136 bytes.
- Both win-x64 publishes completed with zero trimming/AOT warnings; `publish/` contains both executables and `appsettings.json`.
