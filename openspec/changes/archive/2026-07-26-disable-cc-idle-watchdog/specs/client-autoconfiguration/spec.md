## ADDED Requirements

### Requirement: Claude Code streaming idle watchdog is disabled

The bridge SHALL force-write the Claude Code environment key
`API_FORCE_IDLE_TIMEOUT` = `"0"` whenever it configures Claude Code, disabling
Claude Code's client-side idle abort of a streaming model response.

Rationale — the watchdog is active for bridge users and cannot distinguish a
working stream from a dead one on this backend:

1. Claude Code aborts a streaming response after 5 minutes with no bytes. That
   timeout is inactive on direct Anthropic and AWS connections and **active on
   every other provider**, so it is armed by default for every bridge user.
2. Copilot emits no SSE `ping` keepalive. During extended thinking the connection
   is genuinely byte-free, so a model that is still working is indistinguishable
   on the wire from a stalled upstream, and the watchdog aborts turns that were
   progressing normally.
3. The bridge already owns this bound via
   `Pipeline:UpstreamTimeout:StreamIdleTimeoutSeconds`, which is operator-tunable
   and has a defined recovery action. A second, uncoordinated, non-tunable timer
   in the client can only abort turns the bridge intended to keep alive.

The key SHALL be force-written (overwriting any pre-existing value) so the bridge
is unambiguously the sole idle actor. The bridge SHALL NOT write this key for
Codex. The write SHALL preserve all unrelated `env` keys.

This requirement does not extend the maximum length of a turn: the Copilot
backend independently closes a stream that has produced no token after roughly
300 seconds (see `docs/copilot-stream-cap.md`). Disabling the client watchdog
removes a redundant abort; it does not and cannot defeat that server-side cap.

#### Scenario: Claude Code config gains the idle-watchdog key

- **WHEN** `config claude-code` writes Claude Code settings
- **THEN** the `env` block contains `API_FORCE_IDLE_TIMEOUT` = `"0"`

#### Scenario: A pre-existing value is overwritten to the managed value

- **WHEN** the target `env` block already sets `API_FORCE_IDLE_TIMEOUT` to some
  other value (for example `"1"`, which forces the watchdog on for every provider)
- **THEN** `config claude-code` force-writes it to `"0"`

#### Scenario: Codex config never carries the idle-watchdog key

- **WHEN** `config codex` runs
- **THEN** the written `config.toml` contains no `API_FORCE_IDLE_TIMEOUT` key

#### Scenario: Unrelated env keys survive the write

- **WHEN** the target `env` block holds keys the bridge does not manage
- **THEN** those keys and their values are unchanged after `config claude-code`
  writes `API_FORCE_IDLE_TIMEOUT`

## MODIFIED Requirements

### Requirement: Overwrite policy preserves unmanaged values

The bridge SHALL force-write only the connection-defining and Claude-Code-managed
keys: the Claude Code `ANTHROPIC_BASE_URL`, the Claude Code 1M-context env keys
(`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`, `DISABLE_ERROR_REPORTING`), the
Claude Code idle-watchdog key (`API_FORCE_IDLE_TIMEOUT`), and the Codex top-level
`model_provider` pointer plus the `[model_providers.copilot-bridge]` `base_url`.
All other pre-existing keys the bridge does not manage SHALL be preserved. The
`ANTHROPIC_AUTH_TOKEN` placeholder SHALL be filled with a `copilot-bridge` value
only when absent, and an existing value SHALL be preserved. A pre-existing rival
provider block in the Codex file SHALL be kept.

#### Scenario: Existing auth token is preserved

- **WHEN** `~/.claude/settings.json` already has an `ANTHROPIC_AUTH_TOKEN` value
- **THEN** the bridge leaves that value unchanged while force-writing its managed
  keys (`ANTHROPIC_BASE_URL`, the two 1M-context env keys, and
  `API_FORCE_IDLE_TIMEOUT`)

#### Scenario: Missing auth token is filled with a copilot-bridge value

- **WHEN** the Claude Code settings have no `ANTHROPIC_AUTH_TOKEN`
- **THEN** the bridge writes the `copilot-bridge` placeholder value

### Requirement: Config status reports current target and drift

The `config status` subcommand SHALL read the current client configs and report,
for each supported client, where it currently points and whether that differs
from what the current bridge configuration would produce. Drift SHALL include
port drift, a legacy Claude Code fallback-disable key that the bridge would
remove, a Claude Code config missing (or holding a non-managed value for) either
1M-context env key that the bridge would force-write, and a Claude Code config
missing (or holding a value other than `"0"` for) `API_FORCE_IDLE_TIMEOUT`. It
SHALL modify no file.

#### Scenario: Reports matching configuration

- **WHEN** a client's config already points at the appsettings-derived endpoint
  and carries the bridge's managed env keys
- **THEN** `config status` reports that client as configured and not drifted

#### Scenario: A non-bridge endpoint is reported as not pointed at bridge

- **WHEN** a Claude Code config sets `ANTHROPIC_BASE_URL` to an endpoint that is
  not a bridge route (does not carry the `/cc` path)
- **THEN** `config status` reports it as "not pointed at bridge", not as a drifted
  bridge config

#### Scenario: Reports drift when appsettings changed

- **WHEN** the client's stored base URL port differs from the current
  `Server:Port`
- **THEN** `config status` reports the client as drifted and shows both values

#### Scenario: Reports drift for a legacy fallback-disable key

- **WHEN** a Claude Code config's base URL still matches but its stored
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` key is present
- **THEN** `config status` reports the client as drifted

#### Scenario: Reports drift for a missing 1M-context env key

- **WHEN** a Claude Code config's base URL matches but its `env` block is missing
  `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` or `DISABLE_ERROR_REPORTING` (or
  holds a value other than the managed `"1"`)
- **THEN** `config status` reports the client as drifted

#### Scenario: Reports drift for a missing idle-watchdog key

- **WHEN** a Claude Code config's base URL matches but its `env` block is missing
  `API_FORCE_IDLE_TIMEOUT` or holds a value other than the managed `"0"`
- **THEN** `config status` reports the client as drifted

This is the condition of every user configured before this change, which is
precisely the population still running with the watchdog armed.

#### Scenario: Status never writes

- **WHEN** `config status` runs
- **THEN** no client config file is created or modified
