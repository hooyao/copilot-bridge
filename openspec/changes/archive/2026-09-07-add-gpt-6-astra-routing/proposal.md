## Why

GitHub Copilot now serves `gpt-6-astra` on `/responses`, while the bridge neither recognizes that model nor offers a safe migration path from the flagship `gpt-5.6-sol` client identity. Live Copilot probes also establish a real effort incompatibility: Astra rejects `none` and `minimal`, values that GPT-5.6-era clients can select, so a model-only rewrite can fail before a turn begins.

## What Changes

- Add a live-probe-grounded `gpt-6-astra` Responses profile and registry entry.
- **BREAKING (fresh installs):** Route stock `gpt-5.6-sol` traffic to `gpt-6-astra` through `Routing.Locations`, preserving the flagship workload role rather than collapsing Luna, Terra, or Sol Fast into Astra. Existing installations retain their user-owned Locations array during config migration and must opt in explicitly.
- Map source efforts `none` and `minimal` to Astra's accepted `low` effort and retain `low`, `medium`, `high`, `xhigh`, and `max` unchanged.
- Extend the Responses contract snapshot and catalog-versus-live guard with Astra's exact effort, field, tool, and structured multimodal-output facts.
- Retarget the current real-Codex behavior suite to exercise the configured route and require a complex tool loop plus a clean client dispatch log.
- Update the routing, pipeline, context-window, protocol-research, and decision documentation with official OpenAI and live Copilot evidence.

## Capabilities

### New Capabilities

- `gpt-6-astra-routing`: Exact Astra backend support, the `gpt-5.6-sol` compatibility route, target-specific effort coercion, and real-client acceptance evidence.

### Modified Capabilities

None.

## Impact

- Affected production surfaces: Responses model registry/profile catalog, stock `appsettings.json` routing, and latest-model behavior-test target selection.
- Affected verification: live Responses probes and snapshot, routing/catalog unit tests, captured-byte fidelity checks, and real `codex.exe` client evidence.
- No new dependency or endpoint is introduced; Astra continues to use Copilot's native `/responses` endpoint and the existing T1–T4 pipeline.
- Existing upgraded installations keep their user-owned `Routing.Locations` array during config migration; the stock template and fresh installs receive the new default route.
