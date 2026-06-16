## Why

The bridge is adding a second client (Codex), which forces a foundational decision: what is the internal representation (IR) every request flows through? After three parallel research reports (Microsoft.Extensions.AI, LiteLLM, Vercel AI SDK — summarized in `docs/ir-definition-design.md` §1), the decision is settled: **keep our hand-rolled Anthropic Messages DTOs as the IR**, because Anthropic-shape is the most expressive of the three and neutral/OpenAI-shape IRs are demonstrably lossy for Anthropic (LiteLLM's years of dropped `cache_control`, lost thinking signatures, and tool-id clashes).

Anthropic-shape has exactly one gap: it has no typed home for the provider-specific knobs other clients/backends send (Codex/Responses sends `store`, `service_tier`, `include`, `prompt_cache_key`, `text.verbosity`). `MessagesRequest` today **drops** such fields. This change closes that gap by stealing Vercel AI SDK's `providerOptions` namespaced escape-hatch, and **freezes the IR** so changes 2 (contract/drift) and 3 (Codex client) build on a stable foundation.

This is **change 1 of 3**. It is purely foundational: freeze the IR, add the escape-hatch, prove the hot path is byte-unchanged, and stand up the invariant-test framework. **No Codex endpoint, no translators, no live Copilot contract work** — those are changes 2 and 3.

## What Changes

- **Add `ProviderExtensions`** — a namespaced escape-hatch bag (`Dictionary<string, JsonElement>` keyed by provider name, e.g. `["openai"] = {store:false, include:[...]}`), opaque to the pipeline, copied verbatim, AOT-safe (no per-provider `[JsonSerializable]`). Attached at request level (`MessagesRequest`) and content-part level (`ContentBlockParam`) per `docs/ir-definition-design.md` §3.2. Registered in `Models/JsonContext.cs` source-gen context.
- **Freeze the IR contract** (`docs/ir-definition-design.md` §6): `MessagesRequest` + `MessageParam` + `ContentBlockParam` tagged-union = the IR body; `ProviderExtensions` = the lossless tail; reasoning via existing `ThinkingBlockParam{Thinking,Signature}` / `RedactedThinkingBlockParam{Data}` + `OutputConfig.Effort`; tool input/result = `JsonElement` (byte-faithful, already exists); streaming IR = the existing Anthropic SSE event model. The IR body shape does **not** grow per-provider fields — un-modeled knobs go in the bag.
- **Build the A-invariant test framework** (`docs/ir-definition-design.md` §7.0/§7.3/§7.5) — a field-diff harness (extending `ApiComparisonTests.cs`'s JsonNode differ) classifying diffs as `identical | allowed-transform | VIOLATION`, plus tests A1 (round-trip self-inverse), A2 (opaque byte-passthrough of tool args + thinking signature), A3 (bag survival canary — guards Vercel's drop-the-bag bug #5942/#9731), A4 (bag transport of un-modeled knobs).
- **Hot-path no-regression** (§7.5): H1 — replay real `cc-request` fixtures through `Pipeline<MessagesRequest>` before/after the bag, assert serialized upstream body is **byte-identical** (empty bag emits nothing → CC output unchanged). H2 — the entire existing Anthropic suite passes **unchanged**.
- **Committed real fixtures** — de-identified, version-stamped `cc-request-*.json` captured from a real `claude.exe` run via `BridgeFixture` → `request-traces/`. Capture scripts stay gitignored in `docs/scratch/`; fixtures are committed test data used **only as input samples** (not as oracles — that distinction is `docs/ir-definition-design.md` §7.0).

## Capabilities

### New Capabilities
- `ir-provider-extensions`: The frozen IR contract + the `ProviderExtensions` namespaced escape-hatch (request- and part-level), serialized AOT-safely, opaque to the pipeline, copied verbatim.
- `ir-invariant-tests`: The A-invariant test framework — translator self-inverse, opaque byte-passthrough, bag survival, bag transport, and hot-path byte-equality — asserting mathematical properties of our own translation that hold regardless of how Copilot changes.

### Modified Capabilities
<!-- None. No existing OpenSpec specs in openspec/specs/ (research-codex-protocol's specs were research deliverables). The hot-path no-regression guarantee is a requirement under ir-provider-extensions, not a change to an existing spec. -->

## Impact

- **New production code**: `Models/Anthropic/Request/ProviderExtensions.cs` (or `Models/Common/`); a property on `MessagesRequest` and `ContentBlockParam`; a `[JsonSerializable]` entry + converter wiring in `Models/JsonContext.cs`.
- **Modified**: `Models/Anthropic/Request/MessagesRequest.cs` (+ bag property; its "fields intentionally dropped" comment updated to note the bag now carries them), `Models/Anthropic/Request/ContentBlockParam.cs` (+ bag on the base record or per-variant), `Models/JsonContext.cs`.
- **Tests**: new `tests/CopilotBridge.UnitTests` invariant tests + the field-diff harness; `tests/.../Fixtures/cc-request-*.json` committed; the existing suite must pass unchanged.
- **Docs**: `docs/ir-definition-design.md` is the approved design (already written); `docs/pipeline-design.md` §3 updated on completion to name Anthropic-shape-as-IR + `ProviderExtensions` as the official IR contract.
- **AOT invariants binding**: source-gen JSON only (`JsonElement` copied verbatim — no reflection), strongly-typed params, default `internal`. Eyeball binary size after the DTO change (`docs/size-history.md`).
- **No breaking changes; hot path byte-identical** — the bag is additive and inert when empty. `/cc` Claude Code output must not change by a single byte.
