## Context

The guard is a scoped `ToolLeakDetector` that owns one `ResponseLeakAutomaton`. The
automaton dispatches each streamed character to a set of KMP-based `ILeakMatcher`s
(one `<invoke>` matcher + five control-envelope matchers) and reports the FIRST
matcher to close a shape-valid signature, naming the leaked subject. Options come
from `IOptionsSnapshot<ToolLeakGuardOptions>.Value`, read once per request scope.

We need three things: (1) per-signature enable switches; (2) an informative error
+ log that let a user clear a false positive themselves; (3) a design that keeps
today's restart-required semantics but makes future hot-reload a one-seam change.

## Goals / Non-Goals

- Goal: each of the six signatures can be independently disabled; a disabled one is
  not evaluated and passes through; enabled ones are unaffected.
- Goal: the retry error AND the `Warning` name the tripped signature, the exact
  disable key, and the restart requirement.
- Goal: enabled-set is derived per request so hot-reload is later a one-line seam.
- Non-Goal: implementing hot-reload now (switches remain effectively fixed until
  restart because the JSON provider uses `reloadOnChange: false`).

## Decisions

### Config shape
A nested `Signatures` object under `Pipeline:Detectors:ToolLeakGuard` with six
PascalCase booleans (`Invoke`, `TaskNotification`, `TeammateMessage`, `Channel`,
`CrossSessionMessage`, `Tick`), all default true. Bound AOT-safely by the
configuration binding source generator (public parameterless ctor + settable
bools). Absent block == all enabled, so existing configs are unaffected.

The signature ids themselves live in one place (`LeakSignatures`) as kebab-case
constants (`invoke`, `task-notification`, …). The PascalCase config key is derived
from the kebab id (`ToolLeakError.ConfigKey`), so the ids remain the single source
of truth and the disable key printed to the user is always the real binding path.

### Gating by not constructing the matcher
The automaton constructor takes an optional `IReadOnlySet<string>? enabledSignatures`
(null = all enabled) and adds a matcher only when its signature is enabled. Not
constructing a disabled matcher is both zero-cost and semantically important: the
automaton reports the FIRST matcher to match on a character, so a disabled matcher
left in the list could match first and mask an enabled sibling. Omitting it is the
clean gate.

### Per-request enabled set (hot-reload seam)
`ToolLeakDetector` computes `BuildEnabledSignatures(_opts.Signatures)` in
`Begin(ctx)` — once per request scope, no static cache — from an
`IOptionsSnapshot<ToolLeakGuardOptions>` that re-binds per scope. Restart is
required today only because the JSON configuration provider is registered with
`reloadOnChange: false` (the project's edit+restart convention). The future
hot-reload change is a single line in `BridgeConfigurationExtensions` — flip that
flag to `true` — with no change to the detector, the stage, or the automaton. This
seam is documented in code comments; the restart wording is kept per the explicit
product requirement.

### Signature vs subject
`MatchedSubject` remains the log detail (e.g. the leaked tool name `Read`);
`MatchedSignature` is the config identity (`invoke`) used to build the disable key.
For every matcher except `<invoke>`, subject == signature; for `<invoke>` the
subject is the captured tool name while the signature is the constant `invoke`.

### AOT-safe error message
`ToolLeakError.Message(signature)` is embedded in hand-built JSON with no escaping,
so it must contain no `"` or `\`. It uses single quotes around the key
(`'…Signatures:Invoke'`), `:` in the path, `=false`, and a plain-text restart
instruction — all JSON-safe. A test parses the emitted JSON with `JsonDocument` to
guard this.

## Risks / Trade-offs

- A user disabling a signature loses protection for that one family until re-enabled
  — acceptable and the whole point (targeted escape hatch vs. all-or-nothing).
- Restart-required is a mild UX cost; mitigated by naming the exact key and the
  restart need in both the error and the log, and by the documented hot-reload seam.

## Migration

Backward compatible. No action for existing deployments; the absent `Signatures`
block behaves as all-enabled. Users hitting a false positive add the block and set
the offending key to false, then restart.
