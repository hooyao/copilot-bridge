## Context

Copilot discovery on 2026-09-07 exposes `gpt-6-astra` at `/responses` and `ws:/responses`, with a 1,000,000-token context window, 872,000-token prompt limit, 128,000-token output limit, and advertised efforts `low`, `medium`, `high`, `xhigh`, and `max`. The public [OpenAI model page](https://developers.openai.com/api/docs/models/gpt-6-astra) describes the public API deployment as 1,050,000/922,000/128,000, so the bridge must retain Copilot's smaller deployment limits rather than copy public metadata.

The [OpenAI Astra migration guide](https://developers.openai.com/api/docs/guides/latest-model/gpt-6-astra) says Responses tool calling is required, `none` and `minimal` must move to `low`, and `temperature`, `top_p`, `top_logprobs`, and Responses `message.output_text.logprobs` are unsupported. The existing bridge already sends Codex through `/responses` and preserves target-owned unknown fields. Live Copilot probes establish the bridge-specific facts:

- liveness and real Codex 0.153.3 request replay return 200;
- `low`/`medium`/`high`/`xhigh`/`max` return 200, while `none`/`minimal`/`ultra` return 400;
- function, custom grammar, web-search, encrypted-reasoning include, prompt cache key, and reasoning summary are accepted;
- `store:true`, `service_tier`, and image generation are rejected exactly like the existing catalog-wide rules;
- synchronous and `async:true` custom tools retain the existing `custom_tool_call` item and SSE event families;
- a two-turn structured image function output returns 200 and Astra identifies the red image correctly.

The stock Codex catalog exposes `gpt-5.6-sol` instructions and client behavior, while current clients need not know an Astra-specific catalog entry. A route therefore keeps the client-facing identity stable and swaps only the resolved upstream target.

## Goals / Non-Goals

**Goals:**

- Add an exact, live-probed Astra Responses profile and dispatch entry.
- Make the stock configuration route exact `gpt-5.6-sol` traffic to Astra, with explicit `none`/`minimal` to `low` migration.
- Keep client-visible catalog limits conservative for the resolved Astra target while retaining the `gpt-5.6-sol` client slug and instructions.
- Preserve native Responses request/item/event shapes unless live evidence requires a mutation.
- Prove a multi-step, multi-tool turn with real Codex and judge it from Codex's own dispatch log.

**Non-Goals:**

- Routing Luna, Terra, Sol Fast, Claude, or arbitrary GPT aliases to Astra.
- Adding Astra-specific async orchestration to current Codex clients; the bridge only preserves fields and events that pass through it.
- Copying public OpenAI context limits over Copilot's lower deployment limits.
- Rewriting current Codex base instructions or synthesizing an Astra entry absent from the exact official Codex catalog.

## Decisions

### Exact profile and registry entry

Add `gpt-6-astra` to the explicit Responses allowlist and `CodexModelProfileCatalog`. Its accepted efforts are exactly `low`, `medium`, `high`, `xhigh`, and `max`; its fallback is `low`, matching the official migration guidance and the lowest live-accepted tier. The profile enables structured multimodal function output and does not reject custom tools. The existing catalog-wide `store:true`, `service_tier`, and image-generation removals remain applicable.

Prefix dispatch or borrowing the gpt-5.6 profile was rejected: the former can misroute unrelated future GPT ids, while the latter would forward `none` and produce a live 400.

### Compatibility route under the existing client identity

The active stock `Routing.Locations` array receives one exact rule: `gpt-5.6-sol` resolves to `gpt-6-astra`, with `none` and `minimal` mapped to `low`. The rule is exact and first-match-wins; the other gpt-5.6 tier ids remain unchanged. Response model rewriting continues to report the originally requested model to the client.

Directly advertising only `gpt-6-astra` was rejected because the exact Codex catalog controls prompt/tool behavior and older current clients may not contain an Astra entry. A disabled example was rejected for this change because the requested behavior is an actual stock route; documentation will state that config migration preserves existing installations' full Locations arrays.

### Route-aware catalog limits

`CodexCatalogProjector` statically evaluates Locations with the source model fixed and effort/header axes unknown. Only a first potentially matching Location that is unconditional for that model proves an invariant target. If an earlier request-dependent rule could match, a validated catalog hides the source alias rather than using a later fallback's capacity; overlay failure keeps the existing reviewed-baseline fallback. For an invariant route, the source catalog entry keeps its slug/instructions while context and compaction limits come from the resolved target's live Copilot metadata. A cross-model alias whose validated target limits are missing or inconsistent is hidden instead of retaining potentially larger source limits; a direct model keeps the existing reviewed-baseline fallback.

Leaving source limits untouched was rejected because Codex would compact against the 922,000-token gpt-5.6 prompt budget while Copilot Astra currently accepts only 872,000 prompt tokens.

### Preserve the existing Responses grammar

No Astra-only request/response translator is added. A full real gpt-5.6 Codex request succeeds when only its model is changed, and synchronous plus async custom-tool probes use the existing item/event families. The permanent contract sweep gains Astra as a row and remains the two-way guard for every rewrite-driving fact.

## Risks / Trade-offs

- [Copilot changes Astra's contract after release] → The live B2 snapshot and B3 catalog comparison fail on effort, fields, tools, or multimodal behavior before a future reconciliation ships.
- [A fresh install expected literal gpt-5.6-sol] → The route is documented as a fresh-install default and is removable by clearing `Routing.Locations`; Luna, Terra, and Sol Fast remain literal.
- [Existing installations do not acquire the new route automatically] → Config migration intentionally preserves the old complete Locations array; release notes and routing docs provide the exact opt-in block.
- [Catalog routing cannot represent request-dependent rules] → Fixed-model tri-state analysis applies target limits only for a provably invariant first match and hides ambiguous aliases from validated catalogs.
- [Astra emits a client-incompatible tool payload despite bridge 200s] → Acceptance requires a real client tool round-trip and zero router/dispatch fatal rows in the manifest-selected `logs_2.sqlite`.

## Migration Plan

1. Ship the exact profile, registry entry, route, and route-aware catalog projection together.
2. Existing installations add the documented Location explicitly if their preserved array is empty; fresh installs receive it from the stock template.
3. Roll back by removing the Location. Direct `gpt-6-astra` requests remain supported independently of the alias route.

## Open Questions

None. The remaining compatibility claims are acceptance gates to be resolved by the contract sweep and real-client run, not design assumptions.
