## Why

The unified tool-leak guard now trips on six distinct signatures — a leaked
`<invoke>` tool call plus five Claude Code control envelopes
(`<task-notification>`, `<teammate-message>`, `<channel>`,
`<cross-session-message>`, `<tick>`). The guard is shape-gated and conservative,
but it cannot distinguish a leak from a legitimate discussion *about* the markup.
A user debugging how Anthropic tool-use or a `task-notification` envelope works
can prompt the model to emit a faithful sample, and the guard will abort+retry the
turn — a false positive the user currently has no way to clear short of turning the
whole guard off (losing protection for the other five signatures).

## What Changes

- Add an independent on/off switch per signature under
  `Pipeline:Detectors:ToolLeakGuard:Signatures` (`Invoke`, `TaskNotification`,
  `TeammateMessage`, `Channel`, `CrossSessionMessage`, `Tick`; all default true).
  A disabled signature's matcher is not constructed, so its markup passes through
  unchanged while every enabled signature keeps tripping.
- Make the false positive self-service: the retry error surfaced to the client and
  the detection-point `Warning` both name the tripped signature AND the exact
  config key to disable it, and note that a **restart is required** after flipping
  the switch (config does not reload at runtime).
- Design for future hot-reload without implementing it now: the detector reads an
  `IOptionsSnapshot` and recomputes its enabled-signature set per request scope (no
  process-wide cache), so making a flipped switch take effect live is a one-seam
  change (register the config file with `reloadOnChange: true`) with no change to
  detection logic. Restart remains required today.

## Capabilities

### Modified Capabilities
- `tool-leak-guard`: add a per-signature enable switch under the existing
  `ToolLeakGuardOptions`; a disabled signature is not evaluated and passes through,
  and the retry error names the signature + disable key + restart note.
- `observability`: the detection-point `Warning` additionally names the exact
  config key that disables the tripped signature and the restart requirement.

## Impact

- **Config**: new nested `Signatures` block under
  `Pipeline:Detectors:ToolLeakGuard` (six booleans, all default true). Backward
  compatible — absent block behaves exactly as all-enabled.
- **Code**: `ToolLeakGuardOptions` gains `Signatures`; `ResponseLeakAutomaton`
  gates matcher construction and exposes `MatchedSignature`; `ToolLeakError`
  becomes signature-aware (names the signature + disable key + restart);
  `ToolLeakDetector` computes the enabled set per request and enriches the
  `Warning`. No new DTOs; the error JSON stays hand-built and AOT-safe.
- **Client contract**: unchanged retry signal (`overloaded_error`/`api_error`);
  the error message body gains signature/disable-key wording.
- **Hot path**: a disabled matcher is not built, so it costs nothing; enabled
  signatures behave as before.
