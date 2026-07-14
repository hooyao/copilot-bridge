# client-autoconfiguration Specification

## Purpose

Define the isolated CLI workflow that safely points Claude Code and Codex at the
bridge while preserving unrelated client configuration, supporting dry runs and
status reporting, and deriving connection settings from the bridge configuration.

## Requirements
### Requirement: Config command family

The CLI SHALL expose a `config` command group with subcommands that write
bridge-pointing configuration into supported clients' real config files. The
group SHALL include `config claude-code`, `config codex`, and `config status`.
The `config claude-code` subcommand SHALL accept `--scope global|repo`. The
`config codex` subcommand SHALL NOT accept `--scope` (Codex honors global scope
only). All write subcommands SHALL accept `--port <n>` and `--dry-run`.

#### Scenario: Claude Code global scope writes user settings

- **WHEN** the user runs `config claude-code --scope global`
- **THEN** the bridge writes only its own keys into the global
  `~/.claude/settings.json` `env` block and exits 0

#### Scenario: Claude Code repo scope targets the local settings file

- **WHEN** the user runs `config claude-code --scope repo`
- **THEN** the bridge writes to `./.claude/settings.local.json` (the personal,
  gitignored file) and never to the shared `./.claude/settings.json`

#### Scenario: Codex rejects a scope flag

- **WHEN** the user runs `config codex --scope repo`
- **THEN** the command fails with a non-zero exit and an error stating Codex
  supports global scope only, and no file is written

#### Scenario: Unsupported scope is rejected before any write

- **WHEN** a subcommand is invoked with a scope not in the configurator's
  declared supported scopes
- **THEN** the command surfaces the error and performs no filesystem write

### Requirement: Connection facts derived from appsettings

The bridge SHALL derive the connection URL from `appsettings.json` `Server:Port`
(default 8765), overridable by `--port`. The written Claude Code base URL SHALL
be `http://localhost:{port}/cc` and the Codex `base_url` SHALL be
`http://localhost:{port}/codex`. The derivation SHALL use the same strongly-typed
options binding the server uses, not ad-hoc key reads.

#### Scenario: Port comes from appsettings by default

- **WHEN** `Server:Port` is 8765 and no `--port` is given
- **THEN** the Claude Code base URL written is `http://localhost:8765/cc` and the
  Codex base URL is `http://localhost:8765/codex`

#### Scenario: CLI port overrides appsettings

- **WHEN** `Server:Port` is 8765 and the user passes `--port 18765`
- **THEN** the written base URL uses port 18765

#### Scenario: Non-default appsettings port is honored

- **WHEN** `Server:Port` is 9000 and no `--port` is given
- **THEN** the written base URL uses port 9000

### Requirement: Non-streaming recovery remains enabled

The bridge SHALL remove the Claude Code environment key
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` whenever it configures Claude Code,
regardless of detector settings. A mid-stream Anthropic SSE error causes current
Claude Code to recover by issuing a non-streaming request; disabling that request
turns the error into a terminal client failure. The bridge SHALL support the
recovery request by translating a successful cross-routed Responses object to the
Anthropic response IR before response detectors run. The bridge SHALL NOT write
this key for Codex.

#### Scenario: Legacy disable switch is removed when detectors preserve streams

- **WHEN** any response detector is enabled with stream-preserving abort behavior
- **AND** the Claude Code settings already contain `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1`
- **THEN** `config claude-code` removes that key
- **AND** leaves non-streaming recovery enabled.

#### Scenario: Legacy disable switch is removed without stream-preserving detectors

- **WHEN** no response detector can inject a mid-stream error
- **AND** the Claude Code settings already contain `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK`
- **THEN** `config claude-code` removes that key.

#### Scenario: Codex config never carries the Claude recovery switch

- **WHEN** `config codex` runs under any detector configuration
- **THEN** the written `config.toml` contains no
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` key.

### Requirement: Overwrite policy preserves unmanaged values

The bridge SHALL force-write only the connection-defining keys: the Claude Code
`ANTHROPIC_BASE_URL` and the Codex top-level `model_provider` pointer plus the
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

### Requirement: Surgical merge preserves all unrelated content

The bridge SHALL preserve every key, table, comment, whitespace region, and
literal that it does not manage. Claude Code JSON SHALL be edited via a
DOM-preserving node model. Codex TOML SHALL be edited via a trivia-preserving
syntax tree, not a model round-trip that discards comments or formatting. When an
existing non-empty file cannot be parsed safely (Claude Code: not valid JSON, or
valid JSON but not an object; Codex: TOML syntax errors), the bridge SHALL refuse
to write and abort with an error rather than overwrite it — merging would silently
discard the user's unrelated content. The read path used by `config status` SHALL
remain tolerant of such a file and report it instead of crashing.

#### Scenario: Dense Codex file keeps unrelated tables byte-for-byte

- **WHEN** `config codex` runs against a `config.toml` containing unrelated
  tables such as marketplaces, plugins, an mcp_servers env table with
  single-quoted path literals, a multi-line notify array, and OS-specific
  sections
- **THEN** all of those unrelated tables, comments, and literals are byte-for-byte
  identical after the write

#### Scenario: Unrelated Claude Code settings survive

- **WHEN** `config claude-code` runs against a `settings.json` that also holds
  statusLine, enabledPlugins, and effortLevel keys
- **THEN** those keys are present and unchanged after the write

#### Scenario: Special characters and non-ASCII in unrelated values are byte-preserved

- **WHEN** `config claude-code` runs against a `settings.json` whose unrelated
  values contain characters the default JSON encoder would escape (`&`, `<`, `>`,
  `+`) or non-ASCII text
- **THEN** those values are written back verbatim (not `\uXXXX`-escaped)

#### Scenario: Unparseable JSON is refused, not overwritten

- **WHEN** `config claude-code` runs against a non-empty `settings.json` that is
  not valid JSON (for example it contains a `//` comment) or is valid JSON that is
  not an object
- **THEN** the command aborts with an error and the file on disk is left unchanged

#### Scenario: Malformed TOML is refused, not corrupted

- **WHEN** `config codex` runs against a non-empty `config.toml` that has TOML
  syntax errors
- **THEN** the command aborts with an error and the file on disk is left unchanged

### Requirement: Safe and idempotent writes

The bridge SHALL back up the target file before overwriting it and SHALL be
idempotent: running the same command twice against the same inputs SHALL produce
a byte-identical file on the second run. With `--dry-run` the bridge SHALL print
the planned result and write nothing.

#### Scenario: Backup is created before writing

- **WHEN** a write subcommand modifies an existing target file
- **THEN** a backup copy of the prior file content exists after the command

#### Scenario: Re-running produces identical bytes

- **WHEN** a write subcommand runs twice with unchanged inputs
- **THEN** the target file after the second run is byte-identical to after the
  first run

#### Scenario: Dry run writes nothing

- **WHEN** a write subcommand runs with `--dry-run`
- **THEN** the bridge prints the planned configuration and the target file on
  disk is unchanged

### Requirement: Config status reports current target and drift

The `config status` subcommand SHALL read the current client configs and report,
for each supported client, where it currently points and whether that differs
from what the current bridge configuration would produce (including port drift
or a legacy Claude Code fallback-disable key that the bridge would remove). It
SHALL modify no file.

#### Scenario: Reports matching configuration

- **WHEN** a client's config already points at the appsettings-derived endpoint
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

### Requirement: Isolation from the proxy server startup path

The `config` command SHALL run in a composition root that boots no web host,
starts no Kestrel listener, and runs no hosted service. Its dependency graph
SHALL NOT include the request pipeline, auth service, or Copilot client. Adding a
new client configurator SHALL require only a new configurator implementation and
its registration, with no change to the proxy server startup code.

#### Scenario: Config runs without binding a port

- **WHEN** any `config` subcommand runs while the bridge's default port is
  already in use by a running server
- **THEN** the command completes without a port-binding failure because it starts
  no listener

#### Scenario: Config graph excludes runtime services

- **WHEN** the config composition root is built
- **THEN** it does not resolve or construct the request pipeline, the auth
  service, the Copilot client, or any hosted service
