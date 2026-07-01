# Remove-model walkthrough — the 2026 reconciliation

A concrete run of the remove flow, from the commit that pruned the models
Copilot retired. Six ids were suspect (missing from the live `/models` list);
the point of this walkthrough is that **absence was not the delete license — a
live 400 was.**

## 1. Discover the gap

```bash
dotnet run --project src/CopilotBridge.Cli -- debug list-models --all
```

Live claude set was 7 ids. The catalog had 12. The 5 "extra" Anthropic ids —
`claude-opus-4.5`, `claude-opus-4.6-1m`, `claude-opus-4.7-high`,
`claude-opus-4.7-xhigh`, `claude-opus-4.7-1m-internal` — plus the Codex
`mai-code-1-flash-internal` were all missing from `/models`.

**Did NOT delete on that basis.** The `-1m-internal` / `-high` / `-xhigh` ids
were originally kept *precisely because they routed 200 while unadvertised* —
the exact "unknown-but-working" trap. So each got a liveness probe.

## 2. Prove retirement with a live probe

Added `RetiredCandidate_LivenessProbe` (Anthropic) + `MaiCode_LivenessProbe`
(Codex) — minimal request per suspect id, log the status:

```bash
dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~RetiredCandidate_LivenessProbe|FullyQualifiedName~MaiCode_LivenessProbe" --logger "console;verbosity=detailed"
```

Results:
- All 5 Anthropic variants → **400** ("not available for integrator" /
  "model_not_supported"). Delete license granted.
- `mai-code-1-flash-internal` → **400**; but `mai-code-1-flash-picker` → **200**.
  So it wasn't "deleted" — it was **renamed**. Swapped the id, didn't drop the
  profile.

The 400 error body also handed over the authoritative integrator allowlist
(`claude-opus-4.6/4.7/4.8, sonnet-4.6, sonnet-5, …`) — a second confirmation.

## 3. Check what absorbed the retired capability

`opus-4.6-1m` / `opus-4.7-1m-internal` existed only to unlock 1M context.
**Probed the base ids** (`OpusBase_LargePrompt_ProbeOneMillionContextSupport`):
opus-4.6 and opus-4.7 **base** now serve a 639k/677k-token prompt → 200, with
and without the beta. So 1M wasn't lost — it moved to the base. That meant the
two `appsettings.json` redirect rules (`opus-4.x + 1M beta → -1m variant`) had no
valid target *and* were unnecessary → **removed the rules entirely** rather than
repointing them.

## 4. Prune every reference

```bash
grep -rln "opus-4.6-1m\|opus-4.7-1m-internal\|opus-4.7-high\|opus-4.7-xhigh\|opus-4\.5\|mai-code-1-flash-internal" src/ tests/ docs/ | grep -vE "bin/|obj/|request-traces/|/log"
```

Fixed each real hit:
- `ModelProfileCatalog` — deleted the 5 profiles; the opus-4.7 base profile's
  `EffortToVariant` pointed at the now-deleted `-high`/`-xhigh` siblings →
  switched `EffortOnUnsupported` to `Strip`, dropped the map.
- `CopilotModelRegistry.ResponsesModelIds` — `mai-code-1-flash-internal` →
  `mai-code-1-flash-picker`.
- `CodexModelProfileCatalog` — renamed the row; initially marked it
  PLAYGROUND-PENDING (liveness-probed, but its effort matrix not re-probed on the
  new suffix). **Follow-up (2026-07): re-probed directly** —
  `ResponsesProbe.MaiCodePicker_Effort_ReProbe` / `_Tool_ReProbe` confirmed the
  extrapolated "small" set + `RejectsCustomTools` were correct on the live
  `-picker` id (none/xhigh → 400, custom apply_patch → 500), so the pending note
  was removed. The lesson: extrapolation is a *labeled placeholder*, discharged by
  a probe — not a permanent guess.
- `ModelProfileProbe.AllModels` — removed the retired ids.
- `appsettings.json` — removed the two dead redirect rules.
- Unit tests keyed on the ids (`CodexRoutingAndCatalogTests`,
  `CodexRequestBuildTests`) — updated to the live id.

## 5. Tests + docs

- Ran `dotnet test tests/CopilotBridge.UnitTests --filter "Category!=Integration"` — green.
- Updated `docs/pipeline-design.md` (model count 11→7, ThinkingPolicy preset
  comments, the redirect table), `docs/routing.md` (shipped-config section),
  `docs/context-window.md` (the ctx table + Opus §), the user-account memory, and
  added a reconciliation memory.

## The lesson

Two ids that looked identically "gone" from `/models` had opposite fates: 5 were
genuinely retired (400), 1 was merely renamed (the old id 400s, a new id 200s).
Only the probe distinguished them. **Never batch-delete on list absence.**
