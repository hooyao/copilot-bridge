## MODIFIED Requirements

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

## ADDED Requirements

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

## REMOVED Requirements

### Requirement: Non-streaming recovery remains enabled

**Reason**: The old requirement made a connection command delete a user-selected
Claude Code fallback policy. Connection setup now owns connection/auth facts only;
runtime support for a recovery request remains available without forcing the client
to issue one.

**Migration**: Existing presence or absence of
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` is preserved. Operators choose the
native Claude Code policy explicitly; no automatic migration rewrites it.

#### Scenario: Legacy fallback deletion is retired

- **WHEN** a connection command encounters an existing fallback preference
- **THEN** it preserves that preference instead of deleting it.

### Requirement: Overwrite policy preserves unmanaged values

**Reason**: Its earlier scenarios still treated first-party and timeout values as
bridge-managed writes. The replacement requirement narrows ownership to
connection/authentication and makes every behavior field user-owned.

**Migration**: Use `Connection commands preserve unmanaged values`; existing
behavior fields remain unchanged on the next connection command.

#### Scenario: Legacy managed behavior writes are retired

- **WHEN** either connection command runs
- **THEN** only its connection/authentication fields are changed.

### Requirement: Config status reports current target and drift

**Reason**: The earlier drift contract compared client behavior settings with
bridge-derived expectations. Status now reports only bridge-owned connection/auth
drift and renders user values as observations.

**Migration**: Use `Config status reports connection and authentication drift`;
edit behavior values in the native client configuration when desired.

#### Scenario: Legacy behavior drift is retired

- **WHEN** a user-owned timeout or retry differs from a bridge budget
- **THEN** status reports the value as an observation rather than drift.

### Requirement: Claude Code 1M-context environment

**Reason**: Connection setup no longer opts users into first-party/1M or telemetry
policy.

**Migration**: Use `Claude Code 1M-context settings remain user-owned`; existing
values and absence are preserved.

#### Scenario: Legacy automatic 1M opt-in is retired

- **WHEN** the first-party/telemetry keys are absent before connection setup
- **THEN** they remain absent afterward.

### Requirement: Claude Code long-thinking timeout environment

**Reason**: The bridge no longer derives client timeouts, adds a hidden margin, or
writes a fixed request cap.

**Migration**: Use `Claude Code timeout settings remain user-owned`; set native
Claude Code values explicitly and compare them with the startup inventory.

#### Scenario: Legacy timeout derivation is retired

- **WHEN** Claude timeout keys are absent before connection setup
- **THEN** no bridge margin, cap, or fallback value is written.
