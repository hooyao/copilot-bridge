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
truth is always a live probe (`tests/CopilotBridge.Playground/ModelProfileProbe.cs`
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
3. **Write the `ModelProfile`** in `ModelProfileCatalog.BuildDefault()` (or a
   `CodexModelProfile` in `CodexModelProfileCatalog`) with every field grounded
   in a probe result, and a code comment citing the probe method name for each
   non-obvious field. Fields: `AcceptedEfforts`, `EffortOnUnsupported`,
   `Thinking` (`AdaptiveOnly` / `EnabledOnly` / `All`), `MaxThinkingBudget`,
   `AcceptsMidConversationSystem`, `StripBetas`.
4. **Routing check.** Vendor dispatch is prefix-only (`claude-*` → `/v1/messages`,
   gpt/mai-code in `ResponsesModelIds` → `/responses`), so a claude id needs no
   registry change. A new Codex id must be **added to `ResponsesModelIds`** in
   `CopilotModelRegistry` or it falls through to the OpenAI-chat branch. Add a
   `Routing.Locations` entry in `appsettings.json` only if the model needs a
   deliberate remap (e.g. a context-window alias like `gpt-5.5-1m`).
5. **Tests (from the contract, not the code).** Add from-contract unit tests
   asserting the profile's behavior (see `ProfileAdjusterTests`,
   `CodexRoutingAndCatalogTests`) and **mutation-check** each new assertion:
   break the product value, confirm the test goes red. A new test that passes on
   the first run guards nothing.
6. **Load-task smoke — MANDATORY for a Codex (`gpt-*` / `mai-code-*`) model.** A
   liveness/effort probe and a plain one-word turn do **not** exercise the Codex
   client's full inbound wire shape — the harness tool-registration preamble
   (`input[0]` `additional_tools`), multi-call `function_call`/
   `function_call_output` round-trips, reasoning echoes. Those only appear when
   the real `codex.exe` runs an actual multi-step **task**. Skipping this is
   exactly how the gpt-5.6 `additional_tools` item shipped a silent 400 (the model
   was probed and added; no load task ever drove the preamble). So for every added
   Codex id, run the real-client load-task smoke against **that id**:
   ```bash
   CODEX_SMOKE_MODEL=<new-id> dotnet test tests/CopilotBridge.Playground \
     --filter "FullyQualifiedName~CodexLoadTaskSmoke" --logger "console;verbosity=detailed"
   ```
   It must exit 0 with the canary in stdout. If it 400s on an unmodeled inbound
   shape (`Polymorphism_UnrecognizedTypeDiscriminator`, a new `input[]`/tool
   `type`), that shape is a NEW change: probe whether Copilot accepts it natively
   (`ResponsesProbe`), then model + carry it (see
   `openspec/changes/archive/*add-codex-additional-tools-item*`). For a Claude
   model the `claude.exe` headless smoke (step 6's Anthropic analogue below /
   `CcOnGpt5*HeadlessTests`) is the equivalent load task.
7. **Docs + memory.** Update `docs/pipeline-design.md` (§7 catalog),
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
    (`CodexLoadTaskSmokeTests`, model via `CODEX_SMOKE_MODEL`). This is the ONLY
    check that exercises the Codex client's full inbound wire shape (the
    `additional_tools` harness preamble, multi-call tool round-trips) — a probe or
    plain turn does not. Required for every added/reconciled Codex id (add step 6).

## Guardrails

- **Probe before you edit.** No catalog change without a cited probe result.
- **Never delete on `/models` absence alone** — require a live 400.
- **Match the nearest model by CONTRACT, not by name** — probe every axis.
- **A Codex model isn't done until a real `codex.exe` load task passes on it.**
  Probes and plain turns miss the client's full inbound wire shape; the load-task
  smoke (`CodexLoadTaskSmokeTests`, `CODEX_SMOKE_MODEL=<id>`) is what catches a new
  `input[]`/tool `type` the bridge doesn't model yet.
- **Repo files are English** (code, comments, docs, commit messages); chat replies
  follow the user's language.
- This repo tracks work with OpenSpec for larger changes — a one-model
  reconcile is usually a direct edit, but if the user wants it tracked, propose
  an OpenSpec change (`/opsx:propose`).
