# The flywheel in depth

The loop turns a live client failure into a bridge fix, a unit test, and a permanent
regression guard — then spins again. This file is the "why" behind each turn.

## The loop, expanded

1. **Integration (actuator).** `dotnet test --filter "Kind=ClientBehavior"` boots a
   real bridge subprocess (`ServeProcess`) with a scenario appsettings on a free
   non-8765 port, drives the real client on a path-exercising task, and writes a run
   manifest. The xUnit assertions cover only the harness contract (bridge up, client
   ran, evidence captured).
2. **Verdict (this skill).** Read each manifest → open the client's own evidence →
   PASS / FAIL / INCONCLUSIVE per `references/evidence.md`.
3. **Fix the bridge.** A FAIL is a bridge-code (or contract-understanding) defect until
   proven otherwise — not a test to weaken. Diagnose from the client log's failure
   signature.
4. **Unit tests from the CONTRACT.** Add/adjust unit tests that assert *what the
   behavior must be* (derived from the client-side failure), never mirror the code. A
   new unit test that passes first try is suspect — mutation-check it (break the
   product code, watch it redden). This is the project's highest-priority testing
   directive.
5. **Add a permanent regression guard.** Capture the client bytes that triggered the
   bug and add an `ApiContract` replay (post the captured `input[]` to
   `/codex/responses` or `/cc/v1/messages`, assert the fixed shape). That is what makes
   the bug un-reshippable: the live behavior test *found* it; the replay *keeps it
   found* without needing a live client every run.
6. **Spin again** — re-run the behavior leg; the case that failed must now PASS, and
   the new replay must be green.

## Fix vs. re-scope: which is it?

When a case FAILs, decide honestly:

- **Bridge bug** (the common case) → the client log shows a dispatch fatal / wrong wire
  shape / dropped field. Fix the bridge. Default hypothesis.
- **Case doesn't actually hit the path** → the client log shows the tool never fired,
  or a different path ran. Gate 1 wasn't met — fix the *case* (a stronger prompt, the
  right tool set, the right route), not the bridge. Verify the corrected case reaches
  the path before trusting a subsequent PASS.
- **Client-runtime issue, not the bridge** → e.g. codex's exec landing in a broken
  local JS isolate that `aborted` with the args byte-complete and status 200. That is
  the client's own runtime, not a bridge regression (a fully-restarted client fixes
  it). Triage: if the args were non-empty and the upstream was 200 but the tool
  `aborted`, suspect the client runtime before touching the bridge.

## Unit ↔ integration cadence

Integration (live client) is slow and needs Copilot + the client exe; unit tests are
fast and CI-safe. So:

- **Iterate on unit tests** while fixing (`dotnet test --filter "Category!=Integration"`).
- **Gate the fix on one live behavior case** (`Kind=ClientBehavior`) that hits the
  path — that is the acceptance signal, not the unit run.
- **Bank the regression** as an `ApiContract` replay so CI (which skips live) still
  guards it via the captured bytes.

A fix is DONE only when: the live behavior case PASSes from the client's own log, the
unit tests are green, and a replay guards the exact shape.

## What NOT to do

- Do **not** move the semantic verdict into xUnit. The moment "did exec run?" becomes an
  `Assert`, someone will make it pass on a bridge 200 and the flywheel is dead. The
  thin actuator + agent verdict split is the whole point.
- Do **not** widen an assertion to make a live case pass. A FAIL is the signal working.
- Do **not** call a leg verified off a fixture, a synthetic SSE replay, or a status
  code when the user asked for a real-client check.
