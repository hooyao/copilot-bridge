# Add-model walkthrough — Sonnet 5 (2026-07)

A concrete run of the add flow, taken from the actual commit that added
`claude-sonnet-5`. Use it as a template; the specifics will differ per model but
the *shape* — discover → probe every axis → catalog from results → test → docs —
is what to copy.

## 1. Discover + confirm the id

```bash
dotnet run --project src/CopilotBridge.Cli -- debug list-models --all
```

Copilot exposed `claude-sonnet-5` with `endpoints=[/v1/messages,/chat/completions]`.
`/models` also advertised `effort=[low,medium,high,xhigh,max]`, `ctx=1000000`,
`thinking_budget=1024..32000` — **noted, not trusted.**

Confirmed the client id via the `claude-api` skill + the decompiled Claude Code
source: the complete id is `claude-sonnet-5` (no date suffix).
`CopilotModelRegistry.Normalize("claude-sonnet-5")` is identity (no consecutive
digit-pair, no 8-digit date) — no `Normalize` change needed; locked it with a
guard test.

## 2. Probe every axis (the part that matters)

Added `claude-sonnet-5` to `ModelProfileProbe.AllModels` and wrote targeted
probes mirroring opus-4.8 / sonnet-4.6:

- `Sonnet5_Effort_ReProbe` (effort × adaptive-thinking) → **all of
  low/medium/high/xhigh/max = 200.** First Sonnet-tier model to take `xhigh`.
- `Sonnet5_Thinking_ProbeAcceptance` → null/adaptive = 200, **enabled = 400**
  ("Use thinking.type.adaptive"). So **adaptive-only** — like opus-4.8, NOT like
  its sonnet-4.6 predecessor (which is `ThinkingPolicy.All`). *This is the exact
  trap the rule warns about: family name said "sonnet", contract said "opus-4.8".*
- `Sonnet5_MidConversationSystem_PlacementRules` → the placement matrix returned
  **200 for legal placements** (`U·S` end-of-array, `U·A·U·S`) and the
  *placement-specific* 400 ("must precede an 'assistant' message…") for illegal
  ones — the opus-4.8 signature, NOT the unconditional "Unexpected role 'system'"
  that 4.7/4.6/sonnet-4.6 give. So `AcceptsMidConversationSystem = true`,
  **contradicting Anthropic's "opus-4.8 only" docs.**
- `Sonnet5_ContextOneMillionBeta_ProbeAcceptance` + `…LargePrompt…` → 677k-token
  prompt = 200 with and without the beta → **native 1M**, no `-1m` variant, no
  `StripBetas`.

Run pattern:
```bash
dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~Sonnet5_" --logger "console;verbosity=detailed"
```

## 3. Catalog entry (every field cites a probe)

```csharp
yield return new ModelProfile
{
    CanonicalId = "claude-sonnet-5",
    AcceptedEfforts = ["low", "medium", "high", "xhigh", "max"],   // Sonnet5_Effort_ReProbe
    EffortOnUnsupported = EffortHandling.Strip,
    Thinking = ThinkingPolicy.AdaptiveOnly,                        // Sonnet5_Thinking_ProbeAcceptance: enabled → 400
    MaxThinkingBudget = 32000,
    AcceptsMidConversationSystem = true,   // Sonnet5_MidConversationSystem_PlacementRules — contra Anthropic docs
    AcceptsSpeedFast = false,
    // native 1M (Sonnet5_LargePrompt…) → no StripBetas, no -1m variant
};
```

## 4. Routing

`claude-` prefix → `/v1/messages` automatically; no `CopilotModelRegistry` or
`appsettings.json` change needed.

## 5. Tests (from-contract, mutation-checked)

- `ProfileAdjusterTests`: sonnet-5 coerces `thinking:enabled` → adaptive; keeps
  `xhigh` directly; does NOT strip `context-1m`; `AcceptsMidConversationSystem`
  flag matches the probed set.
- `CodexRoutingAndCatalogTests`: `claude-sonnet-5` (and a dated form) normalize +
  route to `CopilotAnthropic:/v1/messages`.
- **Mutation check:** flipped `AcceptsMidConversationSystem` to false and dropped
  `xhigh` from `AcceptedEfforts` — the corresponding tests went red. Only then
  trusted them.

## 6. Docs + memory + E2E

- Updated `docs/pipeline-design.md` §7, `docs/context-window.md`, model counts;
  dated entry in `docs/design.md`.
- Updated the user-account memory to the new 7-model live set.
- Headless smoke drove `claude-sonnet-5` end-to-end → 200; log showed
  `profile=claude-sonnet-5 target=CopilotAnthropic:/v1/messages`.
