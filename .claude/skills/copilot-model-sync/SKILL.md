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
   // Bodies land in tests/behavior-runs/<serve-dir>/*-upstream-req.json after any
   // Kind=ClientBehavior run (tracing is forced on). Take one, change one field,
   // POST it straight at Copilot via PlaygroundClient — bypassing the bridge.

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

   If no capture exists yet (a brand-new model with no traffic), run one
   `Kind=ClientBehavior` case first to produce one — that run is needed for step 7
   anyway.
4. **Write the `ModelProfile`** in `ModelProfileCatalog.BuildDefault()` (or a
   `CodexModelProfile` in `CodexModelProfileCatalog`) with every field grounded
   in a probe result, and a code comment citing the probe method name for each
   non-obvious field. Fields: `AcceptedEfforts`, `EffortOnUnsupported`,
   `Thinking` (`AdaptiveOnly` / `EnabledOnly` / `All`), `MaxThinkingBudget`,
   `AcceptsMidConversationSystem`, `StripBetas`.
5. **Routing check.** Vendor dispatch is prefix-only (`claude-*` → `/v1/messages`,
   gpt/mai-code in `ResponsesModelIds` → `/responses`), so a claude id needs no
   registry change. A new Codex id must be **added to `ResponsesModelIds`** in
   `CopilotModelRegistry` or it falls through to the OpenAI-chat branch. Add a
   `Routing.Locations` entry in `appsettings.json` only if the model needs a
   deliberate remap (e.g. a context-window alias like `gpt-5.5-1m`).
6. **Tests (from the contract, not the code).** Add from-contract unit tests
   asserting the profile's behavior (see `ProfileAdjusterTests`,
   `CodexRoutingAndCatalogTests`) and **mutation-check** each new assertion:
   break the product value, confirm the test goes red. A new test that passes on
   the first run guards nothing.

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
7. **Load-task smoke — MANDATORY for a Codex (`gpt-*` / `mai-code-*`) model.** A
   liveness/effort probe and a plain one-word turn do **not** exercise a real Codex
   client tool loop — multi-call `function_call`/`function_call_output` round-trips
   and reasoning echoes only appear when the real `codex.exe` runs an actual
   multi-step **task**. That loop (plus model routing) is what this smoke guards.
   So for every added Codex id, run the real-client load-task smoke against **that
   id**. The repo's default shell is PowerShell (set the env var inline, then run):
   ```powershell
   $env:CODEX_SMOKE_MODEL="<new-id>"; dotnet test tests/CopilotBridge.Playground `
     --filter "FullyQualifiedName~CodexLoadTaskSmoke" --logger "console;verbosity=detailed"
   ```
   (bash/CI equivalent: `CODEX_SMOKE_MODEL=<new-id> dotnet test … --filter "FullyQualifiedName~CodexLoadTaskSmoke"`.)
   It must exit 0 with the canary in stdout AND the bridge audit must show the
   model on the wire plus a real `function_call`/`function_call_output` round-trip
   (the test asserts all of these, so a prompt-echo can't pass it). If it 400s on
   an unmodeled inbound shape (`Polymorphism_UnrecognizedTypeDiscriminator`, a new
   `input[]`/tool `type`), that shape is a NEW change: probe whether Copilot
   accepts it natively (`ResponsesProbe`), then model + carry it — the
   `add-codex-additional-tools-item` change under `openspec/changes/` (or
   `openspec/changes/archive/` if later archived) is the worked example.
   Caveat: this smoke exercises only what the `codex exec` CLI emits, which is a
   subset of the full client wire — notably it does NOT send the desktop app's
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
   (switch it to `Strip`), unit tests keyed on the id.
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
- End-to-end sanity: the headless smoke drives a REAL client against the bridge
  with the new/changed model and asserts a 2xx reaches Copilot.
  - **Claude (`claude-*`)** → `claude.exe` (`HeadlessSmokeTests`,
    `CcOnGpt5*HeadlessTests`).
  - **Codex (`gpt-*` / `mai-code-*`)** → `codex.exe` load task
    (`CodexLoadTaskSmokeTests`, model via `CODEX_SMOKE_MODEL`). Exercises a real
    Codex client tool loop (multi-call `function_call`/`function_call_output`
    round-trips + model routing) — a probe or plain turn does not. Required for
    every added/reconciled Codex id (step 7). It does NOT cover the desktop app's
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
- **A Codex model isn't done until a real `codex.exe` load task passes on it.**
  Probes and plain turns don't exercise a real client tool loop; the load-task
  smoke (`CodexLoadTaskSmokeTests`, `CODEX_SMOKE_MODEL=<id>`) catches tool-loop and
  routing regressions the CLI actually drives. Inbound shapes the `codex exec` CLI
  doesn't emit (e.g. the desktop `additional_tools` preamble) need a direct
  HTTP-edge replay instead (`CodexAdditionalToolsHeadlessTests`).
- **Repo files are English** (code, comments, docs, commit messages); chat replies
  follow the user's language.
- This repo tracks work with OpenSpec for larger changes — a one-model
  reconcile is usually a direct edit, but if the user wants it tracked, propose
  an OpenSpec change (`/opsx:propose`).
