# Implementation Tasks

## 1. Signature declaration (single-source, drift-guarded)

- [x] 1.1 Add `public const string SystemReminder = "system-reminder";` to `LeakSignatures` and append `SystemReminder` to `LeakSignatures.All` (last, after `Tick`).
- [x] 1.2 Add `public bool SystemReminder { get; set; } = true;` to `ResponseLeakSignaturesOptions` with an XML doc comment, and add the `LeakSignatures.SystemReminder => SystemReminder` case to `IsEnabled`.
- [x] 1.3 Confirm `ResponseDetectionError.ConfigKey("system-reminder")` yields `SystemReminder` (deterministic split-on-`-`+capitalize — no code change; assert with a test in step 3).

## 2. Matcher + automaton wiring

- [x] 2.1 Add `SystemReminderMatcher` to `ResponseLeakAutomaton`, symmetric with `TickMatcher`: KMP `<system-reminder>` open, KMP `</system-reminder>` close, bounded inner-length counter, trips iff inner text is non-empty. `Signature => LeakSignatures.SystemReminder`, `Subject => LeakSignatures.SystemReminder`.
- [x] 2.2 Add one `AddIfEnabled(LeakSignatures.SystemReminder, () => new SystemReminderMatcher());` line in the constructor (after the `Tick` line).
- [x] 2.3 Update the automaton/detector/options class-summary XML comments that enumerate the envelopes to include `<system-reminder>` (ResponseLeakAutomaton, ResponseLeakDetector, ResponseLeakGuardOptions).

## 3. Config

- [x] 3.1 Add `"SystemReminder": true` to the `Signatures` block in `src/CopilotBridge.Cli/appsettings.json`, and extend the `_Signatures` comment to mention the system-reminder wrapper.

## 4. Tests (from-contract; mutation-check each new test)

- [x] 4.1 `ResponseLeakAutomatonControlEnvelopeTests`: add `<system-reminder>` to the `AllEnvelopes` member data (canonical shape `<system-reminder>\n…\n</system-reminder>`) so it inherits every generic contract (positive, matched subject/signature, every-split-point, char-by-char, fenced-not-leak, fences-not-tracked-detects, disabled-omits-only-that-matcher).
- [x] 4.2 Add system-reminder-specific negatives: unclosed `<system-reminder>…` not a leak; empty `<system-reminder></system-reminder>` not a leak; overlapping restart `<system<system-reminder>…</system-reminder>` detected; clean prose mentioning "system-reminder" not a leak.
- [x] 4.3 `LeakSignatureWiringTests`: the existing drift tests (`EverySignatureId_HasAConfigFlag`, `AllSignaturesEnabled_BuildsAMatcherForEveryId`, `DefaultOptions_EnableEverySignature`) must now cover seven ids — verify they pass unchanged (they iterate `LeakSignatures.All`).
- [x] 4.4 `ResponseInspectionStageTests`: add a streaming `<system-reminder>` leak that injects one error event and stops; add a per-signature test that `Signatures.SystemReminder=false` passes the envelope through while a sibling still aborts.
- [x] 4.5 `ResponseDetectionErrorTests` (if it enumerates signatures): confirm `ConfigPath("system-reminder")` = `Pipeline:Detectors:ResponseLeakGuard:Signatures:SystemReminder`.
- [x] 4.6 Mutation-check: temporarily break `SystemReminderMatcher` (e.g. always return false) and confirm the new positives go red; revert.

## 5. Docs

- [x] 5.1 `docs/pipeline-design.md` §6.1: add `<system-reminder>` to the enumerated control envelopes the response-leak guard detects.
- [x] 5.2 `README.md`: extend the leak-guard / diagnostics wording to include the system-reminder wrapper among the leaked control messages that are auto-repaired.
- [x] 5.3 Update the memory pointer note if relevant (leak-guard now covers seven signatures) — optional, non-blocking.

## 6. Verify

- [x] 6.1 `dotnet test tests/CopilotBridge.UnitTests --filter "Category!=Integration"` green.
- [x] 6.2 `openspec validate add-system-reminder-leak-signature --strict` passes.
