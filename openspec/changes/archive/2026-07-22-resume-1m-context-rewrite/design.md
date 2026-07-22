## Context

Claude Code decides its context window 100% client-side, before sending a request.
The window function was reverse-engineered from the **real running binary**
(`claude.exe` 2.1.216, `GIT_SHA 7fead72f`) — the decompiled 2.1.88/2.1.159 source
in `Q:\MyProjects\claude-code-sourcemap` is stale for this question and must not be
trusted here. Extraction: the bun-compiled exe embeds its JS as plaintext; pull it
with `tr -c '[:print:]' '\n' < claude > strings.txt` and grep.

The 2.1.216 window function (`X4c`, minified):

```
if (Sb(e)) return 1e6;                          // [1m] suffix (has1mContext)
if (t?.includes(context-1m-beta) && Hq(e)) return 1e6;   // beta + supports_1m_beta
if (nO(e)) return 1e6;                           // NEW: native_1m capability
let r = TKn(e); if (r !== null) return r;
...
return 200000;
```

`nO(e)` is the new mechanism and no longer depends on `[1m]`:

```
nO(e): let r = YA(po(e))?.context;               // YA = static bundled capability map
       if (!r?.native_1m) return false;           // opus-4-8 entry: native_1m: true
       let n = Ny(e);                              // provider
       if (n === "firstParty" && Wd() || K3(n) || n === "mantle") return true;
```

`Wd()` is the first-party host check with an escape hatch:

```
Wd(): if (process.env._CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL) return true;
      return host(ANTHROPIC_BASE_URL) === "api.anthropic.com";
```

So for a bridge user (`ANTHROPIC_BASE_URL` = `http://localhost:8765/cc`),
`Wd()` is false → `nO` false → the window falls through to 200,000. The persisted
transcript assistant `message.model` is the bare id (`claude-opus-4-8`), so
`--resume` re-derives 200k. **The bridge cannot fix this from the response side:**
the previously-attempted `[1m]` suffix on the response model is not what CC keys
off, and CC does not persist that suffix — verified empirically (the injection
left the transcript bare and resume showed 200,000).

## Goals / Non-Goals

**Goals:**
- Give bridge users a 1M context window on native-1M models that survives resume.
- Keep the transcript model clean and the change wire-faithful (no fabricated
  response bytes).
- Neutralize the one telemetry side effect of the mechanism.
- Deliver it through the bridge's existing `config` command; make it drift-aware.

**Non-Goals:**
- No change to sonnet-4.5 / haiku-4.5 (truly 200k on Copilot; their resume→200k is
  correct).
- No bridge-side response rewrite for the window.
- No change to the Codex config path (the env keys are Claude-Code-only).
- No attempt to make the window fetch a capability from the bridge (the CC table
  is static and bundled).

## Decisions

### D1 — Fix via two client env vars, written by the existing config command

The only durable lever the bridge holds over the CC process is the `env` block it
already writes in `settings.json` via `ClaudeCodeConfigurator`. `Wd()`'s escape
hatch `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` makes `nO()` fire for a bridge
base URL. Verified end-to-end against real `claude.exe` 2.1.216 + a bridge on a
non-8765 port:

| Scenario | contextWindow |
| --- | --- |
| plain `claude-opus-4-8`, no env | 200,000 (bug reproduced) |
| + `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` | 1,000,000 |
| **`--resume` + env** | **1,000,000** (bug target) |
| same session resumed without the env | 200,000 (proves the env is the lever) |

No `[1m]`, no response rewrite; the transcript model stays `claude-opus-4-8`, so
ccusage / cost tracking are unaffected.

*Alternative considered (response `[1m]` injection):* rejected — empirically inert
on 2.1.216 (this change's original, reverted approach).

### D2 — Also write `DISABLE_ERROR_REPORTING=1` (auto-neutralize the side effect)

A 116-function audit of every `Wd()`-gated behavior (extracted from the 2.1.216
binary, cross-checked with real-client wire capture) found the env flips only
`Wd()`-gated code (bridge users are already `firstParty`), and inference traffic
always targets `ANTHROPIC_BASE_URL` — never anthropic.com. claude.ai-only paths
(Teleport, Files API, subscription features) stay gated behind an OAuth token an
API-key user lacks. **One real side effect:** `Dzc` (Datadog error reporting)
flips off→on. Writing `DISABLE_ERROR_REPORTING=1` (which `Dzc` honors as its first
check) keeps it off, so enabling 1M changes exactly one thing.

The scariest audited candidate — tool-search beta (`Z2`), which Copilot rejects
with a 400 — was **disproven by real-client capture**: with the env set, CC does
not send the tool-search beta. The only two extra betas on the wire
(`advanced-tool-use-2025-11-20`, `cache-diagnosis-2026-04-07`) were probed and both
return 200 on Copilot. A real tool task (Write+Read) executed successfully with the
env set.

*Alternative considered (document the telemetry flip, don't auto-disable):*
rejected — leaves a surprising telemetry gap; the user chose auto-disable so the
unlock is transparent.

### D3 — Force-write both keys (like the base URL), drift-detected

Both keys are force-written (overwrite any pre-existing value) so the pair is
always consistent — mirroring how `ANTHROPIC_BASE_URL` is force-written. The
`config status` drift check grows to cover them: a bridge-pointed config missing
either key (or holding a non-`"1"` value) reads as DRIFTED. `ConfigState` gains
explicit expected/current fields for each (matching the existing
`ExpectedFallback`/`CurrentFallback` pair), keeping `Drifted` a plain equality
chain; Codex passes null for all, so it never drifts on them.

*Alternative considered (fill-if-absent):* rejected in favor of force-writing both
keys to the canonical `"1"`. Claude Code reads both as truthiness, so a pre-existing
non-`"1"` value (e.g. `"0"`) would still be in effect — force-writing is not about
correcting a functional failure but about keeping the bridge's managed state
canonical and drift-detectable (a fill-if-absent policy would leave arbitrary
pre-existing values that `config status` could not meaningfully report as drift).
The user chose always-on for both.

### D4 — Revert the dead `[1m]` code

The reverted M1 files (`ModelRewriteDetector` suffix, `ModelProfile.NativeOneMillionContext`,
`BridgeContext.RoutedNativeOneMillionContext`, `RestoreContext1mTag` option, the
appsettings key, and the two new test files) return to the shipped model-name-restore
behavior. Done via `git checkout` of the modified files + delete of the new ones,
so the model-rewrite regression suite is exactly as it shipped.

## Risks / Trade-offs

- **[The env asserts first-party for a non-first-party base URL]** → Audited: the
  only behavioral flips are the 1M window (intended) and error-reporting
  (neutralized). No prompt data is redirected to Anthropic; claude.ai-only paths
  stay closed for API-key users. Documented in `docs/context-window.md` §5.
- **[CC changes the gate again in a future version]** → The fix is confined to two
  documented env vars written by `config`; if a future CC drops the escape hatch,
  the keys become inert (no harm) and the doc/reverify steps catch it. The window
  mechanism is re-verifiable by driving `claude.exe --output-format json` and
  reading `modelUsage.<model>.contextWindow`.
- **[A user who wants CC error reporting on]** → They can remove
  `DISABLE_ERROR_REPORTING` after configuring; `config status` will then report
  drift (by design — the bridge's managed state includes it). This is the
  documented trade-off of the always-on decision.

## Migration Plan

- Pure config addition; a bridge user re-runs `config claude-code` and gets both
  keys. Existing configs are drift-flagged until re-run.
- No data migration. The reverted `[1m]` code never shipped (M1 branch only).

## Open Questions

- None. The mechanism and side-effect surface are verified against the running
  2.1.216 binary and real-client capture; the force-write + auto-disable decisions
  are the user's.
