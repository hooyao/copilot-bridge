## Context

The response-leak guard (`ResponseLeakAutomaton` + `ResponseLeakDetector`)
already detects a leaked `<invoke>` tool call and five Claude Code control
envelopes. Adding `<system-reminder>` is a scope extension, not a new mechanism:
it reuses the automaton's shared fence tracking, tripped latch, matched-subject
plumbing, per-block reset, and single-source signature wiring.

### Grounding in the Claude Code source (authoritative shape)

`Q:\MyProjects\claude-code-sourcemap\restored-src\src\utils\messages.ts`:

```ts
export function wrapInSystemReminder(content: string): string {
  return `<system-reminder>\n${content}\n</system-reminder>`
}
```

Every consumer classifies via `content.startsWith('<system-reminder>')`
(messages.ts:1800, 1808, 1849, 2502). Conclusions that fix the matcher shape:

- The wrapper is **always the bare tag** `<system-reminder>` — no attributes,
  ever. (Contrast the attribute-bearing envelopes `teammate-message` / `channel`
  / `cross-session-message`.)
- It is **closed** by `</system-reminder>`.
- Inner content is **arbitrary and non-empty** in every real emission (it wraps a
  real attachment/system block).

This is structurally identical to `<tick>…</tick>`: a fixed open tag, a fixed
close tag, and "non-empty inner text" as the sole proof. The existing
`TickMatcher` is therefore the exact template.

## Goals / Non-Goals

**Goals**
- Detect a closed, non-empty, unfenced `<system-reminder>` envelope leaked as
  assistant text and force the existing clean retry.
- Make it a seventh independent per-signature toggle, default on.
- Zero change to the six existing signatures' behaviour, the delivery modes, the
  signal mapping, the logging contract, and the request-side wire.

**Non-Goals**
- No fingerprint/history matching against the actual injected reminder text. The
  leaked example in the capture is a *generic* teaching reminder that need not
  appear verbatim in request history; shape-based closed-envelope detection is
  the safer floor (same decision recorded for the control envelopes).
- No new mechanism for empty/attribute variants — the CC source proves there are
  none.
- No change to `MaxScanChars` / accumulation semantics (the automaton is
  character-fed and window-free already).

## Decisions

### D1 — Shape: closed + non-empty inner, no proof child/attribute

`SystemReminderMatcher` mirrors `TickMatcher` exactly:
- KMP match `<system-reminder>` to open; reset the close matcher and an
  inner-length counter.
- KMP match `</system-reminder>` to close; trip **iff** at least one inner
  character was seen between open and close.
- Bounded counter (capped at close-tag length + 1) so an empty
  `<system-reminder></system-reminder>` does not trip and nothing grows unbounded.

Rejected: requiring a specific inner marker (e.g. the word "reminder" or a
newline). The CC wrapper guarantees `\n` padding, but keying on that would be
implementation-brittle and would miss a model that drops the whitespace while
still leaking the envelope. "Closed + non-empty" is the honest floor.

### D2 — Matcher subject and signature id both `system-reminder`

Like the other control envelopes (and unlike `<invoke>`, whose subject is the
captured tool name), the subject *is* the signature id. `Subject => "system-reminder"`,
`Signature => "system-reminder"`. This drives the log line and the disable-key
text with no special-casing.

### D3 — Single-source wiring, no parallel lists

The signature is added in exactly three declaration sites, each guarded by an
existing drift test:
1. `LeakSignatures.SystemReminder` constant + append to `LeakSignatures.All`.
2. `ResponseLeakSignaturesOptions.SystemReminder` (default `true`) + one
   `IsEnabled` switch case.
3. `ResponseLeakAutomaton` constructor: one `AddIfEnabled(LeakSignatures.SystemReminder,
   () => new SystemReminderMatcher())` line.
4. `appsettings.json`: `Signatures:SystemReminder: true` + comment mention.

`ResponseDetectionError.ConfigKey` already maps `system-reminder` → `SystemReminder`
by its deterministic split-on-`-`+capitalize, so the config path
`Pipeline:Detectors:ResponseLeakGuard:Signatures:SystemReminder` and the
retry/log text are produced with no lookup-table edit. The `LeakSignatureWiringTests`
suite fails loudly if any of the three lists drifts (id with no `IsEnabled` case,
id with no matcher, matcher with no id).

### D4 — Default on, escape hatch documented

Default `true` per the user's instruction. The elevated false-positive surface
(no proof beyond closed+non-empty, same as `<tick>`) is mitigated by (a) fence
gating — a ```` ``` ````-wrapped example never trips — and (b) the independent
`Signatures:SystemReminder=false` switch, whose exact path is surfaced in both the
retry error and the Warning log. This is the identical, already-accepted `<tick>`
trade-off; no new policy.

## Risks / Trade-offs

- **False positives on meta-discussion.** A user asking the model to explain the
  system-reminder mechanism, whose unfenced reply reproduces a full closed
  envelope, would trip. Accepted: it is rare, fence-suppressible, and
  self-service to disable. Documented in the option XML comment and README.
- **Overlap with the request-side `SystemSanitizeStage`.** That stage sanitizes
  an *inbound* `<system-reminder>` the bridge itself injects; this guard is
  *response-side* and independent. No interaction — different direction, different
  code path. Noted so a future reader does not conflate them.

## Migration / Rollout

Additive and default-on. No config migration: an existing `appsettings.json`
without a `Signatures:SystemReminder` key gets the `true` default (an absent
`Signatures` sub-block already enables every signature). Operators who hit a
false positive set the one switch and restart, exactly as for the other six.

## Open Questions

None. The shape is pinned by the CC source; the wiring pattern is established.
