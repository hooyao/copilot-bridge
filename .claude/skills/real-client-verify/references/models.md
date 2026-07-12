# Latest-models-only policy

This is **agent-client behavior testing, not an LLM-API matrix.** The flywheel asks
"can the newest real client drive the newest model through the bridge?" — so it targets
only the **latest Claude id and the latest gpt id**, not every historical model. Old
models are covered (where they still matter) by the `ApiContract` captured-byte replays,
which don't need a live client.

## The ids under test

Single source of truth in code: `ClientBehaviorSupport` in
`tests/CopilotBridge.Playground/Headless/ClientBehavior/ClientBehaviorSupport.cs`.

| Constant | Today | Used by |
| --- | --- | --- |
| `LatestClaude` | `claude-opus-4.8` | CC-native `/cc` cases, and the CC→gpt client-facing id |
| `LatestGpt` | `gpt-5.6-sol` | Codex `/codex` cases, and the CC→gpt route target |

## Bumping when Copilot ships a newer model

1. **Do the catalog work first, elsewhere.** Adding/probing a new model's wire profile
   is the `copilot-model-sync` skill's job (live probe → profile → `ResponsesModelIds`
   etc.). This skill does **not** add models; it only tracks which id the *behavior*
   suite drives. Don't bump the constant to an id the catalog doesn't yet know.
2. **Bump the constant** in `ClientBehaviorSupport` once the new id is in the catalog and
   its load-task smoke passes. That retargets every behavior case at once.
3. **Update the CC→gpt route** if the new gpt id replaces the route target: the
   `CcToGpt` scenario promotes `Routing._Locations_disabled` from the production
   `appsettings.json`, so update that example's `Use.Model` (and any `EffortMap`) in
   `src/CopilotBridge.Cli/appsettings.json` — the scenario picks it up automatically.
4. **Re-run the flywheel** on the new id and render the verdict from the client log
   before trusting it. A new id that a plain turn accepts can still fatal on the real
   tool loop (that is the whole reason this skill exists).

## Why not a matrix

A model × effort × stream × tools matrix belongs to API-contract testing (the
`ApiContract` probes already sweep that offline/cheaply). Re-driving a real client
across a matrix is slow, flaky, and tests the model more than the bridge. The behavior
flywheel's value is depth on the *newest* client+model on the paths that actually break
— so keep it to the latest ids and let the replay suite hold the historical line.
