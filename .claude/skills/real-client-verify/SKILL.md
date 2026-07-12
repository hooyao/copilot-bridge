---
name: real-client-verify
description: >-
  Verify a copilot-bridge change by driving the REAL headless client (codex.exe /
  claude.exe) through a running bridge and reading the CLIENT'S OWN evidence — the
  flywheel that turns "did the client actually execute?" into the thing that drives
  the next fix. Use this WHENEVER you touched bridge code that affects a client
  (tool protocol, Codex /responses T1–T4, Claude /cc, routing, streaming, detectors),
  are about to ship/PR a fix, or say things like "test through real codex/claude",
  "verify this fix end to end", "does the client actually run the tool", "did the
  exec/tool call work". A bridge HTTP 200 and a green unit test are NOT proof and must
  never be reported as one: codex records the "incompatible payload" tool-router fatal
  ONLY in its own logs_2.sqlite while the bridge stays 200. Run the client on a task
  that hits the code path you changed, then read the client's log — that is the
  verdict. Do NOT skip this by claiming it "needs the desktop app" or "can't be
  headless"; it can, and this skill is how.
compatibility: >-
  Windows + live Copilot login (github_token.dat via DPAPI, or the ~/github_token.dat
  dev fallback). Needs the built bridge (dotnet build src/CopilotBridge.Cli) and the
  real client exe: codex.exe (auto-found under %LOCALAPPDATA%\OpenAI\Codex\bin or
  CODEX_EXE) and/or claude.exe (PATH or CLAUDE_EXE). The codex log reader is a .NET 10
  file-based app (dotnet run scripts/read-codex-log.cs).
metadata:
  author: cc-copilot-bridge
  version: "1.0"
---

# Real-client verify — the behavior flywheel

The bridge is a translator between a real agent client (Claude Code, Codex CLI) and
Copilot. **Its correctness is defined by whether the client can parse and execute what
the bridge sends back — not by whether Copilot accepted what the bridge sent up.** A
`200` from the bridge means only the *upstream* was happy. This skill closes that gap:
drive the real client on a task that exercises the code you changed, then render the
verdict from the **client's own evidence**.

## Why this exists (the failure it prevents)

Three gpt-5.6 bugs shipped green — `Missing namespace for function_call`,
`Polymorphism_UnrecognizedTypeDiscriminator, agent_message`, and `exec invoked with
incompatible payload` — while the headless smokes passed. Both blind spots are baked
into this skill's two gates:

1. **Wrong task.** The smokes ran a trivial `echo … > f; cat f` task. That never
   reaches the namespaced-tool / multi-agent / custom-`exec` paths where the bugs
   lived. A task must exercise the path you changed.
2. **Wrong evidence.** The smokes asserted on the bridge trace + exit code + stdout
   canary. The `exec` fatal is a codex-internal router error recorded **only** in
   `~/.codex/logs_2.sqlite` — the bridge stayed 200 with `function_call` on the wire.
   Every asserted signal was green while exec was 100% broken. The verdict must come
   from the client's own dispatch log.

> One line: **a bridge 200 says nothing about whether the client could execute what
> the bridge sent back.** Read the client's log.

## The flywheel loop

```
        ┌─────────────────────────────────────────────────────────────┐
        │ 1. Integration: dotnet test --filter "Kind=ClientBehavior"  │
        │    → ServeProcess boots a REAL bridge subprocess (scenario   │
        │      appsettings, non-8765 port) and drives real codex/claude│
        │      on a PATH-EXERCISING task; writes a run manifest.       │
        └───────────────────────────┬─────────────────────────────────┘
                                    ▼
        ┌─────────────────────────────────────────────────────────────┐
        │ 2. Verdict (THIS skill): read each manifest → open the       │
        │    CLIENT's own log (codex logs_2.sqlite / claude transcript)│
        │    + the bridge trace → PASS / FAIL / INCONCLUSIVE.          │
        └───────────────────────────┬─────────────────────────────────┘
                        FAIL ▼                       ▲ PASS → ship
        ┌─────────────────────────────────────────────────────────────┐
        │ 3. Fix bridge code → 4. unit tests (from CONTRACT) →         │
        │    5. add an ApiContract captured-byte replay so it can never│
        │    silently regress → back to 1.                            │
        └─────────────────────────────────────────────────────────────┘
```

The dotnet layer is a **thin actuator**: it only proves the bridge came up, the client
ran to completion, and evidence was captured. It deliberately does NOT assert the tool
executed — that verdict is yours, from the client log. (Encoding the semantic verdict
in xUnit is exactly what shipped the three green-but-broken releases.)

## The two non-negotiable gates

Before you call anything verified, both must hold. If you cannot satisfy one, say so
and mark the leg **UNVERIFIED** — never dress a bridge 200 up as a pass.

### Gate 1 — the task must exercise the code path you changed
Map your change to a case that reaches it. `echo/cat` is fine for a plain-tool
regression but proves nothing about namespaced tools, multi-agent `agent_message`, or
custom `exec`. See `references/test-cases.md` for the taxonomy and which case hits
which path.

### Gate 2 — the verdict comes from the CLIENT's own dispatch log
- **Codex** → the real `~/.codex/logs_2.sqlite`, windowed to this run (the manifest's
  `dispatchLogPath` + `dispatchSinceUnix` — codex logs to the real home, NOT
  `CODEX_HOME`). Read it with `scripts/read-codex-log.cs`. PASS requires: the tool
  actually ran (the canary is in the client stdout, no `aborted` in stdout) AND **zero**
  rows matching `[ERROR] codex_core::tools::router` / `incompatible payload` /
  `Missing namespace` / `Polymorphism_`.
- **Claude Code** → the behavior tests capture `--output-format stream-json --verbose`,
  so the manifest's `stdoutPath` carries the INTERMEDIATE assistant / `tool_use` /
  `tool_result` events (not just the final result envelope), cross-checked against the
  bridge trace. PASS requires: the turn completed (final result present, not an error)
  AND the `tool_use` → `tool_result` round-trip is present (in the stream-json transcript
  and/or on the wire) — not just a streamed 200.

A bridge-side 200 alone is **INCONCLUSIVE**, never PASS.

## How to run it

1. **Build the bridge** (JIT is fine — the flywheel launches a real `serve` subprocess,
   no AOT publish needed): `dotnet build src/CopilotBridge.Cli`.
2. **Run the behavior leg** for what you changed (they need live Copilot + the client
   exe; they are `[Trait("Category","Integration")]` so CI skips them):
   ```powershell
   dotnet test tests/CopilotBridge.Playground --filter "Kind=ClientBehavior"
   ```
   Target one case with an extra `FullyQualifiedName~` filter (e.g.
   `~CodexBehaviorTests`) while iterating.
3. **Render the verdict.** Each run wrote a manifest under `tests/behavior-runs/manifests/`.
   For each: read the manifest, then read the client's own evidence it points at —
   for codex, run the log reader against the real `~/.codex/logs_2.sqlite` windowed to
   the run (`dispatchLogPath` + `dispatchSinceUnix` + `dispatchUntilUnix` from the
   manifest — codex logs to the real home, NOT `CODEX_HOME`; BOTH bounds matter so a
   later run's fatal isn't misattributed to this one):
   ```powershell
   dotnet run .claude/skills/real-client-verify/scripts/read-codex-log.cs -- "<dispatchLogPath>" <dispatchSinceUnix> <dispatchUntilUnix> "<out.txt>"
   ```
   then Read `<out.txt>`. Apply the Gate-2 rubric. Details + exact field names:
   `references/evidence.md`.

## When to reach for the in-process harness instead

`ServeProcess` (subprocess) is the faithful default — it exercises the real CLI args +
appsettings binding + routing validation. The older in-process `BridgeFixture` boots
the bridge inside the test host; it is faster but bypasses those layers. Use the
subprocess path for anything touching config/routing/startup; the in-process fixture
is fine for a pure translation check. The captured-byte **ApiContract** suite
(`Kind=ApiContract`) is the complementary net: it replays real client bytes and
asserts exact wire shape — that is where a fixed bug becomes a permanent regression
guard (step 5). See `references/flywheel.md`.

## Reference files

- `references/flywheel.md` — the loop in depth: fix-vs-rescope decisions, the
  unit↔integration cadence, and how each live failure becomes both a bridge fix and a
  permanent `ApiContract` replay.
- `references/test-cases.md` — the preset case taxonomy (plain-markdown prompts +
  expected client-side evidence) across the three routes: CC-native `/cc`, Codex
  `/codex`, and CC→gpt. This is where you pick a case that satisfies Gate 1.
- `references/evidence.md` — reading each client's own evidence: the codex
  `logs_2.sqlite` schema + the reader, the claude transcript, the four-file bridge
  trace, and the exact failure signatures.
- `references/models.md` — the latest-models-only policy and how to bump the id under
  test when Copilot ships a newer model (catalog work stays in `copilot-model-sync`).

## Guardrails distilled

- **Real client, not a fixture / replay / 200** for a verification the user asked for.
- **Task must hit the changed path** — echo/cat proves nothing about namespaced /
  multi-agent / custom-exec.
- **Verdict = the client's own log** — codex `logs_2.sqlite`, claude transcript — not
  the bridge trace or exit code.
- **Never bind 8765** — `ServeProcess` picks a free high port; the user's real bridge
  owns 8765.
- **A green bridge audit is INCONCLUSIVE, not PASS.**
- **If you can't run the real client, STOP and mark it UNVERIFIED** — don't reflexively
  claim a path "can't be tested." Most paths are headless-reproducible via `codex exec` /
  `claude.exe`. The exceptions are the desktop-only multi-agent shapes (`agent_message`,
  namespaced collaboration tools) that `codex exec` does not emit — those are still
  covered, by the `ApiContract` captured-byte replays of the real bytes, not by a live
  CLI run. Pick the right harness for the path (see `references/test-cases.md`); never
  skip a path entirely.
