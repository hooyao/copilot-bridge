## Context

`docs/timeout-chain.md` records the measured chain. Eight bounds exist between
Claude Code and Copilot; the binding one for a deep-thinking turn is a
**client-side** watchdog at 180 s, and it is armed as a side effect of the
bridge's own 1M-context flag (`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` makes
Claude Code treat the request as first-party, which swaps its idle budget from
300 s down to 180 s). Copilot emits no keepalive while a model thinks — zero
`ping` events across 137 captured traces — so silence is the normal shape of a
deep-thinking turn.

Lab measurements driving the real `claude.exe` 2.1.220 against a fake upstream
that goes silent on demand:

| silence | client env | outcome |
|---|---|---|
| 240 s | (none) | abort @ **180.013 s** |
| 240 s | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS=900000` | survived |
| 340 s | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS=900000` | abort @ **300.006 s** |
| 340 s | `CLAUDE_STREAM_IDLE_TIMEOUT_MS=900000` | survived |

Only `CLAUDE_STREAM_IDLE_TIMEOUT_MS` lifts both client watchdogs: the client
derives its byte-level budget from the event-level value whenever the byte-level
key is unset, so setting the event-level key raises both, while setting only the
byte-level key leaves the event-level one pinned at its 300 s floor.

Current bridge state: `HttpClient.Timeout` is hard-coded to 10 minutes
(`BridgeServiceCollectionExtensions.cs:113`).

> **Corrected during PR review (rounds 3/5).** This section originally claimed the
> cap bounded the **buffered** path end-to-end. It does not: both forward paths use
> `ResponseHeadersRead`, under which the cap ends at response headers on buffered
> and streaming responses alike — verified over a real socket by
> `UnderResponseHeadersRead_TheClientTimeoutDoesNotCoverTheBodyRead`. So removing it
> does not remove a body bound; it replaces a coarse, unconfigurable header cap with
> the configured per-attempt first-byte budget over the same phase. A buffered body
> that stalls after headers remains unbounded, which this change does not address.

## Goals / Non-Goals

**Goals:**

- The bridge is the single component that decides when a stalled turn ends. Its
  two `Pipeline:UpstreamTimeout` budgets are the only bound that fires in normal
  operation.
- The client is configured to outlast the bridge, automatically, by the same
  `config` command that already points it at the bridge.
- The operator can see the real effective bound at startup without deriving it
  from two config files.

**Non-Goals:**

- **SSE `ping` injection — deferred, not rejected.** Injecting keepalives is the
  complementary half of the fix and is strictly more powerful than configuration
  in two ways: it takes effect immediately (no client restart) and it protects a
  client this bridge never configured. It is also what the real Anthropic API
  does. It is out of *this* change only because it modifies the `/cc` relay
  rather than configuration, and that deserves its own review of the
  byte-passthrough contract. This design therefore writes **no** requirement
  forbidding keepalives, and the follow-up change owns that decision.
- Changing any forwarded or relayed bytes.
- Managing Codex timeouts. Codex's client-side behavior is not measured here, and
  the `config codex` path stays untouched.
- Making the client values independently configurable in `appsettings.json`.
  They are derived (see below), so there is exactly one place to tune.

## Decisions

### D1: Derive the idle key from the bridge budget; write the request cap as a fixed maximum

> **Revised during PR review (round 3).** The original decision derived BOTH keys
> from the budgets. That is unsound for `API_TIMEOUT_MS`: it is a **wall-clock**
> cap while the budgets bound **inactivity**, so no finite value can be guaranteed
> to outlast them — a healthy turn that keeps emitting has no total duration, and a
> stalled one can spend the first-byte budget *and then* one or more stream-idle
> gaps first (900 s + 600 s budgets vs a derived 1200 s). The derivation only moved
> the threshold while implying a guarantee that cannot exist, so it was removed.
> The text below records the final design.

Only the idle key is a function of the bridge's configured budgets:

```
CLAUDE_STREAM_IDLE_TIMEOUT_MS = clamp(StreamIdleTimeoutSeconds  + MarginSeconds) → ms
API_TIMEOUT_MS                = RequestTimeoutMaxMs            (fixed, NOT derived)
```

with `MarginSeconds = 300` (5 min) and the result clamped to the client's own
accepted range — notably the client hard-caps the idle value at 30 min, so a
larger derived value would be silently reduced and must be clamped by the bridge
so what it writes is what takes effect.

*Why:* one knob. The operator tunes `Pipeline:UpstreamTimeout`, re-runs `config
claude-code`, and the client follows. `config status` compares the stored value
against the freshly derived one, so raising a bridge budget without re-running
`config` is reported as drift — the same mechanism that already catches port
drift.

*Alternatives considered:* **fixed constants** (simplest, and drift becomes a
plain equality check — but a raised bridge budget silently stops being the
binding bound, which is the exact failure this change exists to prevent);
**writing the client's maximum** (client essentially never fires first — but if
the bridge process dies mid-turn the client hangs for 30 min instead of failing
promptly).

*Disabled budgets:* a budget configured `<= 0` means the bridge imposes no bound
on that phase. There is no finite client value that can outlast "no bound", so
the derivation writes the clamped maximum for that key and the startup report
states the phase is unbounded.

### D2: `HttpClient.Timeout` becomes `Timeout.InfiniteTimeSpan`

The coarse cap is removed rather than raised. Raising it to a larger constant
would keep a second, invisible bound in the system that no configuration
mentions; the project already has two purpose-built inactivity budgets that are
strictly better (they bound *inactivity*, not total duration, so a
slow-but-progressing turn is never killed).

This is the change's one real behavioral risk and it is called out as
**BREAKING** in the proposal: an operator who disables *both* budgets previously
had a 10-minute backstop and now has none. That is the correct semantic — "zero
means no bound" is what the existing spec already promises for each budget — but
it was previously untrue, and the delta spec now says so explicitly.

Scope check: the shared `HttpClient` singleton is used for Copilot forwarding and
`count_tokens`. The `auth` and `debug` commands construct their own short-lived
clients with a 30 s timeout; those are untouched.

### D3: The startup report is a separate capability, not part of observability

`observability` covers per-request diagnosis (leak detection, trace correlation,
request summaries). The effective-timeout report is a startup, whole-process
statement about configuration. It gets its own capability
(`timeout-budget-report`) so its requirements stay legible rather than being
appended to a per-request spec.

The report reads Claude Code's `settings.json` **best-effort**: a missing,
unreadable, malformed, or not-pointed-at-this-bridge file must not fail startup,
because the bridge is perfectly usable by a client configured some other way.
The existing tolerant read path in `ClaudeCodeConfigurator.Read` already has
these semantics and is the natural seam to reuse — but note it currently lives in
the `config` composition root, which the `client-autoconfiguration` spec requires
to be isolated from the server startup path. The reusable piece is therefore the
*pure parsing/derivation logic*, which must be factored so the hosted service can
call it without pulling the config command's graph into the server.

### D4: Warn on any client bound that fires first — including a missing key

The warning fires when the client would abort before the bridge budget applies.
Two states qualify and are treated identically, because the operator experiences
them identically:

- a stored value **shorter** than the bridge budget;
- a **missing** value.

Treating absence as benign would be wrong on the evidence. The client's
first-party default is 180 s (measured), and the request is first-party
*precisely because the bridge itself writes*
`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1` for the 1M context window. So the
bridge does not need client-version clairvoyance to know the bound — it created
the condition that selects it. Absence is also the default state of every
installation configured before this change, i.e. the exact configuration that
produced the reported failure. A design that stayed silent there would leave the
common case unprotected.

Equal values do not warn: the client is not undercutting, it merely has no
margin.

The warning states **both** remedies rather than only the bridge's own command:
run `config claude-code`, or set the environment variable directly to at least
the named value. Some operators manage `settings.json` by other means (dotfiles,
MDM, a shared team config) and a bridge command that rewrites the file is not
always the right answer for them; naming the variable and the minimum value lets
them comply without handing the file over.

The residual client-version risk is confined to the *number* the warning quotes
for the absent case. `docs/timeout-chain.md` records how it was measured and the
lab harness is reproducible, so refreshing it is a documented procedure rather
than a guess.

## Risks / Trade-offs

- **No upstream bound when both budgets are disabled** → The delta spec states it
  plainly, `appsettings.json` documents it at both keys, and the startup report
  prints "no bound" for a disabled phase, so the state is visible rather than
  silent.
- **Written values take effect only on the client's next start** → Claude Code
  reads `env` at process start. The `config` command's summary and the startup
  report both say so; the spec forbids claiming otherwise. This restart
  dependency is one of the two reasons the deferred keepalive change is worth
  doing — it has no such dependency.
- **Configuration only protects a client this bridge configured** → A Claude Code
  instance pointed at the bridge by other means (hand-edited settings, MDM,
  a shared dotfile repo) gets the startup warning but not the fix. The warning
  therefore names the environment variable and its minimum value, not just the
  bridge command. The deferred keepalive change closes this gap properly.
- **Client-version drift** → The 30-minute clamp and the "only
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS` lifts both watchdogs" fact are properties of
  2.1.220, extracted from the running binary (the 2.1.88 decompile in
  `claude-code-sourcemap` disagrees and is stale). If a future client changes
  them, the written value could stop being sufficient. Mitigation: the numbers
  and their provenance live in `docs/timeout-chain.md`, and the lab harness that
  measured them is reproducible; the startup report surfaces the bridge side
  regardless.
- **Reusing the configurator's read path across composition roots** → Must not
  drag the config command's isolated graph into server startup (an existing spec
  requirement). Mitigation: factor the pure derivation/parse helper; the hosted
  service depends on that, not on the configurator's command wiring.
- **A slow real hang now takes longer to surface** → With the client no longer
  aborting at 180 s, a genuinely wedged upstream is bounded by the bridge budget
  instead. This is the intended trade: the bridge's budgets are inactivity-based
  and operator-visible, where the client's were neither.

## Migration Plan

1. Land the code change; existing installs keep working — the new env keys are
   simply absent until the operator re-runs `config claude-code`.
2. `config status` reports the missing keys as drift, which is the prompt to
   re-run.
3. Rollback: revert the commit. The written client env keys are inert once the
   bridge no longer derives them (they remain valid Claude Code settings and can
   be removed by hand); no persisted state migration is involved.
4. Follow-up: a separate `inject-sse-keepalive` change adds bridge-side `ping`
   injection, which removes both the restart dependency and the
   only-configured-clients limitation noted under Risks. This change leaves that
   design space open — it writes no requirement forbidding keepalives.

## Open Questions

None blocking. The value-derivation policy (D1) was chosen explicitly by the
operator over fixed constants and max-out alternatives.
