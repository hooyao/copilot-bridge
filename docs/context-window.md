# Context windows, 1M, and Sonnet/Haiku routing

> Why the bridge can route Sonnet/Haiku correctly but **cannot** make Claude
> Code "know" a model's context window from a *response*, what actually happens
> when a user enables 1M on a model Copilot caps at 200k, and how the `config`
> command restores 1M after `--resume` via two client env vars. Grounded in live
> experiments against `claude.exe` 2.1.159 + Copilot Enterprise; the resume
> mechanism (§5) is re-grounded against the **running 2.1.216 binary** — the
> decompiled source (`Q:\MyProjects\claude-code-sourcemap`) is 2.1.88/2.1.159 and
> is stale for the window-decision logic, so §5 was reverse-engineered from the
> shipped exe.

## TL;DR

- **The context window is decided 100% client-side**, before the request is
  sent. No *response* the bridge returns changes it — but a *client-config* lever
  does: the `config claude-code` command writes two env vars that make Claude
  Code grant 1M and keep it across `--resume` (see §5).
- **Plain Sonnet/Haiku already works perfectly**: Claude Code uses 200k, Copilot
  base is 200k (4.5 / haiku) or 1M (sonnet-4.6, re-probed). No special handling
  needed for the plain path.
- **`sonnet-4.5[1m]` / `haiku[1m]` is a client-side trap**: Claude Code believes
  1M, but those models really are 200k on Copilot. This **degrades gracefully on
  its own** — when the conversation overfills 200k, Copilot returns a `prompt is
  too long` error that Claude Code already self-heals on. The bridge just strips
  the now-meaningless `context-1m` beta on the way out for those two profiles.
- **`sonnet-4.6[1m]` works**: Copilot upgraded sonnet-4.6 to native 1M ctx
  (re-probed 2026-06-05; an 851k-token prompt returns 200). The profile no
  longer carries a `context-1m-*` strip, so Claude Code's 1M belief is honored
  end-to-end without any model swap.
- **Opus 1M works**: `opus-4.6[1m]` / `opus-4.7[1m]` / `opus-4.8[1m]` are all
  identity passthrough — Copilot serves 1M on the base id natively (opus-4.8
  always did; opus-4.6/4.7 base ids were upgraded to 1M and their dedicated
  `-1m` / `-1m-internal` variants retired in the 2026 reconciliation). No model
  swap, no beta strip; Claude Code's 1M belief is met by the base model.
- **Resume now keeps 1M**: on 2.1.216 the window comes from a bundled
  `native_1m` capability gated on the request being first-party, so a bridge base
  URL reverts to 200k after `--resume`. `config claude-code` fixes this by writing
  `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` (+ `DISABLE_ERROR_REPORTING=1` to
  neutralize the one side effect). No `[1m]` suffix needed; the transcript model
  stays clean. See §5.

## 1. Where the context window comes from — the client, not the server

Claude Code computes the window in `getContextWindowForModel(model, betas)`
(`utils/context.ts`). For a bridge user (`USER_TYPE !== 'ant'`, custom base URL)
the decision collapses to:

1. `has1mContext(model)` — does the model string literally contain `[1m]`? →
   **1,000,000**. Checked **first**, before everything below.
2. `getModelCapability(model)` — reads `max_input_tokens` from `/v1/models`. This
   is the "context window as a response parameter" one might hope to use, but it
   is **dead for bridge users**: `isModelCapabilitiesEligible()` requires
   `USER_TYPE === 'ant'` **and** a first-party Anthropic base URL.
   `refreshModelCapabilities()` returns early before it even fetches.
3. Otherwise → **200,000** default.

> **2.1.216 update:** the running client adds a step between 1 and 3 — a
> `native_1m` **capability** branch (from a bundled model table) gated on the
> request being first-party. It changes the resume story, not this section's
> response-side conclusion. §5 has the full 2.1.216 decision path; §1's point —
> that no *response* the bridge returns sets the window — still holds.

**Empirical confirmation (claude.exe 2.1.159):** running a plain
`claude-sonnet-4.6` turn, the bridge sees **zero** `/v1/models` requests — Claude
Code never fetches it. And `--output-format json` reports
`modelUsage.<model>.contextWindow` directly:

| Selection | `contextWindow` Claude Code reports |
| --- | --- |
| `sonnet` / `haiku` (plain) | `200000` |
| `sonnet[1m]` / `haiku[1m]` | `1000000` |
| `opus[1m]` | `1000000` |

> Consequence: the bridge **cannot** tell Claude Code a window. Returning 200k in
> `/cc/v1/models` (which the bridge already does, honestly) is ignored; and even
> if it weren't, the `[1m]` check (step 1) sits in front of it.

How `[1m]` reaches the wire: `parseUserSpecifiedModel` keeps `[1m]` on the
*internal* model string (→ window + the `context-1m-2025-08-07` beta via
`getAllModelBetas`), then `normalizeModelStringForAPI` **strips `[1m]` before the
request**. So the bridge only ever sees a bare model id plus the `context-1m`
beta — never `[1m]`. The shipped routing locations key on exactly that signal.

## 2. What Copilot actually offers

From Copilot's `/models` (projected through `/cc/v1/models`):

| Model | `max_input_tokens` per `/models` | Actually serves (probed) |
| --- | --- | --- |
| `claude-sonnet-4.5` | 200000 | 200k (probed: 212k → `prompt is too long`) |
| `claude-haiku-4.5` | 200000 | 200k (probed: 212k → `prompt is too long`) |
| `claude-sonnet-4.6` | 1000000 | **1M (probed: 851k → 200 OK)** |
| `claude-sonnet-5` | 1000000 | **1M (probed: 677k → 200 OK)** |
| `claude-opus-4.6` / `4.7` (base) | 1000000 | **1M (2026 re-probe: 639k / 677k → 200 OK)** |
| `claude-opus-4.8` (base) | 1000000 | **1M (probed: 260k → 200 OK)** |

**The 200k models on Copilot are now: sonnet-4.5 and haiku-4.5.** Everything
else — sonnet-4.6, sonnet-5, opus-4.6, opus-4.7, opus-4.8 — is natively 1M on
the base id. (The 2026 reconciliation: opus-4.6 / opus-4.7 base ids were 200k
when this doc was first written and needed a redirect to a dedicated `-1m` /
`-1m-internal` id to reach 1M; Copilot has since **upgraded the base ids to 1M
and retired those dedicated variants**, so the redirects are gone — see
`docs/routing.md` → Retired: the opus 1M redirects.) Also verified: sending the
`context-1m` beta to a 200k model returns **200** — Copilot accepts-and-ignores
it (it does not 400).

## 3. Sonnet / Haiku

### Plain (no `[1m]`) — already correct

Claude Code uses 200k; for sonnet-4.5/haiku-4.5 Copilot is also 200k (perfect
match); for sonnet-4.6 Copilot is actually 1M (so plain selection silently
under-uses the available window, but that's identical to the real Anthropic
API behavior and Claude Code controls the cap). The profile coerces
effort/thinking to what each model accepts. Nothing to do.

### `sonnet-4.5[1m]` / `haiku[1m]` — graceful degradation

The user *can* land here: Claude Code offers a "1M" upgrade for the `sonnet`
alias (and a user can type `haiku[1m]` explicitly; the `[1m]` suffix forces 1M
even though `modelSupports1M` is false for Haiku). For sonnet-4.5 and haiku-4.5
the bridge **cannot** undo the client's 1M belief and Copilot really is 200k
for those models. What happens instead:

1. Claude Code (believing 1M) lets the conversation grow past 200k.
2. Copilot returns `400 {"type":"error","error":{"type":"invalid_request_error",
   "message":"prompt is too long: N tokens > 200000 maximum"}}` — for both
   streaming and non-streaming requests (the overflow is detected before the
   stream starts, so it is an HTTP 400 with a JSON body, not an SSE event).
3. The bridge relays that body **verbatim**.
4. Claude Code's `errors.ts` matches `"prompt is too long"` →
   `prompt_too_long` → its compaction recovery (and
   `parsePromptTooLongTokenCounts` reads `N > 200000` to size the compaction).

So the bridge needs **no error-rewrite** — Copilot's error is already in the
exact shape Claude Code self-heals on. The only bridge change is hygiene: the
sonnet-4.5 / haiku-4.5 profiles carry `StripBetas = ["context-1m-*"]` so the
bridge does not forward a 1M beta the backend cannot honor. This does **not**
change Claude Code's window belief (that is already fixed client-side); it just
keeps the outbound request honest.

### `sonnet-4.6[1m]` — natively 1M, works end-to-end

Different from sonnet-4.5. Copilot now serves sonnet-4.6 with a real 1M context
window (probed 2026-06-05: an 851,612-token padded prompt returns 200 with
`copilot_usage.token_count.input = 851612`). So the bridge does **not** strip
`context-1m-*` on the sonnet-4.6 profile and does **not** do any model swap —
Claude Code's 1M belief is honored by an actually-1M backend, identity
passthrough is correct.

**Guard:** `ResponseModelRewriteStageTests.BufferedBody_UpstreamPromptTooLongError_RelayedVerbatim`
pins that the response pipeline never corrupts an error body (it has no top-level
`model` key, so the rewrite is a no-op) — protecting the self-heal from a future
refactor.

### Best results

For sonnet-4.5 / haiku-4.5, **don't enable 1M** — you gain nothing (Copilot is
200k for those) and you trade proactive auto-compaction for reactive (one
wasted round-trip + a compaction when you hit 200k). For sonnet-4.6,
**enabling 1M is genuinely useful** — Copilot serves it. A global client-side
cap is also available if you must: `CLAUDE_CODE_AUTO_COMPACT_WINDOW=200000`
forces auto-compaction at 200k regardless of the believed window (but it is
global across all models, so it would also cap a legitimate Opus or sonnet-4.6
1M session).

## 4. Opus

`opus-4.7[1m]` and `opus-4.6[1m]` work end-to-end by **identity passthrough**.
When this doc was first written the opus-4.6 / 4.7 base ids were 200k, so the
bridge routed `<model> + context-1m` → a dedicated `-1m` / `-1m-internal`
backend and stripped the beta. The 2026 reconciliation retired those variant ids
*and* upgraded the base ids to native 1M (re-probed: a 639k / 677k-token padded
prompt returns 200 with or without `context-1m-2025-08-07`;
`ModelProfileProbe.OpusBase_LargePrompt_ProbeOneMillionContextSupport`). So there
is no model swap and no beta strip any more — the request flows straight through,
Copilot honors the 1M ctx itself, and Claude Code's 1M belief is met by the base
model.

`opus-4.8[1m]` works the same way and always has: Copilot's claude-opus-4.8 is
**natively 1M** (probed: a 260k-token padded prompt returns 200 with or without
the beta).

**Effort-derivation fix (earlier task).** For adaptive-only models (Opus 4.7
base / 4.8 / sonnet-5), `ProfileAdjuster` coerces `thinking:enabled` → `adaptive`
and *derives* `output_config.effort` from the thinking budget. That derived value
previously skipped effort validation, so Claude Code sending `thinking:enabled`
with a budget that maps to `high`/`xhigh` produced `400 output_config.effort
"high" is not supported by model …; supported values: [medium]`. Fix:
`ProfileAdjuster.Apply` re-runs `ApplyEffort` after `ApplyThinking`, so the
derived effort is validated like any client-sent one. (Since the 2026 re-probe,
Opus 4.7 base / 4.8 / sonnet-5 accept `low..max` directly, so a derived
`high`/`xhigh` is now kept as-is rather than stripped or routed to a sibling.)
See `ProfileAdjusterTests`.

## 5. Restoring 1M after `--resume` (the firstParty capability gate)

**Symptom.** On the running client (2.1.216) a bridge user's opus-4.8 session
starts at 1M but, after `--resume` with no explicit `--model`, reverts to a
200,000 window — Claude Code's auto-compaction then fires at 200k for the rest of
the session even though Copilot's backend still serves 1M.

**Why (2.1.216, reverse-engineered from the shipped exe — the decompiled 2.1.159
source is stale here).** Claude Code's window function now reads a **bundled,
static model-capability table** and no longer relies on the `[1m]` suffix:

```
X4c(model, betas):                              // getContextWindowForModel
  if has1mContext(model) return 1e6             // ① literal [1m] in the string
  if betas.includes("context-1m-…") && supports_1m_beta(model) return 1e6   // ②
  if nO(model) return 1e6                        // ③ native_1m capability  ← the new one
  … return 200000

nO(model):
  cap = capabilityTable[canonical(model)].context   // static, bundled — NOT fetched
  if !cap.native_1m return false                     // opus-4-8 entry: native_1m: true
  if provider==="firstParty" && Wd() return true     // ← the gate bridge users fail
  …

Wd():                                             // "is this a real first-party host?"
  if process.env._CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL return true   // ← escape hatch
  return host(ANTHROPIC_BASE_URL) === "api.anthropic.com"
```

For a bridge user, `ANTHROPIC_BASE_URL` is `http://localhost:8765/cc`, so `Wd()`
is false → branch ③ doesn't fire → the window falls through to 200k. On the first
turn the `[1m]` suffix from `--model opus[1m]` still trips branch ①, but the
persisted transcript `message.model` is the bare `claude-opus-4-8` (CC does not
persist the `[1m]` suffix), so `--resume` re-derives 200k from branch ③ — which is
gated off. **This is why the earlier idea of injecting `[1m]` into the response
`model` does not work on 2.1.216: it's the wrong lever, and CC discards the suffix
on persist** (verified — the injection left the transcript bare and resume still
showed 200,000).

**The fix — two client env vars, written by `config claude-code`.** The escape
hatch `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` makes `Wd()` true, so branch ③
fires for a bridge base URL. `config claude-code` force-writes it (and its
companion) into `settings.json` `env`, next to `ANTHROPIC_BASE_URL`:

| env key | value | why |
| --- | --- | --- |
| `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` | `1` | asserts first-party so the `native_1m` capability applies to the bridge base URL |
| `DISABLE_ERROR_REPORTING` | `1` | neutralizes the one side effect: asserting first-party also flips CC's error-reporting (Datadog) telemetry on; this keeps it off |

Measured end-to-end against real `claude.exe` 2.1.216 (plain `claude-opus-4-8`, no
`[1m]`):

| Moment | env set? | `contextWindow` |
| --- | --- | --- |
| first run | no | 200,000 (bug reproduced) |
| first run | **yes** | 1,000,000 |
| **`--resume` (no `--model`)** | **yes** | **1,000,000 ← the fix** |
| `--resume` (same session) | no | 200,000 (proves the env is the lever) |

No `[1m]` suffix, no bridge response rewrite; the transcript model stays
`claude-opus-4-8`, so ccusage / cost tracking are unaffected. This applies to every
native-1M model (opus-4.6/4.7/4.8, sonnet-4.6, sonnet-5). For sonnet-4.5 /
haiku-4.5 the capability table has no `native_1m`, so branch ③ never fires and they
correctly stay 200k — the env does not (and should not) change that.

**Side effects of asserting first-party (audited, 116 `Wd()`-gated functions +
real-client wire capture).** The env only flips `Wd()`-gated behavior (a bridge
user is already `firstParty`); inference traffic still targets the bridge base URL,
never `api.anthropic.com`; claude.ai-only paths (Teleport, Files API, subscription
features) stay closed for an API-key user. The single real side effect is the
error-reporting telemetry, which is why `DISABLE_ERROR_REPORTING=1` is written with
it. A tempting worry — that first-party would enable the tool-search beta Copilot
rejects — was disproven by wire capture: CC does not send it; the only two extra
betas on the wire (`advanced-tool-use-2025-11-20`, `cache-diagnosis-2026-04-07`)
are both accepted by Copilot. `config status` reports and drift-detects both keys.

**Wire faithfulness.** Unlike the rejected `[1m]`-injection idea, this changes no
response bytes and fabricates no model string — it is a client-side configuration
signal, so the bridge stays byte-faithful to the Anthropic API on the wire.

## 6. Re-verifying

- **Does Copilot still return the CC-recognizable overflow on 200k models?**
  Post a >200k-token body to `/cc/v1/messages` (model `claude-sonnet-4.5` or
  `claude-haiku-4.5`); expect `400` with `prompt is too long: N tokens > 200000
  maximum`. See `ModelProfileProbe.NonOpus_LargePrompt_Probe200kBoundary`.
- **Does sonnet-4.6 actually serve >200k?** Same probe with model
  `claude-sonnet-4.6` — expect 200 with `copilot_usage.token_count.input` well
  above 200000 (last run: 851612).
- **Does opus-4.8 actually serve >200k?** See
  `ModelProfileProbe.Opus48_LargePrompt_ProbeOneMillionContextSupport`.
- **Does Copilot accept the `context-1m` beta on the 200k models?**
  `BetaAcceptanceTests` (filter `context-1m-2025-08-07`) — expect 200
  (accept-and-ignore).
- **What window does Claude Code compute?** Drive `claude.exe --bare -p ...
  --model <m> --output-format json` at the bridge and read
  `modelUsage.<m>.contextWindow`.
- **Does the resume fix hold?** Drive a plain `claude-opus-4-8` turn through the
  bridge with `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` set, then `--resume`
  the session with no `--model`; `modelUsage.<m>.contextWindow` must read
  `1000000` on both. Drop the env and the same resume reads `200000` — the
  control proving the env is the lever. (Note: the user's own
  `~/.claude/settings.json` `env.ANTHROPIC_BASE_URL` overrides a shell env; pass a
  `--settings` file to point a test run at a non-8765 bridge.)
- **Does Claude Code fetch `/models`?** Watch the bridge log during a turn — for
  a non-`ant` user it never requests `/cc/v1/models`.
