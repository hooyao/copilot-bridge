## 1. Contract and architecture

- [x] 1.1 Update `docs/pipeline-design.md` first with the Codex client-edge reasoning-accounting header boundary, raw-audit separation, and current pre-turn client limitation.
- [x] 1.2 Add the live Copilot A/B usage finding and `X-Reasoning-Included` consumer semantics to `docs/copilot-api-research.md` without claiming Copilot emitted the header.
- [x] 1.3 Add contract-first endpoint tests for streaming/buffered 2xx responses, actual ASP.NET headers, non-2xx and non-Responses isolation, and opposite upstream/inbound audit header facts.
- [x] 1.4 Run the new endpoint contract before implementation and confirm it fails specifically because the downstream reasoning-accounting header is absent.

## 2. Bridge implementation

- [x] 2.1 Add one canonical Codex reasoning-accounting header constant/helper and inject it at the `/codex/responses` client edge only for resolved Copilot Responses 2xx results before body streaming.
- [x] 2.2 Preserve raw Copilot response headers and request headers unchanged while recording the synthesized signal only in the actual downstream response and `inbound-resp` audit.
- [x] 2.3 Run the focused endpoint/audit tests and mutation-check the seam by temporarily removing the injection and observing the contract fail.

## 3. Backend contract guard

- [x] 3.1 Add a `Category=Integration`, `Kind=ApiContract` paired live probe that obtains complete reasoning items and compares Copilot input usage with and without their replay.
- [x] 3.2 Run the live probe on the current Responses model and record the positive input-token delta as backend evidence.

## 4. Real Codex behavior

- [x] 4.1 Add a thin `Kind=ClientBehavior` two-turn real-Codex actuator with deterministic low usage, historical encrypted reasoning, and a second tool turn that would falsely compact after sampling without the header.
- [x] 4.2 Run the targeted actuator through a non-8765 bridge subprocess and judge its exact manifest using the bridge trace plus Codex's own `logs_2.sqlite` evidence.
- [x] 4.3 Run an ordinary live-Copilot complex Codex behavior case and confirm tool execution plus zero router/dispatch fatals with the new header present.

## 5. Validation and handoff

- [x] 5.1 Run focused and full unit tests, solution tests excluding Integration, and `AgentRepositoryCompatibilityTests`.
- [x] 5.2 Publish win-x64 Native AOT via the repository-supported Windows recipe, confirm zero warnings, and record the bridge/updater binary sizes.
- [x] 5.3 Write `verification.md` with contract, mutation, backend, real-client, log, trace, AOT, and known-client-limitation evidence; leave the change ready for archive rather than silently patching Codex.

## 6. PR Review Follow-ups

- [x] 6.1 Require the second-turn actuator input to replay the complete first-turn reasoning id, encrypted content, and summary before serving the tool call.
- [x] 6.2 Defer client-edge header synthesis until response output is ready and remove both actual/audit headers on every pre-start non-2xx failure path.
- [x] 6.3 Re-run focused contracts, full unit tests, the real-Codex accounting verdict, and win-x64 Native AOT after review fixes.
