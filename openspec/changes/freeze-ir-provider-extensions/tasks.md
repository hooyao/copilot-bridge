# Implementation Tasks

> Change 1 of 3. Design: `docs/ir-definition-design.md` (approved). Scope: freeze
> IR + escape-hatch + A-invariant tests + hot-path byte-equality. No Codex, no
> live Copilot contract work.

## 1. Provider-extensions type + IR freeze

- [ ] 1.1 Add `ProviderExtensions` type (`Models/Common/ProviderExtensions.cs`): a record wrapping `Dictionary<string, JsonElement> ByProvider`, opaque, copied verbatim. Default everything `internal`.
- [ ] 1.2 Register `ProviderExtensions` in `Models/JsonContext.cs` (`[JsonSerializable]`); confirm `JsonElement` values serialize source-gen (no reflection). Decide explicit-property vs `[JsonExtensionData]` (lean explicit).
- [ ] 1.3 Add a `ProviderExtensions?` slot to `MessagesRequest` (request level); update the "fields intentionally dropped" comment to note the bag now carries un-modeled knobs. Ensure `WhenWritingNull` so an empty/absent bag emits nothing.
- [ ] 1.4 Add a `ProviderExtensions?` slot to `ContentBlockParam` (part level — base record or per-variant per `docs/ir-definition-design.md` §3.2); may ship unused.
- [ ] 1.5 Confirm AOT build clean (`dotnet build`); eyeball published size delta later (7.3).

## 2. A-invariant test framework

- [ ] 2.1 Extend the `ApiComparisonTests` JsonNode differ into a reusable field-diff harness that classifies each diff as `identical | allowed-transform | VIOLATION`, with an explicit allowed-transform allowlist (our transforms only — e.g. model-id normalize). Fail only on VIOLATION.
- [ ] 2.2 A1 round-trip self-inverse: run captured request samples into the IR and back; assert equality under the per-field fidelity bar (`docs/ir-definition-design.md` §7.1) via the harness.
- [ ] 2.3 A2 opaque byte-passthrough: assert tool-call `input` JSON and thinking-block `Signature` are byte-identical input→output (raw-text compare, not value compare).
- [ ] 2.4 A3 bag survival canary: inject `ProviderExtensions["openai"]["__canary__"]` mid-IR; assert byte-identical at the outbound boundary (guards Vercel drop-the-bag #5942/#9731).
- [ ] 2.5 A4 bag transport: assert `store`/`include`/`prompt_cache_key`/`text.verbosity` ride the bag through the IR and reappear intact.

## 3. Real fixtures (input samples, not oracles)

- [ ] 3.1 Capture real `cc-request-*.json` from a `claude.exe` run via `BridgeFixture` → `request-traces/` (plain turn, tool-call turn, multi-turn, a thinking turn). De-identify (auth already `<redacted>`); stamp client version + capture date.
- [ ] 3.2 Commit fixtures under `tests/.../Fixtures/`; document the refresh procedure; keep capture scripts gitignored in `docs/scratch/`.

## 4. Hot-path no-regression

- [ ] 4.1 H1 byte-identical CC output: replay each `cc-request-*.json` through `Pipeline<MessagesRequest>` and serialize the upstream body; assert byte-identical to a pre-change golden (the bag is inert when empty). This is the gate the whole change rests on.
- [ ] 4.2 H2 existing suite unchanged: run the full current Anthropic playground + unit suite; assert it passes with **no test edits**.

## 5. Verification & docs

- [ ] 5.1 `dotnet test CopilotBridge.slnx --filter "Category!=Integration"` green (the new invariant + harness tests are pure-logic, CI-safe).
- [ ] 5.2 Eyeball published AOT binary size after the DTO change; record in `docs/size-history.md`.
- [ ] 5.3 Update `docs/pipeline-design.md` §3 to name Anthropic-shape-as-IR + `ProviderExtensions` as the official IR contract (the IR is now frozen).
