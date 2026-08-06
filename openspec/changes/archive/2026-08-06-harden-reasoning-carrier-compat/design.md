# Design — hardening the reasoning carrier for version and origin skew

## The problem this change actually solves

PR #64 treated the envelope as an encoding detail. It is not: once folded into
`redacted_thinking.data`, it becomes **durable client data**. Claude Code
persists transcripts, replays them on `--resume`, and carries them through
compaction. So the envelope is a wire protocol between bridge versions that
happen to be separated by time rather than by a network — with no negotiation
step, and a peer that may be OLDER than the writer.

Every decision below follows from that single fact.

## Decision 1 — read a version-free prefix, but keep EMITTING `_v1:`

**Chosen:** recognise `cbridge_rr_7f3a9d2c:` + a parsed version on READ; continue
emitting `cbridge_rr_7f3a9d2c_v1:`.

The initial reasoning was that `..._v1:` conflates *is this ours?* with *can I read
it?*, so an unknown version looks the same as "not ours" and gets forwarded
upstream. That is true of a HYPOTHETICAL future prefix — but measurement (see
"Rollout sequencing" below) showed it is NOT true of the payload: the shipped
v0.4.29-beta reader already returns `Invalid` for an unknown `v` or an unknown
top-level field. The conflation only bites if someone changes the prefix.

So the fix splits: take the read support (free, and it unblocks any future switch),
decline the emit switch (the only thing that could strand an older reader).

Alternatives rejected:
- *Switch the emitted prefix now.* Would create the exact fail-open case this change
  exists to remove, for every user who rolls back.
- *Probe-parse every `redacted_thinking` value.* Breaks the guarantee that
  provider-native blobs are never parsed as bridge JSON.

**Legacy read.** `_v1:` must stay decodable — transcripts already exist in the
wild as of v0.4.29-beta. Read-only; no existing transcript is rewritten.
## Decision 2 — bind origin, and DROP on mismatch

**Chosen:** record `{backend vendor, canonical model id}`; on mismatch, drop the
carrier and continue statelessly, logged.

Drop rather than reject because PR #64 established the asymmetry empirically:
a **missing** reasoning item costs one turn's hidden state, while a **wrong** one
is either an upstream 400 or, worse, silently accepted nonsense. That work also
proved the client tolerates a turn with no reasoning block — that is precisely
the "unreplayable ⇒ omit" path already shipped.

Rejected: failing the request. A user who switches models mid-session did nothing
wrong; a hard error would make routing changes feel broken.

**Open question for review:** should the model id match be exact, or family-level
(`gpt-5.6-sol` ↔ `gpt-5.6-luna`)? Exact is safer and simpler; family-level keeps
more state alive across sibling routing. Recommend starting exact — a dropped
carrier is cheap, and we have no probe evidence that sibling models accept each
other's encrypted state. **We must not guess this**; if family-level is wanted, it
needs a live probe, in the same spirit as the `SupportsMultimodalFunctionOutput`
row.

## Decision 3 — Supported / Unsupported / Unknown

**Chosen:** three-valued capability; `Unknown` + image ⇒ **logged downgrade**,
not a failure.

The boolean is not wrong about `Supported`; it is wrong about the other side.
Today `false` means both "we probed it and it cannot" and "we have never heard of
this model", and only the second one deserves an operator-visible signal.

`ResponsesRequestBuilder` is a static class with no logger, so this needs a
carried-out signal (the same shape as the existing `OutboundEffortCoerced` on the
response context) rather than an inline log call. Routing that into the audit
summary is what makes a rename **observable** instead of inferable.

**Open question for review:** should `Unknown` + image FAIL instead? The codex
review recommended an actionable compatibility error. Against: it converts a
model rename — a Copilot-side event we do not control — into a hard outage for
every image-bearing turn, when the text fallback still produces a usable (if
degraded) answer. For: silently dropping an image can produce a confidently wrong
answer, which is worse than an error. **Recommend logged downgrade + audit
field**, and revisit if we ever see it fire in practice. This is a judgement call
that should be made explicitly, not inherited from whichever is easier to code.

## Rollout sequencing — measured, and the plan was wrong

The original plan here was a two-release rollout: a reader release that recognises
a new version-free prefix, then a writer release that emits it. The stated fear was
that a user who rolls back meets an unrecognised carrier, gets `Absent`, and the
value is forwarded upstream — the exact bug this change fixes.

That fear was never measured. When it was — by compiling the exact shipped
v0.4.29-beta decoder and running it — the premise collapsed:

| carrier replayed into v0.4.29-beta | verdict | outcome |
| --- | --- | --- |
| `_v1:` prefix, unknown payload `v` | `Invalid` | 400, safe |
| `_v1:` prefix, new `origin` field | `Invalid` | 400, safe |
| new version-free prefix | `Absent` | **forwarded upstream** |

The shipped reader already fails closed on a payload it cannot handle: its
`HasOnlyKnownFields` accepts only `v`/`item`, so even adding `origin` trips it.
The fail-open case exists ONLY for an unfamiliar prefix — which is to say, the
two-release rollout existed to contain a hazard that only the prefix switch would
have created.

**Decision: never switch the emitted prefix.** Version evolution rides the payload's
`v`, where every shipped reader already behaves correctly. `FamilyPrefix` is kept as
a READ-only reserved form, so if a future need for it ever appears, today's builds
already decode it and the switch could be made without stranding them. Reading costs
nothing; emitting is the risk.

The general rule this generalises to — evolve the payload, never the discriminator —
is recorded in `docs/auto-update.md`, since it applies to anything the bridge
persists into client-visible state.

## Testing strategy

Contract-first, per the project directive — and note that the highest-value tests
here cannot be written against a single build:

- **Cross-version:** a carrier bearing an unknown-but-well-formed version must
  produce the bounded 400, NOT reach the upstream. Assert on the upstream request
  bytes, not just the status.
- **Rollback:** the legacy `_v1:` reader path decodes a carrier minted by the
  shipped v0.4.29-beta encoder. Pin a real captured carrier as a fixture so this
  cannot drift with the encoder.
- **Reroute:** a carrier minted for model A, replayed on a request routed to
  model B, is dropped — and the turn still completes.
- **Unknown model + image:** the downgrade is logged and surfaced in the audit
  summary; the request still succeeds as text.
- **Mutation-check each one.** NOTE: the PR #64 method (`git stash` the whole fix)
  does not work here — the new tests reference new API surface, so pre-fix code does
  not compile and the run fails for the wrong reason. Use the narrowest BEHAVIOUR-only
  mutation that keeps the API intact instead, and confirm each one reddens exactly the
  test that asserts it.
Real-client acceptance: a live run must show a real carrier surviving a real
`claude.exe` transcript round-trip byte-identically, with the new `origin` field
intact after decode. The cross-model REROUTE drop is the one leg no current
scenario exercises — no behavior case changes model mid-session — so it rests on
unit coverage until a dedicated case exists. That gap should be stated, not
papered over with the passing runs of neighbouring cases.
