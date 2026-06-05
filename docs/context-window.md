# Context windows, 1M, and Sonnet/Haiku routing

> Why the bridge can route Sonnet/Haiku correctly but **cannot** make Claude
> Code "know" a model's context window from a response, what actually happens
> when a user enables 1M on a model Copilot caps at 200k, and the one resume
> caveat we deliberately do **not** paper over. Grounded in live experiments
> against `claude.exe` 2.1.159 + Copilot Enterprise; cross-checked against the
> decompiled Claude Code source (`Q:\MyProjects\claude-code-sourcemap`).

## TL;DR

- **The context window is decided 100% client-side**, before the request is
  sent, from whether the model string carries a `[1m]` suffix. There is **no
  server-side lever** — nothing the bridge returns changes it.
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
- **Opus 1M works**: `opus-4.7[1m]` routes to `claude-opus-4.7-1m-internal` (a
  real 1M backend); `opus-4.8[1m]` is identity passthrough because Copilot's
  opus-4.8 is natively 1M (also re-probed 2026-06-05; 260k-token prompt 200).
- **Resume caveat**: after `--resume`, Opus reverts to 200k (the `[1m]` tag is
  not persisted). We do **not** fix this by injecting `[1m]` into the response —
  the real Anthropic API never does that, and faithfulness wins. Re-select
  `opus[1m]` to get 1M back.

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
| `claude-opus-4.5` / `4.6` / `4.7` (base) | 200000 | 200k |
| `claude-opus-4.8` (base) | 1000000 | **1M (probed: 260k → 200 OK)** |
| `claude-opus-4.6-1m`, `claude-opus-4.7-1m-internal` | **1000000** | 1M |

**The 200k models on Copilot are now: sonnet-4.5 and haiku-4.5.** Everything
else either is natively 1M (sonnet-4.6, opus-4.8) or has a dedicated `-1m` /
`-1m-internal` id that does (opus-4.6, opus-4.7). Also verified: sending the
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

`opus-4.7[1m]` works end-to-end: the bridge routes `claude-opus-4.7` +
`context-1m` → `claude-opus-4.7-1m-internal` (a real 1M backend) and strips the
now-redundant beta. Claude Code's 1M belief is honoured by a genuinely 1M model.

`opus-4.8[1m]` also works, but via a different mechanism: Copilot's
claude-opus-4.8 is **natively 1M** (re-probed 2026-06-05: a 260k-token padded
prompt returns 200 with or without `context-1m-2025-08-07`). So no model swap
and no beta strip — the request flows through identity passthrough, Copilot
honors the 1M ctx itself, and the bridge stays out of the way.

**Effort-derivation fix (same task).** For adaptive-only models (Opus 4.7 base /
4.8), `ProfileAdjuster` coerces `thinking:enabled` → `adaptive` and *derives*
`output_config.effort` from the thinking budget. That derived value previously
skipped effort validation, so Claude Code sending `thinking:enabled` with a
budget that maps to `high`/`xhigh` produced `400 output_config.effort "high" is
not supported by model claude-opus-4.8; supported values: [medium]`. Fix:
`ProfileAdjuster.Apply` re-runs `ApplyEffort` after `ApplyThinking`, so the
derived effort is validated like any client-sent one — Opus 4.8 strips it
(adaptive default, accepted), Opus 4.7 base routes to its `-high`/`-xhigh`
sibling. See `ProfileAdjusterTests`.

## 5. The resume caveat (deliberately not "fixed")

`ResponseModelRewrite` rewrites the response `model` back to the **client-
requested** id, so Claude Code's session log / ccusage report (e.g.)
`claude-opus-4-8` instead of the backend `claude-opus-4.7-1m-internal`. Claude
Code persists *that response model* and restores it on `--resume`.

Measured:

| Moment | model Claude Code restores | window |
| --- | --- | --- |
| first run, `--model opus[1m]` | `claude-opus-4-8[1m]` | 1,000,000 |
| `--resume` (no `--model`) | `claude-opus-4-8` | **1,000,000 if 4.8 (native 1M); 200,000 if 4.7 (routed via -1m-internal)** |

The rewrite fixes the *version* on resume (no longer the 4.7 variant) but the
`[1m]` tag is not part of the persisted model string, so for 4.7 the **window
reverts to 200k**. For opus-4.8 the resumed window is actually still 1M —
because Claude Code's 1M decision is `has1mContext(model)` (the `[1m]` tag),
which fails on resume, but Copilot's 4.8 serves whatever fits regardless, and
the bridge no longer downgrades. The session log says 200k but the backend
will still accept up to 1M; Claude Code's auto-compaction will trigger at 200k
unless re-selected as `opus[1m]`.

For sonnet-4.6 the same applies — Copilot serves 1M either way; the client
believes 200k after resume but `--model sonnet[1m]` re-instates the 1M belief.

We deliberately **do not** fix this by emitting `claude-opus-4-8[1m]` in the
response `model`: the real Anthropic API never puts `[1m]` there, and the bridge
stays faithful to the API (a fabricated value risks Claude Code's display /
cost / canonical-name parsing). The accepted workaround: after `--resume`,
re-select `opus[1m]` to restore 1M. For Sonnet/Haiku, reverting to 200k on resume
is in fact the *correct* outcome.

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
- **Does Claude Code fetch `/models`?** Watch the bridge log during a turn — for
  a non-`ant` user it never requests `/cc/v1/models`.
