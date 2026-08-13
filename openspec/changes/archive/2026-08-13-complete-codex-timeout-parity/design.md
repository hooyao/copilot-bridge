## Context

The timeout audit of `release-0.5.3-beta` found that the current startup table is
not an end-to-end turn calculation:

- `config claude-code` force-writes
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS = min((StreamIdleTimeoutSeconds + 300) * 1000,
  1800000)` and `API_TIMEOUT_MS = 3600000`; a disabled bridge idle budget is
  silently converted to the 30-minute Claude client cap.
- `config codex` replaces the complete bridge provider table. A user-authored
  `stream_idle_timeout_ms`, `request_max_retries`, or `stream_max_retries` in that
  table is discarded and Codex falls back to its built-in values.
- the report labels upstream response-header and per-event-gap deadlines as
  `what ends a turn`, although bridge, Codex, and Claude retry layers can restart
  those timers;
- the option called `FirstByteTimeoutSeconds` ends when **upstream headers** arrive,
  before the client receives a downstream byte. The first downstream event can
  require one header budget plus one first-SSE-event gap per bridge send;
- a true buffered body has no bridge timeout after upstream headers;
- only global client files are inspectable at bridge startup. Current Codex
  precedence permits project, profile, and CLI overrides above the global file;
  Claude Code likewise has repo and process-environment sources.

The official [OpenAI Configuration Reference](https://learn.chatgpt.com/docs/config-file/config-reference)
confirms Codex defaults of 300,000 ms SSE idle, four HTTP request retries, and
five SSE stream retries. Current `openai/codex` source at
`eb752e43d9b7bd7dc5965ea20642bcf7f1a492d8` applies the idle timeout to each
parsed `stream.next()`, treats the bridge's generic `response.failed` as
retryable, and can therefore run six sampling attempts at the default stream
retry setting. The official [configuration precedence](https://learn.chatgpt.com/docs/config-file/config-basic)
also confirms that global `config.toml` is not necessarily the effective layer.

The previous draft of this change copied the hidden Claude margin into Codex and
added another fixed 60-minute fallback. That draft is rejected and fully
superseded by this design.

## Goals / Non-Goals

**Goals:**

- Make every printed value traceable to an exact bridge key, client key, or
  named/source-confirmed built-in default while formatting durations for people.
- Separate configured client duration from the client interpretation (default,
  floor, cap) and from bridge runtime behavior.
- Distinguish per-send, per-request-attempt, per-stream-attempt, per-event-gap,
  and whole-turn scopes.
- Preserve user-selected client timeout and retry values byte-for-byte when a
  bridge connection command runs.
- State that Codex calculations use global `~/.codex/config.toml` and exclude
  project/profile/CLI overrides; apply the analogous caveat to Claude Code.
- Correct retry, first-downstream-event, keepalive, equality-race, and buffered
  response reporting without changing their runtime behavior in this change.
- Verify the rendered facts through real Claude Code and Codex clients.

**Non-Goals:**

- Choosing timeout values for the user or adding another safety margin/fallback.
- Adding a total turn deadline or changing any client/bridge retry count.
- Resolving Codex project/profile/CLI layers at bridge startup. One long-running
  bridge serves unrelated working directories and cannot know the future client's
  effective project context.
- Automatically deleting previously bridge-written Claude values; provenance
  cannot be distinguished from an identical value deliberately selected by the
  user.
- Adding timeout-writing CLI flags. If explicit editing is desired later, it must
  be a separate opt-in interface whose argument is written exactly.
- Implementing before the operator approves the exact startup output below.

## Decisions

### Client timeout and retry fields become user-owned

`config claude-code` becomes connection-only. It will write
`ANTHROPIC_BASE_URL` and fill `ANTHROPIC_AUTH_TOKEN` only when absent. It will
preserve `CLAUDE_STREAM_IDLE_TIMEOUT_MS`, `API_TIMEOUT_MS`,
`CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS`, watchdog toggles, retry settings,
`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`, `DISABLE_ERROR_REPORTING`, and
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` exactly as found. None of those values
will contribute to `config status` drift.

This deliberately separates previously bundled behavior: 1M-context assertion,
telemetry suppression, non-streaming fallback policy, and timeouts are client
choices, not prerequisites for reaching the bridge. A later opt-in command may
manage one of them, but the connection command will not.

`config codex` will surgically upsert only these bridge-owned facts:

- top-level `model_provider = "copilot-bridge"`;
- provider `name`, `base_url`, and `wire_api`;
- the command-auth fields required by the bridge installation.

All other fields in the existing provider table, including idle timeout, request
and stream retries, WebSocket timeout, query parameters, and headers, remain in
place with their trivia. The dry-run summary will explicitly say that client
timeout/retry fields were preserved.

Alternative considered: copy the bridge stream-idle number exactly into both
clients. Rejected because merely pointing a client at the bridge would still
silently take ownership of a user setting, and the same number has different
semantics on the three sides. The operator already knows how to set each client;
the bridge's responsibility is to expose the relationship truthfully.

### Print concise human durations, not storage units

The reader retains raw values internally for validation, but normal startup output
uses the shortest exact human duration. Examples:

```text
4m
15m, explicit
unset -> 5m*
configured 1m -> effective 5m (client floor)
```

For Claude Code, the reader will include both stream-idle and byte-idle keys,
rendered as `SSE event idle` and `SSE byte idle`, and will apply only
source-verified floor/cap rules for a named client version. If the
version or an enabling toggle cannot be known, the configured human duration is
printed but effective behavior is labelled version-dependent/unknown. It will not
silently reinterpret an invalid value as an ordinary absent default.

For Codex, the global provider's idle timeout is rendered as `SSE event idle` and
both retry fields are printed as their raw configured/default counts; startup does not expand retry counts into
total-attempt arithmetic. Genuine absence selects the official defaults. Zero is
displayed as explicit zero, not replaced by a default. Codex has no whole-request
timeout.

### The startup report is a global baseline, not an omniscient effective config

Every client heading names the inspected file and says `global only`. The footer
states that Claude repo/process-environment values and Codex
project/profile/CLI values are not included. The report never calls the global
value the client's definitive effective setting.

This limitation is intentional and approved. Resolving a project config would be
misleading because the bridge has no single client working directory at startup.

### Scope and retries remain explicit without console arithmetic

The bridge inventory uses precise scopes:

- response headers: per upstream send; each bridge transient retry or auth replay
  starts a fresh header timer;
- SSE idle: per gap between complete parsed upstream events;
- keepalive: downstream activity after the first upstream event, not a budget;
- buffered body after headers: no bridge bound;
- Claude request caps: per normal or after-stream-error HTTP request attempt;
- Codex request retries: additional HTTP attempts;
- Codex stream retries: additional sampling attempts.

Startup will not expand these mechanics into a long calculation section. One
plain-language footer states that timeouts apply per attempt, retries begin a new
attempt, and therefore no fixed whole-turn deadline can be inferred. Detailed
attempt arithmetic and phase composition stay in `docs/timeout-chain.md`.

### Keepalive authority is conditional and phase-specific

An active ping can make the client idle watchdog non-binding only after the first
upstream event and only while the ping reaches the downstream socket. It never
resets the bridge upstream-event gap. Whole-response buffering, an interval that
cannot precede the relevant deadline, or a client watchdog shorter than the ping
interval makes protection inactive.

If bridge and client idle deadlines are equal without effective keepalive, the
report says `race at <value>`; it does not choose the bridge by arithmetic tie.
Keepalive reachability will be based on the actual active detector set,
including tool-input validation, rather than one option flag.

### Validate only representability, never alter a positive bridge value

Positive bridge timeout and keepalive values must fit the timer APIs used by
`CancelAfter` and `Task.Delay`. Startup fails with an actionable configuration
error when they do not. Zero or negative retains the existing documented disabled
meaning. No clamp, margin, or substitute duration is introduced.

### Exact proposed startup rendering

The following is the canonical rendering for the reported upgrade state: shipped
bridge defaults and readable bridge-pointed global client configs whose timeout
and retry keys are absent. Paths and values are dynamic; labels, scopes, order,
caveats, and wording are fixed.

```text
Timeouts (observed configuration; startup does not rewrite values):
  Bridge — appsettings.json
    upstream response headers  4m / send attempt
    upstream SSE event gap     4m / parsed event gap
    downstream keepalive       15s, after first upstream event
    network retries            2
    buffered body              no limit after headers
  Claude Code — C:\Users\HuYao\.claude\settings.json (global only)
    SSE event idle             unset -> 5m*
    SSE byte idle              unset -> 5m*
    request timeout            unset -> normal 10m*; after stream error 5m*
    retries                    not visible at bridge startup
  Codex — C:\Users\HuYao\.codex\config.toml (global only)
    SSE event idle             unset -> 5m* / parsed event
    request retries            unset -> 4*
    stream retries             unset -> 5*
    whole request              no limit
  note: timeouts apply per attempt; a retry starts a new attempt, so there is no fixed whole-turn limit
  scope: global client configs only; project/profile/CLI/env overrides are not included
  * = client built-in default
```

When a client key is explicit, its line prints the exact human duration and known
effective interpretation, for example `15m, explicit`. No `manage:` line claims
that a bridge command will rewrite it. An unreadable file retains its section with
`unknown` facts and never suppresses the other sections. Detailed storage values,
header-plus-first-event, and retry-attempt calculations live in documentation, not
the startup console.

### Verification must test facts, not the old policy

Unit tests will be rewritten from the new contract and mutation-checked against
hidden arithmetic, destructive provider replacement, omitted retry values, false
turn-level labels, global-config overclaims, and buffering mistakes.

Real Codex verification will use isolated global and project configs to prove the
report uses only global values, then inspect Codex-owned `logs_2.sqlite` while a
short timeout causes retry. Real Claude verification will use distinct normal,
after-stream-error, stream-idle, and byte-idle values and confirm the client's own
transcript and behavior. Bridge HTTP 200 or unit tests alone are not acceptance
evidence.

## Risks / Trade-offs

- **Longer startup log** -> Keep one compact inventory with no calculated section;
  completeness is carried by source/scope labels and the detailed timeout reference.
- **Global values differ from a future client process** -> Label the scope on every
  client heading and repeat the exact invisible override classes in the footer.
- **Previously bridge-written timeout/1M/fallback values remain** -> Preserve them
  as user-owned on upgrade and print relevant timeout facts explicitly; automatic
  deletion would be another unapproved mutation.
- **Client behavior changes by version** -> Always print configured values; attach a
  version/source label to derived client-effective facts and use unknown when the
  installed version cannot be established.
- **No single turn deadline is satisfying** -> Report that truth. A fabricated
  minimum is easier to read but wrong once retries and phase resets exist.

## Migration Plan

1. Upgrade and restart the bridge. Startup reads but never writes either client.
2. Existing timeout values—including values previously written by older bridge
   releases—remain unchanged and are printed as explicit values.
3. Running either `config` command updates only connection/auth ownership and
   preserves all other client behavior fields. Dry-run names that preservation.
4. Operators edit timeout/retry values in the native client configuration if they
   want different behavior, then restart that client as required.
5. Rollback restores the prior binary; no migration deletes or rewrites user values.

## Open Questions

No implementation question remains. The exact startup rendering above is the
approval gate; product work stays blocked until the operator accepts it.
