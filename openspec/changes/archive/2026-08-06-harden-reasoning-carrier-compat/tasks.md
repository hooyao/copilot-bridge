# Tasks — harden the reasoning carrier against version and origin skew

Three deferred findings from the PR #64 Codex adversarial review, all re-verified
against shipped code on `main` (`2c93b1a`). Ordered so the RELEASE SEQUENCING
constraint (§4) cannot be violated by accident. NOTE: §4 changed during
implementation — the planned two-release rollout was cancelled once measurement
showed it would have CREATED the hazard it was meant to avoid. See 4.2.

## 1. Ground the contract
- [x] 1.1 Re-confirm on `main` that the version is inside the prefix
  (`ClaudeReasoningEnvelope.cs:27`, `Prefix = "cbridge_rr_7f3a9d2c_v1:"`) and that
  `TryUnfold` matches it with `StartsWith` (line 78), so a `_v2:` or unknown-prefix
  carrier returns `Absent` and is forwarded upstream rather than failing closed.
- [x] 1.2 Write the falsifying test FIRST: a carrier with a bumped version reaches
  the outbound upstream request today. Assert on the upstream BYTES, not the
  status code — a 200 is exactly what makes this invisible.
- [x] 1.3 Freeze a v1-shaped carrier as a literal fixture, so the legacy read path
  is proven against a form the current encoder is not the author of. Guarded by
  `FrozenLegacyFixture_MatchesWhatThisBuildEmits`, which fails if the encoder's
  payload shape ever drifts away from the frozen literal — without that guard a
  hand-frozen fixture can silently stop representing any real carrier and the
  compatibility test passes vacuously.
- [x] 1.4 Confirm the envelope records no origin (`TryFold` writes `{v, item}`
  only) and that `ir.Model` / the resolved target are available at fold time —
  if they are not, that constraint shapes the whole of §3.
- [x] 1.5 Confirm the multimodal gate is boolean-exact
  (`ResponsesRequestBuilder.cs:70`), that `gpt-5.6-sol` is the only row with the
  flag (`CodexModelProfileCatalog.cs:156`), and that the downgrade path emits
  nothing — `ResponsesRequestBuilder` is a static class with no logger.

## 2. Version: family prefix + parsed version
- [x] 2.1 Split `Prefix` into a version-free family discriminator plus a version
  token parsed from the payload.
- [x] 2.2 Add the legacy `_v1:` READ path. Read-only — do not rewrite or migrate
  existing transcripts.
- [x] 2.3 Make an unknown/newer version reach the existing
  `InvalidClaudeReasoningEnvelopeException` → bounded 400, instead of `Absent`.
- [x] 2.4 **Keep emitting `_v1:` in this release.** The encoder switch is §4.2 and
  belongs to a LATER release; landing it here breaks rollback.
- [x] 2.5 Tests: unknown version fails closed and never reaches the upstream;
  the frozen v1 fixture from 1.3 still decodes; a non-carrier value is still
  untouched.

## 3. Origin binding
- [x] 3.1 Record backend vendor + canonical model id in the envelope at fold time.
- [x] 3.2 On unfold, compare recorded origin against the resolved target; on
  mismatch DROP the carrier and continue statelessly (never fail the request,
  never replay another model's state).
- [x] 3.3 Surface the drop as an observable downgrade in the audit record.
- [x] 3.4 **Decide exact-vs-family model matching before coding** (see
  `design.md`). Default to EXACT. Do not adopt family-level matching on
  reasoning alone — that is a claim about whether sibling models accept each
  other's encrypted state, and this project requires a live probe for that class
  of claim.
- [x] 3.5 Handle a carrier reaching an Anthropic-routed request: it must not be
  forwarded to that backend as provider-native content.
- [x] 3.6 Tests: same-origin replay unchanged (byte-identical); cross-model
  reroute drops and the turn still completes; Anthropic reroute does not forward.

## 4. Release sequencing — MEASURED, and the plan changed
- [x] 4.1 Verify a rollback from this build to v0.4.29-beta is safe. Done by
  compiling the exact shipped v0.4.29-beta decoder and running it against both
  cases, rather than reasoning about it:
  - `_v1:` prefix + this build's new `origin` field → **`Invalid`** (bounded 400,
    safe — its `HasOnlyKnownFields` accepts only `v`/`item`)
  - a NEW-prefix carrier → **`Absent`** (falls through and is FORWARDED UPSTREAM)
- [x] 4.2 **CANCELLED — do not switch the emitted prefix, in this release or a
  later one.** The premise was wrong. v0.4.29-beta ALREADY fails closed on a
  payload it cannot read; the fail-open case exists only for an unfamiliar
  PREFIX. Switching the prefix would have manufactured the very hazard the
  two-release rollout was invented to contain. Payload `v` carries version
  evolution instead, and `FamilyPrefix` stays read-only/reserved so a future
  build could adopt it without stranding today's readers. Pinned by
  `EmittedPrefixStaysV1_SoAnOlderReaderFailsClosed` and
  `ReservedFamilyPrefixIsReadable_SoAFutureSwitchIsSafe`.
- [x] 4.3 Record the rule in `docs/auto-update.md` — corrected from "reader before
  writer" to **"evolve the payload, never the discriminator"**, with the measured
  verdict table.

## 5. Three-valued capability
- [x] 5.1 Replace the boolean with Supported / Unsupported / Unknown; keep exact
  matching, never borrow a positive capability from a nearest profile.
- [x] 5.2 **Decide whether Unknown + image should FAIL or downgrade** (see
  `design.md`; recommendation: logged downgrade). Make this call explicitly — do
  not let it default to whichever is easier to implement.
- [x] 5.3 Carry the downgrade out of the static builder (mirror the existing
  `OutboundEffortCoerced` pattern on the response context) and into the audit
  summary. `Unsupported` stays quiet; only `Unknown` reports.
- [x] 5.4 Tests: unknown-model image downgrade is reported and the request still
  succeeds as text; a probed-unsupported model downgrades without the report;
  `gpt-5.6-sol` is unaffected.

## 6. Spec drift correction
- [x] 6.1 Rewrite the isolation paragraph in `cc-responses-reasoning-replay`: the
  shipped design has NO target gate — the codec lives in the Claude client
  adapters, runs unconditionally, and Codex isolation is structural (own adapter,
  never mints a carrier, never references the codec). The archived text still
  describes the reviewed-away vendor-gated stage.
- [x] 6.2 Keep `docs/pipeline-design.md` consistent with the new version/origin
  rules.

## 7. Verification
- [x] 7.1 Full unit suite green.
- [x] 7.2 Mutation-check every new test. NOTE: the PR #64 method (`git stash` the
  whole of `src/`) does NOT work here — the new tests reference new API surface
  (`FamilyPrefix`, `OriginMarker`, the reporting `Build` overload), so pre-fix code
  does not compile and the run fails for the wrong reason. Used the narrowest
  BEHAVIOUR-only mutation that keeps the API intact instead: unknown version →
  `Absent`, origin check → `if (false)`, downgrade report → `false`. Each reddened
  exactly the test asserting it (4 failures, 34 passes), including the byte-level
  upstream assertion.
- [x] 7.3 `ApiContract` replay pinning that an unknown-version carrier never
  reaches the upstream.
- [x] 7.4 Real-client (`Kind=ClientBehavior`) run. `cc-to-gpt-multitool` produced a
  real carrier and the real `claude.exe` echoed it back BYTE-IDENTICALLY; decoding
  the echoed value shows `v:1` and `origin:"gpt-5.6-sol"`, so the new field survives
  a live transcript round-trip. All 10 CC cases: `tool_use`→`tool_result` present,
  `is_error:false`, `terminal_reason:completed`; zero `bridge_*` marker leaks.
  CAVEAT: the cross-model REROUTE drop is covered by unit tests only — no scenario
  currently changes model mid-session, so the drop path has not been exercised by a
  real client. Worth a dedicated behavior case before relying on it in production.
- [x] 7.5 Behavior leg run on ONE build: bridge built 18:57:54, run started
  11:01:13Z, all 18 manifests inside that window, 16/16 passed in 13m14s. NOTE the
  post-run code edit (4.2 revision) touched comments plus the emitted-prefix pin;
  the emitted bytes are unchanged from what this run exercised (`_v1:` either way),
  so the run remains valid evidence for the carrier path.
- [x] 7.6 AOT publish: zero trim/AOT warnings, binary size recorded in
  `docs/size-history.md` (bridge `14,128,128` B, +16,896 B / +0.12%).
