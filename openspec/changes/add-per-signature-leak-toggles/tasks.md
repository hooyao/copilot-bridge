# Implementation Tasks

## 1. Config

- [x] 1.1 Add `ToolLeakSignaturesOptions` (six PascalCase bools, all `= true`) and a
  `Signatures` property on `ToolLeakGuardOptions`, with XML docs covering the
  false-positive/disable/restart use case and the hot-reload design note.
- [x] 1.2 `LeakSignatures`: single source of truth for the six kebab signature ids
  plus an `All` array.
- [x] 1.3 `appsettings.json`: add the `Signatures` block (six booleans, all true)
  with an explanatory `_Signatures` comment.

## 2. Automaton

- [x] 2.1 `ResponseLeakAutomaton` constructor takes an optional
  `enabledSignatures` set (null = all) and constructs only enabled matchers.
- [x] 2.2 Add a `Signature` to `ILeakMatcher` and each matcher; add
  `MatchedSignature` to the automaton, captured alongside the subject on trip and
  cleared on reset.

## 3. Error + detector

- [x] 3.1 `ToolLeakError` becomes signature-aware: `Message(signature)`,
  `Json(signal, signature)`, `ConfigKey(signature)` (kebab→PascalCase), and
  `ConfigPath(signature)`. Message names the signature + disable key + restart and
  stays AOT/JSON-safe (no `"`/`\`).
- [x] 3.2 `ToolLeakDetector` computes the enabled set per request via
  `BuildEnabledSignatures` (no static cache; hot-reload seam documented) and passes
  it to the automaton.
- [x] 3.3 On trip, read `MatchedSignature`, pass it to `Json(signal, signature)`,
  and enrich the `Warning` with `signature=`, the disable key, and the restart note.

## 4. Tests (contract-derived, mutation-checked)

- [x] 4.1 `ResponseLeakAutomaton{Invoke,ControlEnvelope}Tests`: `MatchedSignature`
  naming distinct from subject; a disabled signature omits only its matcher while
  siblings still trip.
- [x] 4.2 `ToolLeakErrorTests`: `ConfigKey` kebab→PascalCase mapping (all six),
  `ConfigPath`, message names signature/key/restart, JSON-safe (no `"`/`\`), and
  `Json` parses as an error.
- [x] 4.3 `ResponseInspectionStageTests`: a disabled signature passes through; a
  disabled signature with another signature still aborts; the retry error names the
  signature + disable key + restart; the `Warning` names the disable key + restart.
- [x] 4.4 Mutation-check each new contract test (gating, `MatchedSignature`,
  `ConfigKey`, error restart wording, log restart wording, detector mapping) goes
  red on broken product, then revert.

## 5. Docs / specs

- [x] 5.1 `tool-leak-guard` spec: add the per-signature toggles requirement; note
  the `Signatures` sub-block in the configuration requirement.
- [x] 5.2 `observability` spec: the detection `Warning` also names the disable key
  and restart requirement.
- [x] 5.3 `docs/pipeline-design.md` §6.1 and `README.md`: document the six toggles,
  the escape-hatch use case, the named error/log, and the restart requirement.

## 6. Verification

- [x] 6.1 `dotnet test tests/CopilotBridge.UnitTests` green; CLI build clean (0
  warnings).
- [ ] 6.2 Solution-wide `dotnet test --filter "Category!=Integration"` green.
