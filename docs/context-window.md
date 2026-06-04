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
  is 200k. No special handling needed.
- **`sonnet[1m]` / `haiku[1m]` is a client-side trap**: Claude Code believes 1M,
  but Copilot has no 1M Sonnet/Haiku. This **degrades gracefully on its own** —
  when the conversation overfills 200k, Copilot returns a `prompt is too long`
  error that Claude Code already self-heals on. The bridge just strips the
  now-meaningless `context-1m` beta on the way out.
- **Opus 1M works** because the bridge routes it to a real 1M backend
  (`claude-opus-4.7-1m-internal`).
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

| Model | `max_input_tokens` |
| --- | --- |
| `claude-sonnet-4.5`, `claude-sonnet-4.6` | 200000 |
| `claude-haiku-4.5` | 200000 |
| `claude-opus-4.5` / `4.6` / `4.7` / `4.8` (base) | 200000 |
| `claude-opus-4.6-1m`, `claude-opus-4.7-1m-internal` | **1000000** |

**Copilot has no 1M Sonnet or Haiku** (and no 1M Opus *base* — only the dedicated
`-1m` / `-1m-internal` ids). Also verified: sending the `context-1m` beta to
Copilot Sonnet returns **200** — Copilot accepts-and-ignores it (it does not 400).

## 3. Sonnet / Haiku

### Plain (no `[1m]`) — already correct

Claude Code uses 200k, Copilot is 200k, the profile coerces effort/thinking to
what each model accepts. Nothing to do.

### `sonnet[1m]` / `haiku[1m]` — graceful degradation

The user *can* land here: Claude Code offers a "1M" upgrade for the `sonnet`
alias (and a user can type `haiku[1m]` explicitly; the `[1m]` suffix forces 1M
even though `modelSupports1M` is false for Haiku). The bridge **cannot** undo the
client's 1M belief. What happens instead:

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
Sonnet/Haiku profiles carry `StripBetas = ["context-1m-*"]` so the bridge does
not forward a 1M beta the backend cannot honor. This does **not** change Claude
Code's window belief (that is already fixed client-side); it just keeps the
outbound request honest.

**Guard:** `ResponseModelRewriteStageTests.BufferedBody_UpstreamPromptTooLongError_RelayedVerbatim`
pins that the response pipeline never corrupts an error body (it has no top-level
`model` key, so the rewrite is a no-op) — protecting the self-heal from a future
refactor.

### Best results

For Sonnet/Haiku, **don't enable 1M** — you gain nothing (Copilot is 200k) and
you trade proactive auto-compaction for reactive (one wasted round-trip + a
compaction when you hit 200k). A global client-side cap is also available if you
must: `CLAUDE_CODE_AUTO_COMPACT_WINDOW=200000` forces auto-compaction at 200k
regardless of the believed window (but it is global across all models, so it
would also cap a legitimate Opus 1M session).

## 4. Opus

`opus[1m]` works end-to-end: the bridge routes `claude-opus-4.8` + `context-1m`
→ `claude-opus-4.7-1m-internal` (a real 1M backend) and strips the now-redundant
beta. Claude Code's 1M belief is honoured by a genuinely 1M model.

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
| `--resume` (no `--model`) | `claude-opus-4-8` | **200,000** |

The rewrite fixes the *version* on resume (no longer the 4.7 variant) but the
`[1m]` tag is not part of the persisted model string, so the **window reverts to
200k**.

We deliberately **do not** fix this by emitting `claude-opus-4-8[1m]` in the
response `model`: the real Anthropic API never puts `[1m]` there, and the bridge
stays faithful to the API (a fabricated value risks Claude Code's display /
cost / canonical-name parsing). The accepted workaround: after `--resume`,
re-select `opus[1m]` to restore 1M. For Sonnet/Haiku, reverting to 200k on resume
is in fact the *correct* outcome.

## 6. Re-verifying

- **Does Copilot still return the CC-recognizable overflow?** Post a >200k-token
  body to `/cc/v1/messages` (model `claude-sonnet-4.5`); expect `400` with
  `prompt is too long: N tokens > 200000 maximum`.
- **Does Copilot accept the `context-1m` beta on Sonnet?**
  `BetaAcceptanceTests` (filter `context-1m-2025-08-07`) — expect 200.
- **What window does Claude Code compute?** Drive `claude.exe --bare -p ...
  --model <m> --output-format json` at the bridge and read
  `modelUsage.<model>.contextWindow`.
- **Does Claude Code fetch `/models`?** Watch the bridge log during a turn — for
  a non-`ant` user it never requests `/cc/v1/models`.
