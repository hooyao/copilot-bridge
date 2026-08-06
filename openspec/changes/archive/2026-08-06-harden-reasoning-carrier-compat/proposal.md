## Why

PR #64 (v0.4.29-beta) shipped the reasoning-replay carrier and the multimodal
tool-result path. A Codex adversarial review of that branch raised five design
objections; three were fixed in-PR, and **three remain** — deferred at the time
because each looked like it needed a rollout strategy rather than a code edit. All
three were re-verified against the shipped code on `main` (`2c93b1a`), and one of
them turned out to need far less than the deferral assumed (see #1).

They share one shape: **a state the bridge persists into a CLIENT transcript, or
a capability it silently withdraws, has no way to say "I cannot handle this".**
Each fails quietly, a turn or a release away from its cause.

### 1. An unfamiliar carrier prefix fails OPEN

`ClaudeReasoningEnvelope.Prefix` is `cbridge_rr_7f3a9d2c_v1:` and `TryUnfold`
matches it with `StartsWith` (`ClaudeReasoningEnvelope.cs:78`). Anything that does
not match returns `Absent`, which means "ordinary provider data — forward it".

Measured, not assumed (see `design.md`): the shipped v0.4.29-beta decoder DOES fail
closed on a payload it cannot read — an unknown `v`, or an unknown top-level field,
both yield `Invalid` and a bounded 400. What it cannot survive is an unfamiliar
PREFIX: that returns `Absent` and the value goes upstream as if it were
provider-native encrypted content, with a 200 and no error anywhere.

So the real exposure is narrower than it first looked, and it is forward-looking:
any future build that changes the discriminator strands every reader shipped before
it. Claude Code persists transcripts and replays them across `--resume` and
compaction, and self-update makes rollback routine, so a transcript written by one
build WILL be read by another.
### 2. Persisted reasoning state is not bound to its origin

The envelope records `{v, item}` and nothing about **where the state came from**
(`ClaudeReasoningEnvelope.TryFold`). It is minted from a `gpt-5.6-sol` turn and
carries no backend, model, or compatibility epoch.

Two failure modes, both invisible until the replay turn:
- The session re-routes (config change, model switch, routing rule edit) and the
  bridge replays one Responses model's encrypted state into a **different** one.
- The session re-routes to an **Anthropic** backend, where the carrier is not
  decoded at all and reaches Copilot as an opaque blob that is not its own.

Version validation cannot detect either — the version is correct in both cases.

### 3. A model rename silently converts image output into text

`ResponsesRequestBuilder.cs:70` gates structured multimodal output on an
**exact** catalog hit (`exactProfile?.SupportsMultimodalFunctionOutput == true`),
while every other rule falls back to `GetNearest`. The exactness is correct — a
positive wire capability must never be borrowed. The **silence** is not.

`gpt-5.6-sol` is the only row with the flag (`CodexModelProfileCatalog.cs:156`).
If Copilot renames it, an image-bearing tool result is flattened to base64 text
with `Vision=false`; the request still returns **200** and the model simply never
sees the image. `ResponsesRequestBuilder` is a static class with no logger, so
the downgrade path emits nothing at all — the only signal is the router's generic
fuzzy-profile note, which says nothing about images.

A boolean cannot distinguish "probed and unsupported" from "not yet catalogued",
and those two deserve opposite handling.

### Also: the archived spec drifted from what shipped

`openspec/specs/cc-responses-reasoning-replay/spec.md` still says the envelope is
recognized "only on an explicitly identified Claude Code→**Responses** request
path". The final implementation (after Copilot review round 2) **removed the
target gate entirely**: the fold/unfold pair lives in the Claude client adapters,
runs with no vendor or route condition, and isolation from the native Codex edge
is structural — that edge has its own adapter, never mints a carrier, and never
references the codec (pinned by `NativeCodexEdge_CannotSeeTheCarrierCodec`). The
spec text describes a design that was reviewed away. Correcting it is in scope
here because requirement 1 rewrites the same paragraph.

## What Changes

- **Read a version-free discriminator; keep EMITTING `_v1:`.**
  `cbridge_rr_7f3a9d2c:` + a parsed version is now recognised on READ, so a future
  build could adopt it without stranding today's readers. The emitted prefix does
  NOT change: switching it is the one move that makes an older reader fail open, so
  version evolution rides the payload's `v`, where every shipped reader already
  fails closed. The `_v1:` form stays fully decodable; no transcript is rewritten.

- **Bind the envelope to its origin.** Record the originating backend vendor and
  canonical model id in the envelope. On replay, a mismatch is a bounded,
  bridge-owned downgrade: drop the carrier and continue statelessly rather than
  replay one model's encrypted state into another. Dropping is safe precisely
  because PR #64 established that a missing reasoning item is recoverable while a
  wrong one is not.

- **Make capability three-valued.** Replace the boolean gate with
  Supported / Unsupported / Unknown. Exact matching stays. `Unknown` (a model not
  in the catalog) with an image present becomes an explicit, logged downgrade —
  and the audit summary records it — so a rename surfaces as a visible event
  rather than a silently text-only turn. Whether `Unknown` should instead FAIL the
  request was decided explicitly rather than by convenience — logged downgrade,
  reasoning in `design.md`.

- **Correct the archived spec** to describe the shipped edge-codec design (no
  target gate, structural Codex isolation).

Behavior deltas, all of them corrections: a carrier bearing the reserved
version-free prefix with an unreadable version now 400s instead of being forwarded
upstream; a re-routed session drops stale reasoning
state instead of replaying it into the wrong model; an uncatalogued model with an
image logs a downgrade instead of going quiet. No change to the happy path — a
current-version, same-origin carrier on a catalogued model is byte-identical.

## Impact

- Affected specs: `cc-responses-reasoning-replay` (MODIFIED — version handling,
  origin binding, and the drifted isolation paragraph),
  `cc-responses-multimodal-tool-results` (MODIFIED — three-valued capability).
- Affected code: `Pipeline/Adapters/ClaudeCode/ClaudeReasoningEnvelope.cs`,
  `Pipeline/Adapters/ClaudeCode/ClaudeCodeInboundAdapter.cs`,
  `Pipeline/Strategies/Codex/ResponsesRequestBuilder.cs`,
  `Pipeline/Routing/CodexModelProfile.cs`, `Endpoints/ClaudeCode/RequestSummary.cs`.
- Tests: additions to `ResponsesReasoningReplayTests` (cross-version, rollback,
  cross-model reroute), `CodexImageTests` (unknown-model downgrade); a new
  `ApiContract` replay pinning that an unknown-version carrier never reaches the
  upstream.
- **No staged rollout needed** (this was the plan and it was cancelled): payload
  evolution is downgrade-safe on every shipped reader, so this ships as one release.
  The general rule — evolve the payload, never the discriminator — is recorded in
  `docs/auto-update.md`.
- **Known gap:** the cross-model reroute drop is covered by unit tests only. No
  behavior scenario changes model mid-session, so a real client has not exercised
  that path; a dedicated case is worth adding before leaning on it.
