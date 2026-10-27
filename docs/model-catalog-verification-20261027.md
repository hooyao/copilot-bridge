# October model catalog verification

The saved client evidence was inspected again for PR #97 after Copilot requested
confirmation of the reported live checks. This is an execution-evidence verdict,
not an inference from bridge HTTP status or the xUnit actuator's exit code.

The fresh runs below executed the product source at
`1c73dcf309d630aa3267c20a14fcea149bc27cbe` on 2026-10-27 UTC. Later verification
and review-workflow changes do not alter the product runtime exercised here.

- Client: real Codex app-server `0.164.0-alpha.11.1`, command-backed provider auth.
- Client executable SHA-256:
  `1103e9e09c71fa2cd0afdf6d8f3b9a2d63174fe8efd7cb5bd09f1cb14ef58a098`.
- Bridge: JIT subprocess on an isolated non-8765 port for each model, built from
  that source. This does not claim verification of a published AOT release binary.
- Built bridge assembly SHA-256:
  `6afc127b9f406e459ac03dd6f14548500557b45f49124a81ed0d6de354ca425c`.

Each client computed 43 × 47, wrote the result, appended a model-specific marker
with another call, and read the file back. The actual tool-result content and the
completed client answer contained `2021` and the expected marker. Every sampling
request retained the exact selected model and low upstream effort. MAI 2's inbound
minimal effort with web search was translated to its separately probed compatible
low combination.

| Exact model | Requests | Emitted calls with matching client outputs | Client SQLite rows | Router fatals / ERROR rows / retries | Client turn |
| --- | ---: | ---: | ---: | --- | --- |
| `gpt-5.4-nano` | 5 | 4 / 4 | 318 | 0 / 0 / 0 | completed |
| `gpt-6.1-terra` | 4 | 3 / 3 | 186 | 0 / 0 / 0 | completed |
| `mai-code-2-flash` | 4 | 3 / 3 | 281 | 0 / 0 / 0 | completed |

Calls were read from actual upstream response SSE and matched by call id to
later client-supplied `function_call_output` / `custom_tool_call_output` input
items. No client execution-abort signal was present. The log reader inspected
each manifest's exact isolated `dispatchLogPath` and bounded time window; none
was an empty-window check.

Terra also appeared exactly once and visibly in the real client's `model/list`,
offering precisely `low, medium, high, xhigh, max`. Ultra was absent. Its reviewed
official source record retains Ultra unchanged; the bridge projection applies
the independently probed backend restriction. Nano and MAI 2 remain explicit-id
choices without synthesized Codex picker resources.

The private evidence is retained locally under `tests/behavior-runs/`; full
prompts, credentials, and raw traces are deliberately not committed. These exact
manifest names and windows identify the inspected recheck, rather than older runs:

| Model | Manifest | SQLite Unix-second window | Manifest SHA-256 |
| --- | --- | --- | --- |
| Nano | `codex-october2026-gpt-5.4-nano-20261027-042206-764.json` | 1793074889–1793074926 | `773859ac7fb512bb0a0d88e694a8b2910f9f23181d8482614b08739498f4f2db` |
| Terra | `codex-october2026-gpt-6.1-terra-20261027-042238-205.json` | 1793074931–1793074958 | `52e4c34b4250068f11ea8b6dc18b0d7b2b928c4eafe7939f2fa42b0d6e0c58f2` |
| MAI 2 | `codex-october2026-mai-code-2-flash-20261027-042311-672.json` | 1793074963–1793074991 | `760ab802f6cb854f3ef84e3babba1957e5d176342fb53bd6df50174bc5cc43cc` |

The source also passed 1,954 offline unit tests and the four repository/skill
mirror checks. Live exact-model B2/B3 observations and streaming captured-request
rewrite checks are exercised by `October2026_ReviewedContracts_MatchSnapshotAndCatalog`
and `October2026_RealCodexBytes_RewriteBoundaries`. The initial five contract
checks passed, and the same checks were repeated against these fresh captures.
Eight mutation checks previously detected altered accepted efforts, fallback
policy, multimodal support, minimal/search behavior, picker merge, and Ultra
filtering; restored product values passed the complete offline suite again.

The review-status helper separately passed 32 offline contracts. Its bounded
verification-report reminder is an evidence-inspection signal, not a code finding
or an automatic evidence waiver. Inline findings, hidden counts, unknown failure
wording, extra instructions, and missing clean headlines remain blocking. A
deliberately overbroad native-client-prefix mutant failed the unknown-failure
case, and the repository's four mirror/compatibility checks passed.

Administrator-disabled Claude models were not probed. The client runs do not
extend the contract for Codex-to-Grok beyond faithful passthrough or claim a
Grok coding-agent integration.
