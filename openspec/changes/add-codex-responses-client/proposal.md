## Why

This is the payoff change: it actually serves the Codex CLI. Changes 1 and 2 built the foundation — the frozen Anthropic-shape IR with the `ProviderExtensions` escape-hatch (change 1) and the live Copilot `/responses` contract snapshot + drift detection (change 2). This change adds the `/codex/responses` endpoint and the **four translators (T1–T4)** that route a Codex request through that IR to Copilot's native `/responses` backend, plus the per-model Codex profile catalog and **real `codex.exe` end-to-end validation**.

The architecture is hub-IR, settled with the user: every inbound request is translated to the single IR (the frozen Anthropic shape), runs the **one shared** `Pipeline<MessagesRequest>`, and is routed **by model** to whatever backend it resolves to (`claude-*` → Copilot `/v1/messages`, `gpt-*` → Copilot `/responses`). This is **not** a Responses-passthrough that skips the IR — routing must happen on a backend-agnostic body, which is what makes cross-model substitution possible (the architectural driver: Claude Code could use GPT-5; Codex could use Opus). The deliberate cost is that even Codex's own gpt path round-trips through the Anthropic IR (`Responses →T1→ IR →T2→ Responses`); its fidelity risk is exactly what the round-trip tests guard.

This is **change 3 of 3**. It depends on change 1 (IR + escape-hatch + A-invariant framework) and change 2 (the Responses snapshot the coercions are validated against). It ships **only** the Codex→`/responses` cell; the routing seam is architecture-ready for Codex→Opus and ClaudeCode→GPT5 but those are not wired or tested here.

## What Changes

- **`Models/Responses/` DTOs — fully typed** (decision Q2: no `JsonElement` passthrough, because T1/T2 actively read and rewrite the body, and AOT requires source-gen). `ResponsesRequest` (13 verified fields), `input[]` items (message/function_call/function_call_output/reasoning), content parts (input_text/input_image/output_text), `tools[]` variants by `type`, `Reasoning`, `TextControls`, and the SSE event DTOs T3/T4 need. Grounded in `references/openai-sdk-pkg/`, registered in `Models/JsonContext.cs`.
- **Routing**: wire `CopilotModelRegistry.Resolve` to return the **existing-but-unused** `BackendVendor.CopilotResponses` + `/responses` for the Codex model ids (`gpt-5.3-codex`/`gpt-5.4`/`gpt-5.4-mini`/`gpt-5.5`/`gpt-5-mini`/`mai-code-1-flash-internal`), instead of today's blanket `CopilotOpenAi` + `/chat/completions`. Confirm `Normalize` no-ops on `gpt-5.3-codex`.
- **`CodexModelProfileCatalog`** — per-model effort profiles (two inverted profiles from `docs/codex-protocol-research.md` §2.2: "large" reject `minimal`; "small" reject `none`+`xhigh`), table-driven row-by-row from change 2's Responses contract snapshot (never family-name extrapolated), plus the 2 uniform coercions (strip `service_tier`, drop `image_generation` tool) and the `mai-code-1-flash-internal`-rejects-custom-tools(500) flag. Unknown model → clear error (mirror `UnknownModelException`).
- **The four translators** registered into the **same shared** `Pipeline<MessagesRequest>`: T1 `ResponsesToIrInboundAdapter` + T4 `IrToResponsesOutboundAdapter` (the Codex client edge — real translators, not identity), and `CopilotResponsesStrategy` holding T2 (IR→Responses wire) + T3 (Responses SSE→IR), selected by `target.Vendor == CopilotResponses`. `ICopilotClient.PostResponsesAsync` (POST `{baseUrl}/responses`, bearer + official VS Code Copilot headers; drop Codex's `x-codex-*`; `Copilot-Vision-Request` when `input_image` present; `x-initiator` per last input item).
- **`Endpoints/Codex/CodexResponsesEndpoint.cs`** — `POST /codex/responses` (path = `base_url + /responses`, verified §3.4; not `/codex/v1/...`). Reads+audits body → T1 → IR → shared pipeline → T4 → writes SSE/buffered. Same audit/summary plumbing as `/cc`, leaner (no count_tokens, no `/codex/models`). `app.MapCodexResponses()` in `ServeCommand`; DI in `BridgeServiceCollectionExtensions`.
- **Exhaustive validation on real data** (the user's central demand): A-invariant round-trip/parity tests (A1–A6) on de-identified committed Codex captures, B3 coercion-still-needed against the live probe, hot-path byte-equality (H1/H2), and a live `codex.exe` end-to-end run (E1).

## Capabilities

### New Capabilities
- `codex-responses-endpoint`: The `POST /codex/responses` client surface + the Codex Responses DTOs + the T1–T4 translators wired into the shared IR pipeline + the `CopilotResponsesStrategy` + official-Copilot header fidelity + SSE streaming.
- `codex-model-profiles`: The per-model Codex effort profile catalog + the uniform coercions (strip `service_tier`, drop `image_generation`), grounded row-by-row in the live contract snapshot, with a clear unknown-model error.
- `codex-parity-validation`: The exhaustive parity/round-trip validation on real captured data (A1–A6), B3 coercion-vs-live, hot-path byte-equality, and the live `codex.exe` end-to-end proof.

### Modified Capabilities
<!-- None at the OpenSpec spec level. CopilotModelRegistry/JsonContext/ServeCommand/BridgeServiceCollectionExtensions are production code touched additively; ir-provider-extensions (change 1) and copilot-drift-detection (change 2) are dependencies, not modified specs. -->

## Impact

- **New production code**: `Models/Responses/*`, `Endpoints/Codex/CodexResponsesEndpoint.cs`, the T1/T4 adapters, `CopilotResponsesStrategy` (T2/T3), `Pipeline/Routing/CodexModelProfile*.cs`, `ICopilotClient.PostResponsesAsync` + impl.
- **Modified**: `Models/JsonContext.cs` (+Responses DTOs), `Pipeline/Routing/CopilotModelRegistry.cs` (Codex ids → `CopilotResponses`/`/responses`), `Hosting/BridgeServiceCollectionExtensions.cs` (register T1/T4/strategy/catalog into the shared pipeline registry), `Hosting/ServeCommand.cs` (`MapCodexResponses`), `docs/pipeline-design.md` §3 (finalize as-built), `README.md`/`AGENTS.md` roadmap (Codex → implemented).
- **Tests**: A-invariant (CI, reuse change-1 harness) + B3 + hot-path + live `codex.exe` e2e (`[Trait("Category","Integration")]`, ephemeral non-8765, `~/.codex/config.toml` untouched). Real fixtures from `docs/scratch/codex-capture/` de-identified + committed.
- **AOT invariants binding**: source-gen JSON only, strongly-typed params, official-client headers, profiles are snapshot-derived facts; eyeball binary size after the DTO additions (`docs/size-history.md`).
- **Hot path byte-identical**: adding `CopilotResponsesStrategy` to the registry must not change `/cc` routing or output — guarded by H1.
- **No breaking changes** — additive; `/cc` untouched.
