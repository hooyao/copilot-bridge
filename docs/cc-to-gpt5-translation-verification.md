# Verifying the Claude Code → gpt-5.5 translation (不多 / 不少 / 不映射错)

> How the T2 translation (`ResponsesRequestBuilder`, Anthropic IR → Copilot
> `/responses`) is proven to conserve content: nothing added, nothing lost,
> nothing mis-mapped. This is the method, the evidence, and the committed guards.

## Why this doc exists

Counting blocks (`216 tool_use → 216 function_call`) proves *quantity* is
preserved, not *content* or *semantics*. A translation that maps everything to a
consistently-wrong shape passes every count-based check. So the verification is
built on three independent techniques, each targeting one failure mode, and each
grounded in **real captured traffic** and **live gpt-5.5 behavior** rather than
re-reading the implementation.

## The three properties and how each is proven

### 不多 — nothing added / fabricated / duplicated

**Method:** whole-corpus content-conservation reconciliation. For every real
Claude Code request in the capture set, run the real T2, then INDEPENDENTLY
derive the expected ordered content inventory from the Anthropic IR (by the
mapping contract) and diff it against what T2 actually emitted. Any output token
with no source in the input is an `ADD`.

**Evidence:** 4629 real CC requests → **zero** ADD/duplication/fabrication.

**Committed guard:** `ContentConservationTests.Translation_ConservesContent`
(unit; runs on committed fixtures + `BRIDGE_TRACE_DIR` sample). The repeated
`<system-reminder>` text some requests contain is Claude Code's own conversation
accumulation — present in the INBOUND already, not introduced by T2 (the
reconciliation confirms input↔output parity, so it is not counted as an ADD).

### 不映射错 — nothing mis-mapped

**Method (content):** the same reconciliation compares content **byte-for-byte** —
message text verbatim, `tool_use.input` → `arguments` canonicalized-JSON-equal,
`tool_result.content` → `output` per the flatten contract, order preserved. A
mapped item whose content differs is a `MISMATCH`.

**Method (semantics):** for mappings whose correctness is a fact about gpt-5.5
(not about bytes), **live acceptance probes** — minimal isolating requests to real
gpt-5.5 with a secret the model can only echo if the content truly arrived:

| Mapping decision | Probe | Live result (2026-07-04, gpt-5.5) |
| --- | --- | --- |
| mid-conv `role:"system"` forwarded verbatim | secret in a system turn → recall | 200, secret recalled → **role + content correct** |
| parallel tool outputs delivered out of order | 2 calls, outputs reversed, ask for both | `alpha=ALPHAVAL beta=BETAVAL` → **associated by call_id, not order** |

**Evidence:** 4629 requests → **zero** MISMATCH/DESYNC; both semantic probes pass.

**Committed guards:** the MISMATCH check is in `ContentConservationTests`
(mutation-verified: corrupting one character of the text mapping makes it fail).
The semantic facts are asserted in `ResponsesProbe`
(`MidConvSystemRole_Accepted_AndContentDelivered`,
`ParallelToolOutputs_AssociatedByCallId_NotOrder` — Integration-tagged).

### 不少 — nothing lost

**Method:** the reconciliation flags every input block with no output
representation as a `DROP`. Whole-corpus, the ONLY content that leaves the wire is
the plain Anthropic `thinking` block. That drop is then justified by a live probe.

**Evidence & justification:**
- gpt-5.5 **hard-rejects** a `{type:"thinking"}` content part: 400 *"Invalid
  value: 'thinking'. Supported values are: input_text, input_image, output_text,
  refusal, input_file, computer_screenshot, summary_text, …"* — so forwarding it
  is impossible, the drop is **mandatory**.
- With thinking dropped, the assistant turn keeps its sibling text and the
  conversation stays **coherent** (probe: model answers correctly after the drop).
- `thinking` is model-internal scratch Anthropic itself never replays as visible
  content, so no user-facing content is lost.

T2 handles the drop **explicitly** (a `ThinkingBlockParam` case that skips), not
via a catch-all — so a future edit can't silently forward it and reintroduce the
400.

**Committed guards:** `ContentConservationTests` treats the thinking drop as the
single intended exception (any OTHER drop fails the test);
`ResponsesProbe.PlainThinkingContentPart_Rejected_JustifyingTheDrop` and
`ThinkingDropped_ConversationStaysCoherent` pin the live facts.

## Cross-field / structural constraints — a SEPARATE dimension

Content conservation (above) is per-item: it proves each block maps to the right
bytes. It does **not** catch a different failure class — **two individually-correct
decisions that combine into a request the Responses gateway rejects**. Example
(found in review): `tool_choice = {type:"tool", name:"X"}` is a correct mapping,
and dropping a server/IDE tool "X" is a correct drop, but *together* they emit a
`tool_choice` that names a tool absent from `tools[]` → 400.

These are cross-field invariants of the EMITTED body, checked by a separate
whole-corpus scan (the analyzer enumerates them; the survivors are pinned as
committed unit tests):

| # | Invariant | Status |
| --- | --- | --- |
| C1 | `tool_choice` forced-tool name ∈ emitted `tools[]` | **fixed** — `WriteIrToolChoice` downgrades a forced-but-dropped tool to `"auto"`; tests `ToolChoice_ForcedDroppedTool_DowngradesToAuto` / `_ForcedSurvivingTool_EmitsFunctionChoice` |
| C2 | no `tool_choice` when `tools[]` is absent/empty | **fixed earlier** — `ToolChoice_WithoutSurvivingTools_IsNotEmitted` |
| C3–C6 | tool_use↔tool_result pairing, no orphan/dup/out-of-order call_id | **holds** — T2 copies blocks in order; whole-corpus scan = 0 (and gpt-5.5 associates by call_id regardless of order, live-probed) |
| C7–C8 | no duplicate tool name; every tool has name+parameters | **holds** — corpus scan = 0 |
| C9–C10 | non-empty `input[]`; no empty `message.content[]` | **holds** — corpus scan = 0 |
| C11 | a thinking-only assistant turn vanishes cleanly (no empty message item) | **holds + pinned** — `ThinkingOnlyAssistantTurn_VanishesCleanly_NoEmptyMessageItem` (corpus never sends one, but the builder handles it) |
| C12 | no illegal adjacent-role sequence introduced | **holds** — corpus scan = 0; consecutive user turns are legal on gpt-5.5 (live-probed) |

**Whole-corpus cross-field scan: 4629 requests → 0 violations** (after the C1/C2
fixes). Note the only real `tool_choice={type:tool}` cases in the corpus force
`web_search` — an Anthropic server tool the `/cc` endpoint already 400-rejects
*before* T2 (`TryGetUnsupportedServerTool`), so C1 doesn't fire on them; the C1 fix
matters for a forced IDE-only tool, which is not endpoint-rejected.

## End-to-end

`CcOnGpt5HeadlessTests` (Integration) drives the real `claude.exe --model gpt-5.5`
through the bridge on a multi-tool task and asserts, per request: tools survive to
the wire, upstream 200, tool round-trip occurs, the canary reaches the final
answer, AND the client-facing SSE carries exactly one `message_start` (guards the
T3 double-terminal bug).

## Reproducing

- Offline conservation (fast, no network):
  `BRIDGE_TRACE_DIR=<captures> dotnet test tests/CopilotBridge.UnitTests --filter ContentConservationTests`
- Live mapping facts (needs Copilot):
  `dotnet test tests/CopilotBridge.Playground --filter "ResponsesProbe&(MidConvSystemRole|PlainThinking|ThinkingDropped|ParallelToolOutputs)"`
- End-to-end (needs Copilot + claude.exe):
  `dotnet test tests/CopilotBridge.Playground --filter CcOnGpt5HeadlessTests`

## What is NOT claimed

- **Content conservation is proven; cross-field coverage is enumerated, not
  exhaustive.** The content dimension (不多/不少/不映射错) is a whole-corpus
  byte-level proof. The cross-field dimension (C1–C12) is a *checklist* of
  structural invariants I could enumerate — it is not a proof that no other
  invalid combination exists. A "corpus scan = 0" means the shape doesn't occur in
  4629 real requests; it does NOT mean it's impossible (C1 itself fires on zero
  corpus requests yet was a real bug for a forced IDE-only tool). New invariants
  get added to the analyzer + a committed test as they're found.
- **Vision/image and document blocks** are present in the DTO but rare-to-absent
  in the CC→gpt-5.5 capture set; they are mapped by the same machinery but are not
  part of the byte-level corpus evidence above. If those become common on this
  path, extend the corpus and the reconciliation to cover them.
- **The corpus is a snapshot.** All whole-corpus claims are relative to the
  captured set at verification time; a Claude Code version that emits a new block
  shape or forces a new tool_choice pattern needs a re-scan.
