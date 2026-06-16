# Copilot contract drift detection

Copilot is a moving target. Its `/v1/messages` (Anthropic) and `/responses`
(Codex) backends change what they accept over time — effort tiers widen, a
field that 400'd starts 200'ing, an SSE event gets renamed. Before this
machinery, both wire probes were print-only, so drift was caught by luck (the
2026-06-05 "Copilot widened opus effort" episode was a *manual* re-probe, not a
test). Now each backend has a committed **contract snapshot** and an asserting
sweep that **goes red when the live backend deviates from it**.

This is the "B" (live contract) half of the test philosophy in
[`ir-definition-design.md`](ir-definition-design.md) §7.0/§7.B. The "A"
(offline invariant) half lives in `tests/CopilotBridge.UnitTests/Invariant/`.

## The two snapshots

| File | Backend | Produced by | Guards |
| --- | --- | --- | --- |
| `docs/copilot-anthropic-contract-snapshot.json` | Copilot `/v1/messages` | `ModelProfileProbe.B_AnthropicContract_SweepAssertAndDetectDrift` | `ModelProfileCatalog` (via B3, same test) |
| `docs/copilot-responses-contract-snapshot.json` | Copilot `/responses` | `ResponsesProbe.B_ResponsesContract_SweepAssertAndDetectDrift` | the Codex profile catalog + coercions (built in change 3 `add-codex-responses-client`) |

Each snapshot records **only stable, decision-relevant facts** — per-model
effort accept/reject sets, thinking-shape acceptance, mid-conversation-system
acceptance, field/tool rejections, the SSE event-type set. It deliberately omits
volatile data (message ids, token counts, latencies) so the diff compares the
*contract*, not the response.

## Running the drift check

Both sweeps are `[Trait("Category","Integration")]` — skipped in CI (no Copilot
creds), run on demand or on a schedule when you suspect upstream drift:

```pwsh
$env:CLAUDE_EXE = "C:\path\to\claude.exe"   # for AuthService token resolution
dotnet test tests/CopilotBridge.Playground/CopilotBridge.Playground.csproj `
  --filter "FullyQualifiedName~B_AnthropicContract_SweepAssertAndDetectDrift"
dotnet test tests/CopilotBridge.Playground/CopilotBridge.Playground.csproj `
  --filter "FullyQualifiedName~B_ResponsesContract_SweepAssertAndDetectDrift"
```

Each sweep makes ~90–100 sequential live calls (a single network hiccup is
absorbed by `ProbeRetry` — transport errors retry, HTTP 4xx/5xx do not, since a
status code is a contract fact). A green sweep means the snapshot still matches
Copilot. A green offline suite with a *stale* snapshot is the **expected** state
between drift checks — B2 going red is the prompt to reconcile.

## The workflow when drift is detected

A red B2 is the **signal**, not a failure to suppress. The response is always:

1. **Read the diff.** The test prints exactly what moved, e.g.
   `ADDED models.gpt-5.5.effort.rejected[] (live lists "minimal"; snapshot did not)`
   or `CHANGED models.claude-opus-4.8.mid_conv_system (snapshot=true → live=false)`.
2. **Decide if it's real.** Re-run once (drift should reproduce; a one-off that
   vanishes was a transient the retry should have caught — investigate if not).
3. **Update the snapshot** — one command, then review the git diff:
   ```pwsh
   $env:BRIDGE_REGEN_CONTRACT_SNAPSHOT = "1"
   dotnet test ... --filter "FullyQualifiedName~B_<Anthropic|Responses>Contract_..."
   $env:BRIDGE_REGEN_CONTRACT_SNAPSHOT = $null
   ```
4. **Reconcile the dependent wire-truth.** The snapshot is the *evidence*; the
   code that bakes in that truth must follow:
   - Anthropic drift → reconcile `ModelProfileCatalog` (B3 in the same test
     fails and names the row, e.g. "opus-4.8 catalog AcceptedEfforts=[…] but
     live accepts=[…]").
   - Responses drift → reconcile the Codex profile catalog / coercions in
     `add-codex-responses-client` (change 3). Until that change lands, the
     Responses snapshot simply *records* the truth for change 3 to build
     against — there is no Responses-side catalog to fail yet, hence no B3 there.
5. **Commit** the updated snapshot + any catalog/coercion change together, so the
   evidence and the code move in lockstep.

## Why no allow-list (unlike the A-invariant harness)

The offline field-diff harness (`tests/.../Invariant/FieldDiffHarness.cs`) has an
allow-list of *our own* deterministic transforms, because it asserts that our
translators are self-consistent. This contract diff is the opposite: the snapshot
**is** the contract, so *any* difference is drift by definition. The only thing
ignored is the volatile `_meta` stamp (capture date / account), which is not a
contract fact. There is no tolerance — that's the point.
