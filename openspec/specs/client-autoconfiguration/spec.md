# client-autoconfiguration Specification

## Purpose

Define the isolated CLI workflow that safely points Claude Code and Codex at the
bridge while preserving unrelated client configuration, supporting dry runs and
status reporting, and deriving connection settings from the bridge configuration.
## Requirements
### Requirement: Config command family

The CLI SHALL expose `config claude-code`, `config codex`, and `config status`.
`config claude-code` SHALL accept `--scope global|repo`. `config codex` SHALL
intentionally target only the user/global `~/.codex/config.toml` baseline and
SHALL NOT accept `--scope`; this is a bridge-command limitation, not a claim that
Codex lacks project/profile/CLI configuration layers. All write subcommands SHALL
accept `--port <n>` and `--dry-run`.

The help/error text for `config codex` SHALL state that higher-precedence Codex
project/profile/CLI overrides are outside the command's scope and can supersede
the global provider for a particular client process.

#### Scenario: Claude Code global scope writes user settings

- **WHEN** the user runs `config claude-code --scope global`
- **THEN** the bridge updates only its connection-owned keys in global settings.

#### Scenario: Claude Code repo scope targets the local settings file

- **WHEN** the user runs `config claude-code --scope repo`
- **THEN** the bridge targets `./.claude/settings.local.json`, never the shared repo settings file.

#### Scenario: Codex command is global-only

- **WHEN** the user views help or attempts a scoped Codex configuration
- **THEN** the CLI says this command writes only global `~/.codex/config.toml`
- **AND** warns that project/profile/CLI overrides can supersede it.

#### Scenario: Codex rejects a scope flag

- **WHEN** the user runs `config codex --scope repo`
- **THEN** parsing fails non-zero before any file is written
- **AND** the command help identifies the global-only baseline and the higher-precedence override boundary.

#### Scenario: Unsupported scope is rejected before any write

- **WHEN** a subcommand receives a scope outside its declared support
- **THEN** it fails non-zero and writes no file.

#### Scenario: Dry-run support remains available

- **WHEN** either connection command is invoked with `--dry-run`
- **THEN** it prints the exact planned connection/auth edits and performs no write.

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

### Requirement: Non-streaming recovery policy remains user-owned

The bridge SHALL support a Claude Code non-streaming recovery request when the
client chooses to issue one, including translation of a successful cross-routed
Responses object to Anthropic response IR before response detectors run. The
connection command SHALL NOT choose that client policy:
`config claude-code` SHALL preserve
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` exactly as found, including absence.
The bridge SHALL NOT write this Claude-specific key into Codex configuration.

#### Scenario: Existing fallback preference is preserved

- **WHEN** Claude Code settings contain `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK`
- **AND** the operator runs `config claude-code`
- **THEN** the key and its value remain unchanged.

#### Scenario: Absent fallback preference remains absent

- **WHEN** Claude Code settings omit `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK`
- **THEN** `config claude-code` does not add it.

#### Scenario: Enabled client recovery remains supported by the bridge

- **WHEN** Claude Code issues a non-streaming fallback after a streaming error
- **THEN** the bridge accepts and translates that request as before
- **AND** configuration preservation does not disable the runtime recovery path.

#### Scenario: Codex config never carries the Claude recovery switch

- **WHEN** `config codex` runs
- **THEN** the written `config.toml` contains no newly introduced
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` key.

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

### Requirement: Codex discovery token command is isolated and non-secret

The bridge SHALL expose a noninteractive command suitable for `model_providers.<id>.auth.command`. It SHALL print only a stable, non-secret provider sentinel, complete without starting the web host or reading the token store, and work for both a native executable installation and the supported JIT development invocation.

#### Scenario: Native installation prints sentinel

- **WHEN** Codex invokes the configured native bridge executable and discovery-token arguments
- **THEN** the command prints the sentinel, exits zero, and performs no network or token-store access

#### Scenario: JIT development config remains executable

- **WHEN** `config codex` is run through the .NET host rather than a native published executable
- **THEN** the written command and arguments preserve the required DLL invocation so Codex can obtain the same sentinel

### Requirement: Codex provider behavioral fields are preserved

The bridge SHALL treat timeout, retry, transport, query, and header fields inside
`[model_providers.copilot-bridge]` as user-owned. `config codex` SHALL surgically
upsert only the provider connection identity (`name`, `base_url`, `wire_api`) and
the nested command-auth fields required by this bridge installation. It SHALL NOT
replace the whole provider table.

The preserved fields include, but are not limited to,
`stream_idle_timeout_ms`, `request_max_retries`, `stream_max_retries`,
`websocket_connect_timeout_ms`, `supports_websockets`, `query_params`,
`http_headers`, and `env_http_headers`. Dry-run and apply summaries SHALL state
that behavioral provider fields are preserved and SHALL name every field the
command does change.

#### Scenario: Existing Codex timeout and retries survive configuration

- **WHEN** the bridge provider contains explicit idle timeout, request retry, and stream retry values
- **AND** the operator runs `config codex`
- **THEN** all three values and their formatting/comments remain unchanged
- **AND** only connection/auth fields are updated.

#### Scenario: Missing Codex timeout remains missing

- **WHEN** the bridge provider omits `stream_idle_timeout_ms`
- **AND** the operator runs `config codex`
- **THEN** the key remains absent and Codex continues to use its built-in default
- **AND** the bridge does not derive a value from `StreamIdleTimeoutSeconds`.

#### Scenario: Dry run discloses ownership boundary

- **WHEN** the operator runs `config codex --dry-run`
- **THEN** the summary identifies the connection/auth fields that would change
- **AND** states that provider timeout and retry fields will be preserved.

#### Scenario: Conflicting auth representation is refused safely

- **WHEN** the existing provider expresses `auth` as an inline table or dotted keys
- **THEN** the connection command fails before writing instead of appending a conflicting explicit table
- **AND** the error identifies `[model_providers.copilot-bridge.auth]` as the supported conversion target.

### Requirement: Connection commands preserve unmanaged values

The connection commands SHALL own connection/authentication facts only.
`config claude-code` SHALL write `ANTHROPIC_BASE_URL` and SHALL fill the required
`ANTHROPIC_AUTH_TOKEN` placeholder only when absent. It SHALL preserve all other
Claude Code environment and settings values, including timeout, retry, watchdog,
first-party/1M, telemetry, and fallback keys.

`config codex` SHALL write the top-level `model_provider` pointer and surgically
upsert the bridge provider's `name`, `base_url`, `wire_api`, plus the command,
arguments, timeout, and refresh policy needed for non-secret provider auth. Every
other value in that provider table, every rival provider, and every unrelated
top-level/table value SHALL be preserved.

#### Scenario: Existing auth token is preserved

- **WHEN** `~/.claude/settings.json` already has an `ANTHROPIC_AUTH_TOKEN` value
- **THEN** the bridge leaves that value unchanged while updating the base URL.

#### Scenario: Missing auth token is filled with a bridge placeholder

- **WHEN** the target `env` block has no `ANTHROPIC_AUTH_TOKEN`
- **THEN** the bridge sets only that required placeholder and the base URL
- **AND** does not add unrelated behavioral keys.

#### Scenario: Claude behavioral keys survive byte-for-byte by value

- **WHEN** Claude Code settings contain timeout, watchdog, retry, 1M, telemetry, or fallback keys
- **THEN** every such key retains its original value after `config claude-code`.

#### Scenario: Codex model and provider behavior are preserved

- **WHEN** `config.toml` contains top-level model/effort/context choices and behavioral fields inside the bridge provider
- **THEN** those values remain unchanged after `config codex`.

#### Scenario: Codex provider gains discovery auth

- **WHEN** `config codex` writes the bridge provider
- **THEN** it upserts command-backed auth for this installation without writing a GitHub/Copilot credential
- **AND** it does not rebuild unrelated provider content.

#### Scenario: Prior provider block is kept for easy switch-back

- **WHEN** `config.toml` contains a different `[model_providers.<other>]` block
- **THEN** that block remains byte-identical after the bridge writes its own connection facts.

### Requirement: Config status reports connection and authentication drift

The `config status` subcommand SHALL report whether each supported client points
at the appsettings-derived bridge endpoint and whether bridge-owned connection or
auth fields have drifted. Drift SHALL include port/base-URL drift and a Codex
bridge provider missing or differing in managed identity/command-auth fields.

User-owned timeout, retry, watchdog, first-party/1M, telemetry, and fallback
values SHALL be displayed as observations where useful but SHALL NOT be compared
to a bridge-derived expected value and SHALL NOT make connection status drifted.
`config status` SHALL modify no file.

#### Scenario: Reports matching connection configuration

- **WHEN** a client points at the appsettings-derived endpoint and its bridge-owned auth fields match
- **THEN** `config status` reports it as configured even when its user timeout values differ from bridge inactivity budgets.

#### Scenario: A non-bridge endpoint is reported as not pointed at bridge

- **WHEN** a Claude Code base URL does not carry the `/cc` bridge path
- **THEN** status reports `not pointed at bridge`, not timeout drift.

#### Scenario: Reports drift when appsettings port changed

- **WHEN** the stored bridge base URL port differs from `Server:Port`
- **THEN** status reports drift and shows both URLs.

#### Scenario: Reports Codex discovery-auth drift

- **WHEN** the Codex provider base URL matches but its bridge-owned auth command, arguments, timeout, or refresh policy is absent or stale
- **THEN** status identifies that auth drift without printing credentials.

#### Scenario: User timeout difference is not drift

- **WHEN** Claude or Codex contains any explicit timeout/retry value
- **THEN** status preserves and reports that value without naming an expected bridge-derived replacement
- **AND** the value alone does not change connection status to `DRIFTED`.

#### Scenario: Status never writes

- **WHEN** `config status` runs
- **THEN** no client file is created or modified.

#### Scenario: Status tolerates malformed or unreadable files

- **WHEN** one client file is malformed, oddly typed, locked, or permission-denied
- **THEN** status reports that client as unreadable/not-configured and continues with the other client.

### Requirement: Claude Code 1M-context settings remain user-owned

The connection command SHALL NOT opt the user into Claude Code first-party/1M
behavior or telemetry policy. `config claude-code` SHALL preserve
`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` and `DISABLE_ERROR_REPORTING` exactly
as found and SHALL leave both absent when absent. Documentation MAY explain how
users can set the pair themselves, including the timeout and telemetry side
effects, but connection setup SHALL NOT make that choice.

#### Scenario: Existing 1M-context pair is preserved

- **WHEN** either 1M/telemetry key already exists
- **THEN** `config claude-code` leaves its value unchanged.

#### Scenario: Missing 1M-context pair remains absent

- **WHEN** both keys are absent
- **THEN** `config claude-code` adds neither key.

#### Scenario: Codex config never carries the Claude keys

- **WHEN** `config codex` runs
- **THEN** it introduces neither Claude-specific key.

### Requirement: Claude Code timeout settings remain user-owned

Claude Code timeout values SHALL be user-owned. `config claude-code` SHALL NOT
add, derive, clamp, or overwrite `CLAUDE_STREAM_IDLE_TIMEOUT_MS`,
`CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS`, `API_TIMEOUT_MS`, watchdog toggles, or retry
settings. In particular, it SHALL NOT add a 300-second margin, convert a disabled
bridge budget to 30 minutes, or force a fixed 60-minute request timeout.

The startup/status readers MAY interpret a stored value using a source-confirmed
client default/floor/cap, but startup SHALL display concise configured/effective
human durations and SHALL NOT write the interpretation back. Because these values
take effect in the client process, the bridge SHALL NOT claim that changing its
own appsettings changes a running or future client.

#### Scenario: Existing timeout values are preserved

- **WHEN** any Claude timeout value is present before `config claude-code`
- **THEN** the exact value remains after the command.

#### Scenario: Missing timeout values remain absent

- **WHEN** Claude timeout keys are absent
- **THEN** the connection command adds no timeout key and the client retains its built-in behavior.

#### Scenario: Changing bridge timeout creates no client drift

- **WHEN** the operator changes `StreamIdleTimeoutSeconds` or `FirstByteTimeoutSeconds`
- **THEN** status does not demand a client timeout rewrite
- **AND** startup shows both independent values side-by-side.

#### Scenario: Codex config never carries Claude timeout keys

- **WHEN** `config codex` runs
- **THEN** it introduces neither Claude timeout environment key.
