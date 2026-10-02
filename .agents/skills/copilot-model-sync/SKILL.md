---
name: copilot-model-sync
description: >-
  Reconcile the bridge's model catalog with GitHub Copilot's live model list —
  add support for a new model, or remove support for a model Copilot retired.
  Use this whenever the user wants to add/support a new model (e.g. "add Sonnet
  5", "support gpt-5.6", "Copilot shipped opus-5"), remove/drop a model ("opus-4.6-1m
  is gone from Copilot, delete it"), reconcile/sync/align the catalog with
  Copilot, or investigate why a model 400s. This repo hard-refuses to guess a
  model's wire shape, so every add/remove MUST be grounded in a live probe — do
  NOT edit ModelProfileCatalog.cs from family-name intuition. Follow this skill.
compatibility: >-
  Requires a working Copilot login (`cc-copilot-bridge auth login` done) and the
  ability to run the Playground integration probes (Windows + DPAPI, live Copilot).
metadata:
  author: cc-copilot-bridge
  version: "1.0"
---

# Copilot model sync

Keep `ModelProfileCatalog` (and its Codex sibling) aligned with the models
GitHub Copilot actually serves on this account. Two operations:

- **Add** a model the bridge doesn't know yet (new profile from a live probe).
- **Remove** a model Copilot retired (prune its profile + every reference).

## The one rule everything follows from

**Copilot's `/models` list and its advertised capabilities lie in BOTH
directions — never trust them, always probe the live endpoint.**

- `/models` **omits** ids that still work: the `-1m-internal` / `-high` / `-xhigh`
  variants routed `200` for months while never appearing in the list.
- `/models` **lists** capabilities the gateway rejects at runtime: haiku-4.5
  advertises adaptive thinking but 400s it; docs claimed mid-conv `role:"system"`
  is "opus-4.8 only" but sonnet-5 accepts it too.

So: **a model's absence from `/models` is NOT sufficient grounds to delete its
profile, and its presence is NOT sufficient grounds to add one.** The ground
truth is always a live probe (`tests/CopilotBridge.Playground/ApiContract/ModelProfileProbe.cs`
for Anthropic `/v1/messages`, `ResponsesProbe.cs` for Codex `/responses`). The
whole catalog exists because guessing a wire shape produces a silent Copilot 400
the user can't diagnose. Honor that: probe first, edit second, cite the probe in
a code comment.

> Note: the bridge now *fuzzy-matches* an unknown id to the nearest profile as a
> best-effort fallback (`ModelNameMatcher`), so a missing profile no longer hard-
> 400s. That's a safety net for models newer than the build — it does **not**
> replace adding a real, probed profile. This skill is how you add the real one.

## Step 0 — Snapshot the live set

Run the discovery command and read what Copilot actually exposes:

```bash
dotnet run --project src/CopilotBridge.Cli -- debug list-models --all
```

- `claude-*` rows with `/v1/messages` in `endpoints=[…]` → Anthropic catalog
  (`ModelProfileCatalog`).
- `gpt-*` / `mai-code-*` rows with `/responses` → Codex catalog
  (`CodexModelProfileCatalog` + the `ResponsesModelIds` allowlist in
  `CopilotModelRegistry`).
- Also capture the **integrator allowlist** — when any model 400s, Copilot's
  error body lists the currently-available models for `vscode-chat`. That list is
  a second source of truth and often more current than `/models`.

Diff that live set against the catalog's `KnownIds`. Every id that differs is a
candidate — but a candidate for **probing**, not for editing yet.

## Adding a model

Full worked example (Sonnet 5): `references/add-model-walkthrough.md`. The loop:

1. **Confirm the exact id** Copilot exposes (Step 0) AND the id Claude Code
   sends. For Claude models, verify `CopilotModelRegistry.Normalize` maps the
   client id → the canonical catalog id (usually identity for dotted ids; add a
   `Normalize` test if a date suffix or digit-pair merge is involved). For a
   Claude model, consult the `claude-api` skill for the authoritative id string.
2. **Probe the wire contract.** Add the new id to `ModelProfileProbe.AllModels`
   (drives the thinking × effort matrix) plus targeted probes mirroring the
   nearest known model: combined effort+adaptive-thinking (the shape Claude Code
   really sends), mid-conv-`role:"system"` placement matrix, and a >200k-token
   1M-context probe. Run them live:
   ```bash
   dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~<YourProbe>" --logger "console;verbosity=detailed"
   ```
   Read the `→ HTTP N` lines: `200` = accepted, `400` = rejected. **Do not skip
   a probe because the model "looks like" a known family** — sonnet-5's contract
   matched opus-4.8, not its own sonnet-4.6 predecessor.
3. **Fidelity check — re-confirm every REWRITE-causing finding on real captured
   client bytes.** The probes above are hand-written minimal requests (~150 bytes,
   no system blocks, no betas, non-streaming). A real Claude Code request is ~20×
   larger and carries 3 system blocks, ~8 `anthropic-beta` tokens, tool
   definitions, `cache_control`, and `stream:true`. So a minimal-request result
   answers "does Copilot accept this shape *in isolation*" — **not** "does this
   rule still hold on what the client actually sends."

   That gap only matters for findings the bridge acts on. Sort your results:

   | Finding | Bridge does | Fidelity check |
   | --- | --- | --- |
   | Model accepts X | nothing (passthrough) | not needed — a wrong "accept" surfaces as Copilot's own visible 400 |
   | Model **rejects** X → profile makes the bridge **strip / clamp / coerce** | rewrites the request | **REQUIRED** |

   The asymmetry is the point: a false *accept* fails loudly upstream, but a
   false *reject* makes the bridge silently downgrade a request the backend would
   have taken — the user loses capability with no error anywhere. That is
   unobservable in production, so it must be ruled out before shipping.

   Replay a real captured body, mutating **only the axis under test**:

   ```csharp
   // Bodies land in tests/behavior-runs/<serve-dir>/*-upstream-req.json after a
   // Kind=ClientBehavior run (ServeProcess forces tracing on). Take one, change
   // one field, POST it straight at Copilot via PlaygroundClient — bypassing the
   // bridge.

   // The capture MUST already name the model you are profiling. PlaygroundClient
   // posts body.model verbatim, so a capture from a sibling model would validate
   // that sibling — and rewriting model here would change a second variable,
   // which is exactly what makes a model-specific rejection look "confirmed" on
   // the wrong model. Assert, don't coerce.
   var capturedModel = captured["body"]!["model"]?.GetValue<string>()
       ?? throw new InvalidOperationException("capture has no body.model");
   Assert.Equal(modelUnderTest, capturedModel);   // pick a different capture if this fails

   // BridgeIoSink writes a parseable body as a NESTED OBJECT and only falls back
   // to a string when parsing failed, so handle both shapes.
   var raw = captured["body"]!;
   var body = raw is JsonValue v
       ? JsonNode.Parse(v.GetValue<string>())!.AsObject()
       : raw.DeepClone().AsObject();

   // Leave `stream` exactly as captured. Streaming is one of the things that
   // could plausibly change the answer, so flipping it off would silently drop a
   // constraint that only binds under stream:true — and it would violate this
   // step's own one-variable rule. No need anyway: both TryPost*Async use
   // HttpCompletionOption.ResponseContentRead, so a non-2xx body is fully read
   // either way.

   // The two backends carry the SAME axis under different names — Anthropic uses
   // output_config.effort, Codex/Responses uses reasoning.effort. Writing the
   // Anthropic path into a /responses body sets a field Copilot ignores, so the
   // check would pass while exercising nothing. Pick by capture type, and mutate
   // in place: replacing the parent object drops siblings (a real capture's
   // output_config also carried a json_schema `format`), which would change more
   // than one variable and defeat the point of the check.
   var effortParent = isResponsesCapture ? "reasoning" : "output_config";
   (body[effortParent] ??= new JsonObject()).AsObject()["effort"] = effort;

   // Route to the endpoint the capture came from: a /responses capture posted to
   // /v1/messages would test the wrong backend entirely.
   var (status, resp) = isResponsesCapture
       ? await client.TryPostResponsesAsync(body.ToJsonString())
       : await client.TryPostMessagesAsync(
             body.ToJsonString(),
             anthropicBeta: captured["headers"]?["anthropic-beta"]?.GetValue<string>());
   ```

   > **Effort is only the worked example.** Whatever axis you probed, locate it in
   > the *capture's own* schema before mutating — the two backends diverge on more
   > than this one field (thinking shape, tool arrays, beta headers). A mutation
   > that lands on a field the target backend ignores makes the check pass while
   > testing nothing, which is worse than skipping it.

   Same verdict on both = the rule is model-level and your profile's scope is
   right. **Divergence = the minimal probe misled you** — a beta, a system block,
   or streaming changed the answer, and the profile would encode a rule that does
   not apply to real traffic. Worked example: opus-5's `thinking:disabled` ×
   `xhigh`/`max` rejection, confirmed byte-identical (2756 B, 3 system blocks, 8
   betas) before the clamp shipped.

   **Prerequisite for a brand-new model: you must create the capture first.**
   Only `Kind=ClientBehavior` cases produce `tests/behavior-runs/` (that path is
   written by `ServeProcess`); the older `CodexLoadTaskSmoke` is `Kind=ApiContract`
   and runs on `BridgeFixture`, so it writes nothing there. The behavior cases
   also target fixed latest-model constants
   (`ClientBehaviorSupport.LatestClaude` / `LatestGpt`), so a model that is not yet
   the constant produces no capture of its own. Before this step can run on a new
   id, either retarget the constant (only once the catalog knows the id — see
   `real-client-verify`'s `models.md`) or add a candidate-targeted behavior case.
   Then run it to generate the capture.

   **A new Codex id needs routing before it can produce a capture at all.**
   Pointing a behavior case at the id is not enough: until it is in
   `ResponsesModelIds`, `CopilotModelRegistry` sends it to the unimplemented
   chat-completions branch (`CopilotModelRegistry.cs:51-64`), so no `/responses`
   capture is written — and if no catalog entry fuzzy-matches, `ModelRouterStage`
   fails the request outright before any upstream call. So for a Codex candidate
   the order is: allowlist the id (+ a provisional `CodexModelProfile` if nothing
   close matches) → run the behavior case → come back and do this fidelity check
   → only then finalize the profile in step 4. The provisional entry is scaffolding
   to obtain evidence, not a probed fact; it must be replaced by probed values
   before you ship.
4. **Write the `ModelProfile`** in `ModelProfileCatalog.BuildDefault()` (or a
   `CodexModelProfile` in `CodexModelProfileCatalog`) with every field grounded
   in a probe result, and a code comment citing the probe method name for each
   non-obvious field. Fields: `AcceptedEfforts`, `EffortOnUnsupported`,
   `Thinking` (`AdaptiveOnly` / `AdaptiveOrDisabled` / `EnabledOnly` / `All`),
   `MaxThinkingBudget`, `AcceptsMidConversationSystem`, `StripBetas`.

   > Pick the thinking policy from the probe's *rejections*, not from the family.
   > If only `enabled` was rejected, the model takes `disabled` too and the policy
   > is **`AdaptiveOrDisabled`** — choosing `AdaptiveOnly` there silently coerces a
   > user's explicit `thinking:disabled` back to adaptive (re-enabling and billing
   > reasoning they turned off) and makes any disabled-thinking constraint
   > unreachable. That is exactly opus-5's case.
5. **Routing check.** Vendor dispatch is prefix-only (`claude-*` → `/v1/messages`,
   gpt/mai-code in `ResponsesModelIds` → `/responses`), so a claude id needs no
   registry change. A new Codex id must be **added to `ResponsesModelIds`** in
   `CopilotModelRegistry` or it falls through to the OpenAI-chat branch. Add a
   `Routing.Locations` entry in `appsettings.json` only if the model needs a
   deliberate remap (e.g. a context-window alias like `gpt-5.5-1m`).
5a. **Codex picker catalog — MANDATORY for `gpt-*` / `mai-code-*`.** Routing and a
   live Copilot `/responses` success prove inference only; they do not make the id
   selectable in Codex. Inspect the exact installed Codex binary's bundled catalog
   (`codex debug models --bundled`), its cached/remote catalog, and the bridge's
   `GET /codex/models?client_version=...` projection. If the client's exact
   baseline lacks the slug, capture its **complete** model resource from an
   official `openai/codex` revision, pin the commit and SHA-256 provenance in
   `Catalogs/Codex/Supplemental/capture.json`, and add the resource to
   `Supplemental/models.json` and `CodexSupplementalCatalog.ReviewedSlugs`.
   Do not invent Codex-owned instructions, tools, reasoning levels, visibility,
   or minimum client version from a sibling or a family name. If no complete
   official resource exists, report picker support unavailable; explicit model
   selection can still be tested separately. The projector must expose the id
   with `visibility=list` and `supported_in_api=true` when the exact Copilot
   backend is live, and must preserve a newer same-slug client baseline without
   duplication. Reconcile/remove stale supplements when the official baseline
   later includes them; do not infer retirement from a missing picker row.
   Reconcile picker-advertised `supported_reasoning_levels` with the **probed
   backend** effort set. An official Codex resource may offer an effort that
   Copilot rejects (GPT-6.1 Sol's Ultra is the worked example); apply an
   explicit projection override to both supplemental and future same-slug
   baseline records while keeping the pinned source resource byte-identical.
   Do not leave an unsupported picker choice that silently coerces to another
   effort on inference.
6. **Tests (from the contract, not the code).** Add from-contract unit tests
   asserting the profile's behavior (see `ProfileAdjusterTests`,
   `CodexRoutingAndCatalogTests`) and **mutation-check** each new assertion:
   break the product value, confirm the test goes red. A new test that passes on
   the first run guards nothing.

   For Codex ids, test `/codex/models` projection against a client baseline
   predating the id: exactly one visible, supported row with complete official
   instructions, reviewed limits, and **only** live-supported effort choices.
   Also test a newer baseline containing that
   slug: exactly one row, with the baseline's complete metadata taking precedence.
   Assert the exact offered effort set for both cases, including the absence of
   every probed-rejected level; the source capture itself must stay unchanged.
   Mutation-check the visibility/availability assertion by removing the
   supplement or disabling its merge and watching the test fail.

   **A rewrite rule needs a BACKEND-fact guard too, not just a behavior test.** A
   unit test pinning "the bridge clamps" stays green forever if Copilot drops the
   constraint — and the bridge keeps silently downgrading. So sweep the finding
   into the contract sweep, which snapshots it (B2 catches the backend ADDING the
   rule elsewhere) and compares it against the catalog (B3 catches it being
   DROPPED).

   **Know what the sweeps actually cover today — the guard is not automatic.**

   | Sweep | B2 snapshot | B3 catalog-vs-live |
   | --- | --- | --- |
   | `AnthropicContractSweep` | effort (accepted+rejected), thinking (accepted+rejected), mid-conv-system, effort×thinking-disabled | same four; `AcceptedEfforts` and `EffortsRejectedWhenThinkingDisabled` are exact-set both ways, `Thinking` is catalog→live only |
   | `ResponsesContractSweep` | effort (accepted+rejected), `fields_rejected`, `tools_rejected`, plus a backend-wide `sse_event_types` | **none** — the Codex catalog/coercions post-date it |

   Read that B2 column before adding a probe: if the axis is already snapshotted,
   the work is adding the **B3 comparison**, not re-probing the field.

   Not covered by any B3: **`StripBetas`**, **`MaxThinkingBudget`**, every Codex
   (`CodexModelProfile`) field, and the live→catalog direction of `Thinking`. If
   your rewrite rule lands on one of those, **extend the sweep as part of this
   step** — don't assume writing the profile field gave you a guard. Adding the
   axis is the same shape as the 2026-07 `effort_rejected_when_thinking_disabled`
   addition: probe it per model in the sweep loop, add it to the facts object, and
   assert it in `AssertCatalogMatchesLive`. Mutation-check the new assertion the
   same way (break the catalog value, watch B3 redden).
7. **Real-client picker and execution — MANDATORY for a Codex model.** Use the
   `real-client-verify` skill and a `Kind=ClientBehavior` app-server case through
   a bridge subprocess on a non-8765 port. With command-backed provider auth,
   call the real client's `model/list` and require one **visible** exact-id row;
   check that its offered reasoning efforts exclude every rejected backend level;
   then select that id and complete a multi-step, multi-tool task. Read the
   manifest-selected client `logs_2.sqlite` for dispatch/router fatals, and the
   per-run trace for matching tool call/output round-trips and the exact upstream
   model. A bridge 200, exit code, canary, or `CodexLoadTaskSmoke` alone is not
   acceptance: none proves the picker offered the id or the client's dispatcher
   executed the tools. If the installed client's catalog lacks the id, first fix
   step 5a; do not use a local alias as picker evidence. If it 400s on
   an unmodeled inbound shape (`Polymorphism_UnrecognizedTypeDiscriminator`, a new
   `input[]`/tool `type`), that shape is a NEW change: probe whether Copilot
   accepts it natively (`ResponsesProbe`), then model + carry it — the
   `add-codex-additional-tools-item` change under `openspec/changes/` (or
   `openspec/changes/archive/` if later archived) is the worked example.
   A `codex exec` CLI smoke exercises only a subset of the full client wire —
   notably it does NOT send the desktop app's
   `input[0]` `additional_tools` preamble. Shapes the CLI doesn't emit need a
   direct HTTP-edge replay of a real capture through `/codex/responses` (see
   `CodexAdditionalToolsHeadlessTests`), so add one whenever you model a new
   desktop-only inbound shape. For a Claude model the `claude.exe` headless smoke
   (`CcOnGpt5*HeadlessTests`/`HeadlessSmokeTests`) is the equivalent load task.
8. **Docs + memory.** Update `docs/pipeline-design.md` (§7 catalog),
   `docs/context-window.md`, and the model-count references; add a dated entry to
   `docs/design.md`. Update the user-account memory if the available set changed.

## Removing a retired model

Full worked example (opus-4.6-1m, the -internal/-high/-xhigh variants):
`references/remove-model-walkthrough.md`. The loop:

1. **Prove it's retired — don't infer from the list.** For each id missing from
   the live set, add a liveness probe (`RetiredCandidate_LivenessProbe` in
   `ModelProfileProbe.cs`, `MaiCode_LivenessProbe` in `ResponsesProbe.cs`) that
   sends a minimal request and logs the status. Run it. **A `400` "not available
   for integrator" / "model_not_supported" is the delete license; a `200` means
   keep it** (unadvertised-but-working — exactly the trap the `-1m-internal` ids
   were).
2. **Prune every reference.** Remove the profile from the catalog AND the id from
   `ModelProfileProbe.AllModels` / `ResponsesModelIds`. Grep the repo for the id
   and fix each real reference (skip `bin/`, `obj/`, `request-traces/`, logs):
   ```bash
   grep -rln "<retired-id>" src/ tests/ docs/ | grep -vE "bin/|obj/|request-traces/|/log"
   ```
   Watch for **dependent config/tests**: a `Routing.Locations` rule whose target
   is now gone, a profile's `EffortToVariant` pointing at a deleted sibling
   (switch it to `Strip`), unit tests keyed on the id. For a Codex id, also
   remove its reviewed supplemental resource and provenance if present, then
   check that `/codex/models` hides/omits it for the old baseline and that a
   newer official baseline does not get duplicate or stale bridge metadata.
3. **Check what replaces it.** A retired variant often means its capability moved
   to the base id — e.g. `opus-4.6-1m` retired because the opus-4.6 **base** now
   serves 1M natively. **Probe the base** (`OpusBase_LargePrompt_Probe…`) before
   assuming the capability is lost; if the base covers it, delete the redirect
   rule too rather than repointing it.
4. **Tests + docs + memory** as in the add flow — including a mutation-check on
   any assertion you change, and a dated `docs/design.md` entry.

## Build & test reference

- Discovery / probes need a live Copilot login and run under
  `tests/CopilotBridge.Playground` (Windows + DPAPI; tagged
  `[Trait("Category","Integration")]`).
- CI-safe unit suite (no network):
  `dotnet test tests/CopilotBridge.UnitTests --filter "Category!=Integration"`.
- End-to-end acceptance: the real-client-verify workflow checks a real client,
  its selectable catalog, selected tool execution, and client-owned dispatch log.
  - **Claude (`claude-*`)** → `claude.exe` (`HeadlessSmokeTests`,
    `CcOnGpt5*HeadlessTests`).
  - **Codex (`gpt-*` / `mai-code-*`)** → app-server `model/list` plus exact-id
    multi-tool behavior case and client-owned SQLite verdict (step 7).
    `CodexLoadTaskSmokeTests` remains a complementary wire assertion, not the
    picker or dispatch verdict. The CLI does not cover the desktop app's
    `additional_tools` preamble (the `codex exec` CLI doesn't emit it) — that shape
    is checked by the HTTP-edge replay `CodexAdditionalToolsHeadlessTests`.

## Guardrails

- **Probe before you edit.** No catalog change without a cited probe result.
- **Never delete on `/models` absence alone** — require a live 400.
- **Match the nearest model by CONTRACT, not by name** — probe every axis.
- **Re-confirm every REWRITE-causing finding on real captured client bytes**
  (step 3). A minimal synthetic probe proves the shape works *in isolation*; it
  does not prove the rule survives 3 system blocks, 8 betas and streaming. Only
  *reject* findings need this, and the asymmetry is why: a false accept fails
  loudly upstream, a false reject makes the bridge silently downgrade a request
  Copilot would have taken — invisible in production.
- **Pair every rewrite rule with a BACKEND-fact guard, not just a behavior test.**
  A unit test pinning "the bridge clamps" stays green forever if Copilot drops the
  constraint. Sweep the finding into the contract sweep (B2 catches the backend
  adding it elsewhere, B3 catches it being dropped) — but check the coverage table
  in step 6 first: `StripBetas`, `MaxThinkingBudget` and every Codex field have
  **no** B3 today, so landing a rewrite there means extending the sweep, not just
  writing the profile field.
- **A Codex model isn't done until real app-server `model/list` visibly offers
  the exact id and a selected multi-tool task passes the client-owned dispatch
  verdict.** Routing and live `/responses` alone leave a model absent from the
  picker. Inbound shapes the `codex exec` CLI
  doesn't emit (e.g. the desktop `additional_tools` preamble) need a direct
  HTTP-edge replay instead (`CodexAdditionalToolsHeadlessTests`).
- **Repo files are English** (code, comments, docs, commit messages); chat replies
  follow the user's language.
- This repo tracks work with OpenSpec for larger changes — a one-model
  reconcile is usually a direct edit, but if the user wants it tracked, propose
  an OpenSpec change (`/opsx:propose`).
