# Verification — Codex reasoning context accounting

Date: 2026-08-18

## Contract established

- Copilot `/responses` HTTP responses omit `X-Reasoning-Included` on the raw upstream wire.
- A paired live `gpt-5.6-sol` probe confirms Copilot input usage nevertheless charges replayed reasoning items.
- Codex 0.147/0.148 treats header presence as the signal that server usage already includes historical reasoning; without it Codex adds a local encrypted-reasoning estimate again.
- The bridge now synthesizes `X-Reasoning-Included: true` only on a 2xx native `/codex/responses` response resolved to `CopilotResponses`, after raw upstream headers were snapshotted and before downstream bytes are written.

## Contract-first and mutation evidence

Before product implementation, the focused five-test endpoint run produced the required shape:

- three positive tests failed because the actual ASP.NET header / `inbound-resp` header was absent;
- the non-2xx and non-Responses isolation tests passed.

After implementation the same focused run passed 5/5. Mutation-checking `ShouldSignalReasoningIncluded` to return false made both the actual-response and audit-boundary positive contracts fail; restoring the implementation returned them to 2/2 green.

The audit contract proves both boundaries in one request:

- raw `upstream-resp`: header absent;
- actual ASP.NET response and `inbound-resp`: `X-Reasoning-Included: true`.

## Live Copilot backend fact

Test:

`ReasoningReplayRequirementsProbe.Gpt56Sol_InputUsageAlreadyChargesReplayedReasoning`

Result: PASS through a disposable non-8765 bridge subprocess using only the staged DPAPI legacy access-token mirror.

```text
reasoning_items=2 with=615 without=59 delta=556
```

The positive 556-token input delta independently guards the backend fact that justifies header synthesis. If this delta disappears, the probe fails and the bridge assertion must be re-evaluated.

## Real Codex targeted behavior verdict

- Client: real Codex app-server `0.148.0-alpha.9`
- Model: `gpt-5.6-sol`
- Case: `Codex_ReasoningIncluded_PreventsFalsePostSamplingCompact_ForVerdict`

Manifest:

`tests/behavior-runs/manifests/codex-reasoning-included-accounting-20260818-144925-568.json`

Evidence:

- bridge subprocess used a random non-8765 port;
- deterministic upstream observed two user-turn sampling requests, one byte-complete replay of the first turn's reasoning id/encrypted content/summary, one actual custom-tool output, and zero `request_kind=compaction` requests;
- all three raw `upstream-resp` artifacts omitted the header;
- all three client-facing `inbound-resp` artifacts carried `X-Reasoning-Included: true`;
- trace request 2 received `custom_tool_call(call_reasoning_accounting_exec)` and request 3 contained the matching `custom_tool_call_output`;
- Codex stdout contained `codex-reasoning-accounting-canary-91827` and no execution-abort signature;
- Codex's own log recorded the response header, `auto_compact_scope_limit=900`, `token_limit_reached=false`, and actual `exec` dispatch;
- SQLite digest scanned 201 rows: router/dispatch fatals 0, ERROR rows 0, retry rows 0.

Verdict: PASS for the bridge-owned reasoning-accounting leg.

## Real Codex live-Copilot behavior verdict

Case: `Codex_MultiStepToolChain_ProducesDispatchLogForVerdict`

Manifest:

`tests/behavior-runs/manifests/codex-multistep-toolchain-20260818-112150-269.json`

Evidence:

- four live Copilot `/responses` requests completed with HTTP 200;
- each raw upstream response omitted the header and each Codex-facing response carried `X-Reasoning-Included: true`;
- the trace contains three successive custom-tool call/output round trips;
- stdout contains `codex-behavior-canary-51742` and no abort signature;
- SQLite digest scanned 265 rows: router/dispatch fatals 0, ERROR rows 0, retry rows 0.

Verdict: PASS for ordinary live Copilot tool execution with the synthesized header.

## Offline validation

- Focused header/audit contract: 5/5 PASS.
- Full unit suite before review: 1659/1659 PASS; after review follow-ups: 1660/1660 PASS.
- Solution tests with `Category!=Integration`: 1659/1659 PASS before review; Playground correctly had no matching non-Integration tests.
- `AgentRepositoryCompatibilityTests`: 4/4 PASS after updating both real-client skill mirrors.
- `git diff --check`: PASS.

## Native AOT

Windows `win-x64` publication used `build-aot.bat` for the bridge and the same verified VS developer environment plus explicit VS Installer PATH for the updater.

- warnings: 0
- `publish/copilot-bridge.exe`: 14,755,840 bytes after review follow-ups
- `publish/copilot-updater.exe`: 5,019,136 bytes

## PR review follow-ups

- The second-turn deterministic upstream now refuses to serve the tool call unless Codex replayed the exact first-turn reasoning id, the complete 3,000-character encrypted blob, and the exact summary entry. The rerun recorded `reasoning-replay=1`, tool output 1, and compactions 0.
- Reasoning-accounting synthesis is deferred until a buffered translation succeeds or the first SSE event is ready. Every pre-start error path removes the signal from the ASP.NET and audit header sets before emitting a non-2xx response. A red-before-green synthetic pre-start failure contract guards the 502 boundary.

## Known external client limitation

Codex 0.147.0-alpha.6.6 and 0.148.0-alpha.9 clear their remembered `server_reasoning_included` flag before the next pre-turn compaction check (`openai/codex#32483`). The bridge cannot reorder that client code. This change fixes the missing bridge response signal and its post-response accounting path; affected Codex versions can still compact early at a later user-turn boundary until the upstream reset ordering is fixed. The bridge deliberately does not hide that limitation by falsifying usage or inflating live-probed context limits.
