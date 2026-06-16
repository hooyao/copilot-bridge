## Why

Copilot is a moving target. Its `/v1/messages` and `/responses` backends change what they accept over time — Copilot has already drifted on the Anthropic side repeatedly (`docs/bug-mid-conversation-system-messages-dropped.md`, `docs/copilot-upstream-toolcall-bug-report.md`, and `ModelProfileCatalog`'s own note that effort acceptance was *manually* re-probed 2026-06-05 after "Copilot widened it" — a drift caught by luck, not by a test). Today **both** wire probes (`ModelProfileProbe` for `/v1/messages`, `ResponsesProbe` for `/responses`) are **print-only** (`_output.WriteLine`, zero assertions), so neither backend has any drift alarm. When Copilot moves, our hand-curated wire-truth (`ModelProfileCatalog`, and the Codex coercions in change 3) silently goes stale and the bridge breaks with no signal.

This change makes the **live Copilot backends the ground truth** and adds **drift detection** that goes red when either backend moves. It is the "B" half of the test philosophy in `docs/ir-definition-design.md` §7.0/§7.B: change 1 proved our translators are internally faithful (invariant); this change proves we match the live upstream *now*, for **both** backends, and alarms when that stops being true.

This is **change 2 of 3**. It depends on change 1's test framework and feeds change 3 (the Responses snapshot recorded here is the wire-truth change 3's Codex profile catalog will be built against).

## What Changes

- **Promote both probes from print → assert (B1).** `ModelProfileProbe` (Copilot `/v1/messages`) and `ResponsesProbe` (Copilot `/responses`) gain real assertions over the live wire facts: Anthropic side — per-model effort/thinking/mid-conv-system/beta acceptance (the facts `ModelProfileCatalog` encodes); Responses side — per-model effort accept/reject, `service_tier`/`store`/`image_generation` rejection, the live `/responses` SSE event set.
- **Commit a contract snapshot per backend.** `docs/copilot-anthropic-contract-snapshot.json` (`/v1/messages`) and `docs/copilot-responses-contract-snapshot.json` (`/responses`) — each "what Copilot did on date X" for that backend, version/date-stamped.
- **Drift detection (B2) — the core mechanism.** Each live probe result is diffed against its committed snapshot; **any difference fails with a readable diff** ("Copilot now ACCEPTS `service_tier`", "`claude-haiku-4.5` now accepts adaptive thinking", "new event `response.foo`", "opus effort widened to include `max`"). Drift becomes a red test → a one-line snapshot update → a code review of whether the catalog/coercions must change — instead of a silent breakage. The 2026-06-05 episode would have been an automatic red.
- **Catalog-still-correct (B3).** `ModelProfileCatalog` (Anthropic side) is asserted against the **live** probe result, not a frozen golden: "opus accepts only `medium` BECAUSE the live probe says so." (The Responses-side catalog/coercions don't exist yet — change 3 — so this change only *records* the Responses live facts as a snapshot for change 3 to build against.)

All B-tests are `[Trait("Category","Integration")]` (skipped in CI, which has no Copilot creds), run on demand + on a schedule. A green offline suite with a stale snapshot is the **expected** state between drift checks; B2 going red is the prompt to reconcile.

## Capabilities

### New Capabilities
- `copilot-contract-snapshots`: Committed per-backend contract snapshots (`/v1/messages` and `/responses`) recording the live wire-truth Copilot exhibits, produced by the asserting probes, version/date-stamped.
- `copilot-drift-detection`: Live probes promoted to asserting (B1) + per-backend drift detection (B2) that fails with a diff when the live backend deviates from its snapshot, plus catalog-still-correct assertions (B3) tying `ModelProfileCatalog` to the live Anthropic probe.

### Modified Capabilities
<!-- None at the OpenSpec spec level. ModelProfileProbe/ResponsesProbe are test code, not specs; ModelProfileCatalog is production data validated (not changed) by B3. -->

## Impact

- **Tests modified**: `tests/CopilotBridge.Playground/ModelProfileProbe.cs` and `ResponsesProbe.cs` gain assertions + snapshot read/compare; a shared snapshot-diff helper. All remain `[Trait("Category","Integration")]`. Probes keep using the existing `PlaygroundClient` (auth + headers already wired).
- **New committed data**: `docs/copilot-anthropic-contract-snapshot.json`, `docs/copilot-responses-contract-snapshot.json`.
- **Production code**: none changed — `ModelProfileCatalog` is *validated against live* by B3, not edited (unless B3 reveals it's already stale, which is then a finding to reconcile).
- **CI unaffected**: B-tests are Integration-tagged and skipped in CI (no Copilot creds); the snapshot diff only runs live.
- **Account facts** (for the probe matrices): Enterprise (`api.enterprise.githubcopilot.com`); `/responses` has 6 models (`gpt-5.3-codex`/`gpt-5.4-mini`/`gpt-5.4`/`gpt-5.5`/`gpt-5-mini`/`mai-code-1-flash-internal`, `docs/codex-protocol-research.md` §2.1); `/v1/messages` has 11 Claude models.
- **No IR changes, no Codex endpoint** — those are changes 1 and 3.
