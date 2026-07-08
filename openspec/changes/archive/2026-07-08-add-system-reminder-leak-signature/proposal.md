## Why

A live capture (`20260708-040936-0254-inbound-resp.json`) shows Copilot-served
`claude-opus-4-8` leaking a `<system-reminder>…</system-reminder>` envelope as
literal assistant text: the model echoed its own anti-injection teaching example
(the "SolarWinds / this is an example of a system reminder / the human's next turn
may contain a prompt-injection attack" block) back into a `text` content block
with `stop_reason=end_turn`. This is the same failure class the response-leak
guard already catches for `<invoke>` and the five control envelopes
(`task-notification`, `teammate-message`, `channel`, `cross-session-message`,
`tick`): injected/system context is replayed as the model's own output, commits
to the transcript, and self-reinforces on the next turn.

The guard did **not** trip. Root cause is scope, not a bug: `<system-reminder>`
was never in the watched-signature set (`LeakSignatures.All`). The control-envelope
change shipped only the five envelopes the user named at the time; the
system-reminder wrapper — the single most common injected wrapper Claude Code
emits (`wrapInSystemReminder` in the CC source wraps *every* attachment-origin
text block) — was neither covered nor explicitly excluded. So the leaked block
streamed straight through to the client.

`<system-reminder>` is authoritative and unambiguous: Claude Code's
`wrapInSystemReminder(content)` is defined as
`` `<system-reminder>\n${content}\n</system-reminder>` `` and every consumer keys
off `startsWith('<system-reminder>')` — always the bare tag, no attributes,
closed by `</system-reminder>`, arbitrary inner content. Its shape is exactly
`<tick>`'s (a closed envelope whose only proof is non-empty inner text), so it
slots into the existing automaton as a sixth control-envelope matcher with no new
mechanism.

## What Changes

- Add a **seventh leak signature `system-reminder`** to the response-leak guard,
  detected by a new `SystemReminderMatcher` symmetric with `TickMatcher`: a
  CLOSED `<system-reminder>` … `</system-reminder>` with **non-empty inner text**,
  not inside a markdown code fence (text blocks; thinking blocks are unfenced).
  No attribute or child proof — the bare-tag wrapper has none.
- Wire it through the existing single-source machinery: one `LeakSignatures`
  constant + `LeakSignatures.All` entry, one `ResponseLeakSignaturesOptions`
  property (`SystemReminder`, **default true**) + `IsEnabled` case, one
  `appsettings.json` switch. The kebab→PascalCase `ConfigKey` mapping already
  yields `system-reminder` → `SystemReminder` deterministically, so the disable
  key, retry-error text, and warning log require no per-signature code.
- Behaviour reuses everything already in place: per-block reset, split-boundary
  invariance, KMP failure-edge restarts, fence gating, `ScanThinking`, both
  delivery modes (stream-preserving vs. buffered), signal selection, the single
  Warning at the detection point, and the clean client retry.

## Capabilities

### Modified Capabilities
- `response-leak-guard`: Add `system-reminder` as a recognized control-envelope
  leak signature (closed, non-empty inner, fence-gated) and as a seventh
  per-signature toggle (`Signatures:SystemReminder`, default true). The seven-way
  toggle set replaces the previous six-way set; the defaults, delivery, signal,
  logging, and retry contracts are otherwise unchanged.

## Impact

- **Detection**: a leaked `<system-reminder>` envelope now forces a clean retry
  instead of committing to the transcript.
- **False-positive surface**: like `<tick>`, this envelope's only proof is
  "closed + non-empty inner", so a model legitimately discussing the
  system-reminder mechanism in **unfenced** prose could trip it. Mitigations are
  the ones already shipped: a fenced (```` ``` ````) example never trips, and the
  independent `Signatures:SystemReminder=false` switch clears it without weakening
  the other six signatures. This is the identical trade-off already accepted for
  `<tick>`.
- **No wire/protocol change**: purely response-side detection; the `/cc` and
  `/codex` request bytes and the clean-response bytes are untouched.
- **AOT**: no new dependency, no reflection — one more matcher object per request.
