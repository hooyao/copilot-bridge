## ADDED Requirements

### Requirement: Claude Code 1M-context environment

The bridge SHALL force-write two Claude Code environment keys whenever it
configures Claude Code, so a bridge user (whose base URL is not
`api.anthropic.com`) receives a 1,000,000-token context window on models whose
Copilot backend natively serves 1M (opus-4.6/4.7/4.8, sonnet-4.6, sonnet-5), and
so that window survives `--resume`:

1. `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` = `"1"` — Claude Code decides the
   context window from a bundled model-capability table gated on the request
   being first-party; a custom base URL fails that gate and falls back to
   200,000. Asserting first-party lets the native-1M capability apply. This is a
   client-side signal only; Claude Code's inference traffic continues to target
   the configured bridge base URL.
2. `DISABLE_ERROR_REPORTING` = `"1"` — asserting first-party also enables Claude
   Code's error-reporting telemetry, which is otherwise off for a custom base
   URL. The bridge SHALL disable it so enabling the 1M window does not silently
   turn on that telemetry channel.

Both keys SHALL be force-written (overwriting any pre-existing value) so the pair
is always consistent. The bridge SHALL NOT write either key for Codex. The write
SHALL preserve all unrelated `env` keys.

#### Scenario: Claude Code config gains the 1M-context env keys

- **WHEN** `config claude-code` writes Claude Code settings
- **THEN** the `env` block contains `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` =
  `"1"` and `DISABLE_ERROR_REPORTING` = `"1"`

#### Scenario: Pre-existing values are overwritten to the managed value

- **WHEN** the target `env` block already sets either key to some other value
- **THEN** `config claude-code` force-writes both to `"1"`

#### Scenario: Codex config never carries the 1M-context env keys

- **WHEN** `config codex` runs
- **THEN** the written `config.toml` contains neither
  `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` nor `DISABLE_ERROR_REPORTING`

## MODIFIED Requirements

### Requirement: Overwrite policy preserves unmanaged values

The bridge SHALL force-write only the connection-defining and Claude-Code-managed
keys: the Claude Code `ANTHROPIC_BASE_URL`, the Claude Code 1M-context env keys
(`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`, `DISABLE_ERROR_REPORTING`), and the
Codex top-level `model_provider` pointer plus the
`[model_providers.copilot-bridge]` `base_url`. All other pre-existing keys the
bridge does not manage SHALL be preserved. The `ANTHROPIC_AUTH_TOKEN` placeholder
SHALL be filled with a `copilot-bridge` value only when absent, and an existing
value SHALL be preserved. A pre-existing rival provider block in the Codex file
SHALL be kept.

#### Scenario: Existing auth token is preserved

- **WHEN** `~/.claude/settings.json` already has an `ANTHROPIC_AUTH_TOKEN` value
- **THEN** the bridge leaves that value unchanged and only updates
  `ANTHROPIC_BASE_URL`

#### Scenario: Missing auth token is filled with a copilot-bridge value

- **WHEN** the target `env` block has no `ANTHROPIC_AUTH_TOKEN`
- **THEN** the bridge sets it to a `copilot-bridge` placeholder value with no
  competitor branding

#### Scenario: Unrelated env keys survive the 1M-context write

- **WHEN** `config claude-code` runs against a `settings.json` `env` block that
  holds an unrelated key
- **THEN** that key is present and unchanged after the write, alongside the
  managed base-URL and 1M-context keys

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
remove, and a Claude Code config missing (or holding a non-managed value for)
either 1M-context env key that the bridge would force-write. It SHALL modify no
file.

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
