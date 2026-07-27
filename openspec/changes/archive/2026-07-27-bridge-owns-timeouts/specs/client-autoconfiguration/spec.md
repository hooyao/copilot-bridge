## ADDED Requirements

### Requirement: Claude Code long-thinking timeout environment

The bridge SHALL force-write two Claude Code environment keys whenever it
configures Claude Code, so the client's own idle and request watchdogs outlast
the bridge's upstream inactivity budgets and the bridge remains the component
that decides when a stalled turn ends:

1. `CLAUDE_STREAM_IDLE_TIMEOUT_MS` — Claude Code applies an inactivity bound to a
   streaming turn. Because Copilot emits no keepalive while a model is thinking,
   a deep-thinking turn is legitimately silent for minutes, and the client
   default aborts a healthy stream. This key is the value that governs the
   client's streaming idle bound; the bridge SHALL write a value derived from its
   own configured budgets such that the client bound is not shorter than the
   bridge's.
2. `API_TIMEOUT_MS` — Claude Code applies a whole-request bound, and the same
   value also bounds each attempt of the non-streaming recovery request the
   client issues after a streaming failure. A recovery request produces no bytes
   until the model has finished, so this bound SHALL likewise be written to a
   value not shorter than the bridge's first-byte budget.

Both keys SHALL be force-written (overwriting any pre-existing value) so the pair
stays consistent with the bridge's configuration, in the same manner as the
1M-context keys. The bridge SHALL NOT write either key for Codex. The write SHALL
preserve all unrelated `env` keys.

Because Claude Code reads these values at process start, the written values take
effect on the client's next start; the bridge SHALL NOT claim they affect a
running client session.

#### Scenario: Claude Code config gains the long-thinking timeout keys

- **WHEN** `config claude-code` writes Claude Code settings
- **THEN** the `env` block contains both `CLAUDE_STREAM_IDLE_TIMEOUT_MS` and `API_TIMEOUT_MS`
- **AND** each written value is not shorter than the bridge budget it is derived from.

#### Scenario: Pre-existing timeout values are overwritten to the managed values

- **WHEN** the target `env` block already sets either key to some other value
- **THEN** `config claude-code` force-writes both to the bridge-derived managed values.

#### Scenario: Unrelated env keys survive the timeout write

- **WHEN** `config claude-code` runs against a `settings.json` `env` block holding unrelated keys
- **THEN** those keys are present and unchanged after the write, alongside the managed timeout keys.

#### Scenario: Codex config never carries the timeout keys

- **WHEN** `config codex` runs
- **THEN** the written `config.toml` contains neither `CLAUDE_STREAM_IDLE_TIMEOUT_MS` nor `API_TIMEOUT_MS`.

## MODIFIED Requirements

### Requirement: Overwrite policy preserves unmanaged values

The bridge SHALL force-write only the connection-defining and Claude-Code-managed
keys: the Claude Code `ANTHROPIC_BASE_URL`, the Claude Code 1M-context env keys
(`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`, `DISABLE_ERROR_REPORTING`), the
Claude Code long-thinking timeout env keys (`CLAUDE_STREAM_IDLE_TIMEOUT_MS`,
`API_TIMEOUT_MS`), and the Codex top-level `model_provider` pointer plus the
`[model_providers.copilot-bridge]` `base_url`. All other pre-existing keys the
bridge does not manage SHALL be preserved. The `ANTHROPIC_AUTH_TOKEN` placeholder
SHALL be filled with a `copilot-bridge` value only when absent, and an existing
value SHALL be preserved. A pre-existing rival provider block in the Codex file
SHALL be kept.

#### Scenario: Existing auth token is preserved

- **WHEN** `~/.claude/settings.json` already has an `ANTHROPIC_AUTH_TOKEN` value
- **THEN** the bridge leaves that value unchanged while force-writing its managed
  keys (`ANTHROPIC_BASE_URL`, the two 1M-context env keys, and the two
  long-thinking timeout env keys)

#### Scenario: Missing auth token is filled with a copilot-bridge value

- **WHEN** the target `env` block has no `ANTHROPIC_AUTH_TOKEN`
- **THEN** the bridge sets it to a `copilot-bridge` placeholder value with no
  competitor branding

#### Scenario: Unrelated env keys survive the 1M-context write

- **WHEN** `config claude-code` runs against a `settings.json` `env` block that
  holds an unrelated key
- **THEN** that key is present and unchanged after the write, alongside the
  managed base-URL, 1M-context, and long-thinking timeout keys

#### Scenario: Codex model and effort are preserved

- **WHEN** `config.toml` has top-level `model` and `model_reasoning_effort`
- **THEN** the bridge changes only `model_provider` and the
  `[model_providers.copilot-bridge]` block, leaving `model` and
  `model_reasoning_effort` unchanged

#### Scenario: Prior provider block is kept for easy switch-back

- **WHEN** `config.toml` already contains a different
  `[model_providers.<other>]` block
- **THEN** that block remains in the file after the bridge writes its own
  provider block

### Requirement: Config status reports current target and drift

The `config status` subcommand SHALL read the current client configs and report,
for each supported client, where it currently points and whether that differs
from what the current bridge configuration would produce. Drift SHALL include
port drift, a legacy Claude Code fallback-disable key that the bridge would
remove, a Claude Code config missing (or holding a non-managed value for) either
1M-context env key that the bridge would force-write, and a Claude Code config
missing (or holding a value other than the bridge-derived one for) either
long-thinking timeout env key. It SHALL modify no file.

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

#### Scenario: Reports drift for a missing or stale long-thinking timeout key

- **WHEN** a Claude Code config's base URL matches but its `env` block is missing
  `CLAUDE_STREAM_IDLE_TIMEOUT_MS` or `API_TIMEOUT_MS`, or holds a value other
  than the one the current bridge configuration would write (for example after
  the operator raised a `Pipeline:UpstreamTimeout` budget)
- **THEN** `config status` reports the client as drifted and shows both values

#### Scenario: Status never writes

- **WHEN** `config status` runs
- **THEN** no client config file is created or modified

#### Scenario: Status tolerates a malformed or oddly-typed file

- **WHEN** `config status` reads a client file that is malformed (Codex TOML with
  syntax errors) or has an unexpected value type (a Claude Code env value that is a
  number/boolean instead of a string), or that cannot be read (locked/permission
  error)
- **THEN** `config status` reports that client as unreadable/​not-configured and
  continues reporting the other clients, rather than crashing
