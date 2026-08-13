# Timeouts along the client → copilot-bridge → Copilot chain

This is the detailed reference behind the compact timeout inventory printed when
the bridge starts. It explains which clock applies to which phase and where every
value comes from.

The central rule is simple: **the bridge never chooses or rewrites a client
timeout**. `config claude-code` and `config codex` manage connection and
authentication fields only. Bridge budgets, Claude Code settings, and Codex
provider settings are independent operator choices.

## What startup reports

With the shipped bridge defaults and bridge-pointed global client files that omit
their timeout/retry keys, startup prints:

```text
Timeouts (observed configuration; startup does not rewrite values):
  Bridge — appsettings.json
    upstream response headers  4m / send attempt
    upstream SSE event gap     4m / parsed event gap
    downstream keepalive       15s, after first upstream event
    network retries            2
    buffered body              no limit after headers
  Claude Code — C:\Users\me\.claude\settings.json (global only)
    SSE event idle             unset -> 5m*
    SSE byte idle              unset -> 5m*
    request timeout            unset -> normal 10m*; after stream error 5m*
    retries                    not visible at bridge startup
  Codex — C:\Users\me\.codex\config.toml (global only)
    SSE event idle             unset -> 5m* / parsed event
    request retries            unset -> 4*
    stream retries             unset -> 5*
    whole request              no limit
  note: timeouts apply per attempt; a retry starts a new attempt, so there is no fixed whole-turn limit
  scope: global client configs only; project/profile/CLI/env overrides are not included
  * = client built-in default
```

Durations use concise exact units. Startup does not repeat storage values such as
`300000ms` when `5m` is exact. A present invalid value is reported as invalid; it
is never silently treated as an absent key.

## The phases are different clocks

| Phase | Bridge clock | Client clock | Reset / scope |
|---|---|---|---|
| Upstream response headers | `FirstByteTimeoutSeconds` | Claude request timeout may run concurrently; Codex has no whole-request cap | Per bridge send attempt; retry backoff is outside it |
| First upstream SSE event after headers | `StreamIdleTimeoutSeconds` | Client parsed-event and byte-idle watchdogs | No bridge ping is sent before this first event |
| Later upstream SSE event gaps | `StreamIdleTimeoutSeconds` | Client idle watchdogs, refreshed by delivered bridge pings | Bridge deadline resets only on a genuine parsed upstream event |
| True buffered body after headers | No bridge body bound | Claude request timeout where applicable; Codex has no whole-request cap | Header timeout is already disarmed |
| Client HTTP request attempt | No bridge whole-request cap | Claude `API_TIMEOUT_MS` mode; no Codex equivalent | A client retry starts another request attempt |
| Whole turn | No single bridge/client deadline | Retry layers can restart the clocks above | No fixed total can be inferred from this inventory |

The setting historically named `FirstByteTimeoutSeconds` actually ends when
**upstream response headers** arrive. It does not promise that a downstream client
has received its first event. On a streaming send, the first downstream event can
therefore require one header wait followed by one first-SSE-event gap.

If either relevant bridge phase is disabled (`<= 0`), that part of the composition
is unbounded. If both are positive and there is no retry, the bridge-side upper
bound to the first parsed event for one send is:

```text
response-header budget + first parsed-SSE-event-gap budget
```

That is a phase composition, not a whole-request or whole-turn timeout.

## Bridge values are exact

`Pipeline:UpstreamTimeout` is read once at startup:

- `FirstByteTimeoutSeconds` bounds upstream response headers per send attempt.
- `StreamIdleTimeoutSeconds` bounds each gap between complete parsed upstream SSE
  events.
- `KeepAliveIntervalSeconds` schedules downstream activity; it is not a timeout.
- A positive value is used exactly as configured. There is no margin, clamp, or
  fallback.
- A zero or negative value disables that timer.

The runtime validates every positive value before the server binds. Values larger
than the range accepted by the actual `CancelAfter` / `Task.Delay` paths fail
startup with the option name, raw value, supported range, and correction guidance.

The shared model-forwarding `HttpClient` has no coarse whole-request timeout.
After response headers, a non-streaming body can therefore wait indefinitely.

`Pipeline:UpstreamRetry:MaxRetries` is the bridge's count of additional transient
network retries before response headers. Each retry is a new send attempt with a
fresh header budget; backoff is outside that budget. An authentication refresh can
also replay a rejected send once. These conditional branches are why startup
prints the retry count instead of fabricating one total duration.

## Claude Code values

The bridge reads only global `~/.claude/settings.json`. It does not read repo
settings or the future Claude process environment.

The current interpretation was measured and reconfirmed against real Claude Code
`2.1.221`:

| Startup label | Global key | Confirmed 2.1.221 behavior |
|---|---|---|
| `SSE event idle` | `CLAUDE_STREAM_IDLE_TIMEOUT_MS` | Absent default/floor `5m` |
| `SSE byte idle` | `CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS` | Absent `5m`, or `3m` when first-party mode is selected; an explicit value is constrained to `10s..30m`; when absent, an explicit event-idle value is inherited |
| `request timeout` | `API_TIMEOUT_MS` | Absent normal request `10m`; absent after-stream-error request `5m`; one explicit value governs both modes |

Claude Code has both a parsed-event watchdog and a byte-level watchdog; the
shorter active one can end an attempt. Codex has the equivalent parsed-event layer
but no separate byte-idle setting.

Startup probes `claude --version` with a short best-effort timeout. It applies the
table above only to the verified version. If the executable is unavailable or the
installed version differs, startup keeps an explicit configured duration visible
but labels its effective interpretation version-dependent/unknown instead of
guessing a floor, cap, or default.

Claude retry behavior cannot be established from the inspected global file alone,
so startup says `not visible at bridge startup`. It does not convert an unknown
retry policy into a whole-turn maximum.

## Codex values

The bridge reads only the active bridge provider in global
`~/.codex/config.toml` (or `$CODEX_HOME/config.toml`). The provider must be selected
as `copilot-bridge`; its provider table must use `name = "copilot-bridge"`, point
to a `/codex` base URL, and use `wire_api = "responses"`.

The official OpenAI
[Configuration Reference](https://learn.chatgpt.com/docs/config-file/config-reference)
defines these provider defaults:

| Startup label | Provider key | Built-in default |
|---|---|---|
| `SSE event idle` | `stream_idle_timeout_ms` | `5m` |
| `request retries` | `request_max_retries` | `4` retries |
| `stream retries` | `stream_max_retries` | `5` retries |

An explicit zero is zero; it is not absence and is not replaced by a default.
Codex has no provider-wide whole-request timeout, so startup prints
`whole request no limit`.

The two retry counts have different scopes:

- request retries are additional HTTP request attempts for qualifying provider
  failures;
- stream retries are additional sampling attempts after qualifying streaming
  interruptions.

Both are conditional. Startup intentionally prints raw counts, not `up to N
attempts`, and does not multiply the nested retry layers into a misleading turn
deadline.

The official
[Config basics](https://learn.chatgpt.com/docs/config-file/config-basic)
lists general precedence as CLI overrides, trusted-project config, selected
profile, user config, system config, then built-in defaults. The current
configuration reference also documents machine-local restrictions for provider
and auth keys in project files. Either way, a long-running bridge cannot know the
working directory, profile, or CLI arguments of a future Codex process. Startup
therefore labels the inspected global file as a baseline, never the definitive
effective configuration of every future client.

## Keepalive is downstream activity, not extra time

Copilot can remain silent while a model is thinking. After the first genuine
upstream event, the bridge can inject and flush a complete ping event every
`KeepAliveIntervalSeconds` while upstream remains silent.

The invariants are:

- no ping precedes the first genuine upstream event;
- a ping can refresh client byte/event-idle watchdogs only if it reaches the
  downstream socket before their deadline;
- a ping never resets the bridge's upstream SSE event-gap deadline;
- whole-response buffering prevents pings from reaching the client while the body
  is being collected;
- an interval equal to or longer than the relevant deadline is not protective;
- equal unprotected client/bridge deadlines are a race, not a deterministic bridge
  win.

The bridge sends a complete data event rather than an SSE comment. Codex times the
next parsed event and an SSE parser can discard comment-only frames before that
wait observes them. Claude Code ignores the ping as content while its activity
watchdogs are refreshed.

## What the connection commands change

`config claude-code` changes only:

- `env.ANTHROPIC_BASE_URL`;
- absent `env.ANTHROPIC_AUTH_TOKEN` (filled with the non-secret bridge
  placeholder).

It preserves all timeout, retry, watchdog, first-party/1M, telemetry, and fallback
values, including values written by older bridge releases.

`config codex` changes only:

- top-level `model_provider`;
- bridge-provider `name`, `base_url`, and `wire_api`;
- nested command-auth `command`, `args`, `timeout_ms`, and
  `refresh_interval_ms`.

It preserves provider timeout, retry, transport, query, and header fields with
their TOML trivia. `config status` treats those behavioral fields as observations,
not bridge drift.

## Diagnosing a timeout

1. Read the startup inventory and note the source path and scope of each value.
2. Check the bridge request summary: `upstream_timeout=first_byte|stream_idle`
   identifies a bridge phase; `error=cancelled by client` means a client deadline
   or user cancellation ended the request first.
3. For a first-event failure, remember that keepalive has not started yet.
4. For a post-start failure, check whether pings could reach the client or whether
   an active detector required whole-response buffering.
5. Check the native client evidence. Codex retry/dispatch facts live in its own
   `logs_2.sqlite`; Claude Code behavior belongs in the real client transcript.
6. Edit the bridge and client settings independently. The bridge never adds
   undisclosed headroom or rewrites one from the other.
