## MODIFIED Requirements

### Requirement: Overwrite policy preserves unmanaged values

The bridge SHALL force-write only the connection-defining and Claude-Code-managed keys: the Claude Code `ANTHROPIC_BASE_URL`, the Claude Code 1M-context env keys (`_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL`, `DISABLE_ERROR_REPORTING`), the Claude Code long-thinking timeout env keys (`CLAUDE_STREAM_IDLE_TIMEOUT_MS`, `API_TIMEOUT_MS`), and the Codex top-level `model_provider` pointer plus the bridge-owned `[model_providers.copilot-bridge]` provider block and its nested command-auth block. The managed Codex provider fields SHALL include `name`, `base_url`, `wire_api`, and the command, arguments, timeout, and refresh policy needed to obtain a non-secret provider sentinel. All other pre-existing keys the bridge does not manage SHALL be preserved. The `ANTHROPIC_AUTH_TOKEN` placeholder SHALL be filled with a `copilot-bridge` value only when absent, and an existing value SHALL be preserved. A pre-existing rival provider block in the Codex file SHALL be kept. Existing user-level Codex model, effort, context-window, and auto-compaction overrides SHALL remain user-owned and unchanged.

#### Scenario: Existing auth token is preserved

- **WHEN** `~/.claude/settings.json` already has an `ANTHROPIC_AUTH_TOKEN` value
- **THEN** the bridge leaves that value unchanged while force-writing its managed keys (`ANTHROPIC_BASE_URL`, the two 1M-context env keys, and the two long-thinking timeout env keys)

#### Scenario: Missing auth token is filled with a copilot-bridge value

- **WHEN** the target `env` block has no `ANTHROPIC_AUTH_TOKEN`
- **THEN** the bridge sets it to a `copilot-bridge` placeholder value with no competitor branding

#### Scenario: Unrelated env keys survive the 1M-context write

- **WHEN** `config claude-code` runs against a `settings.json` `env` block that holds an unrelated key
- **THEN** that key is present and unchanged after the write, alongside the managed base-URL, 1M-context, and long-thinking timeout keys

#### Scenario: Codex model and effort are preserved

- **WHEN** `config.toml` has top-level `model`, `model_reasoning_effort`, `model_context_window`, or `model_auto_compact_token_limit`
- **THEN** the bridge changes only `model_provider` and its own provider/auth blocks, leaving those explicit user choices unchanged

#### Scenario: Codex provider gains discovery auth

- **WHEN** `config codex` writes the bridge provider
- **THEN** the provider declares command-backed auth that invokes this bridge installation's noninteractive sentinel command and sets the managed refresh policy
- **AND** no GitHub or Copilot credential is written

#### Scenario: Prior provider block is kept for easy switch-back

- **WHEN** `config.toml` already contains a different `[model_providers.<other>]` block
- **THEN** that block remains in the file after the bridge writes its own provider block

### Requirement: Config status reports current target and drift

The `config status` subcommand SHALL read the current client configs and report, for each supported client, where it currently points and whether that differs from what the current bridge configuration would produce. Drift SHALL include port drift; a Codex bridge provider missing or differing in any managed command-auth field; a legacy Claude Code fallback-disable key that the bridge would remove; a Claude Code config missing (or holding a non-managed value for) either 1M-context env key that the bridge would force-write; and a Claude Code config missing (or holding a value other than the bridge-derived one for) either long-thinking timeout env key. It SHALL modify no file.

#### Scenario: Reports matching configuration

- **WHEN** a client's config already points at the appsettings-derived endpoint and carries all of that client's managed keys, including Codex discovery auth when applicable
- **THEN** `config status` reports that client as configured and not drifted

#### Scenario: A non-bridge endpoint is reported as not pointed at bridge

- **WHEN** a Claude Code config sets `ANTHROPIC_BASE_URL` to an endpoint that is not a bridge route (does not carry the `/cc` path)
- **THEN** `config status` reports it as "not pointed at bridge", not as a drifted bridge config

#### Scenario: Reports drift when appsettings changed

- **WHEN** the client's stored base URL port differs from the current `Server:Port`
- **THEN** `config status` reports the client as drifted and shows both values

#### Scenario: Reports Codex discovery-auth drift

- **WHEN** the Codex provider base URL matches but its managed auth command, arguments, timeout, or refresh policy is absent or stale
- **THEN** `config status` reports Codex as drifted and identifies the mismatched discovery-auth fact without printing any credential

#### Scenario: Reports drift for a legacy fallback-disable key

- **WHEN** a Claude Code config's base URL still matches but its stored `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` key is present
- **THEN** `config status` reports the client as drifted

#### Scenario: Reports drift for a missing 1M-context env key

- **WHEN** a Claude Code config's base URL matches but its `env` block is missing `_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL` or `DISABLE_ERROR_REPORTING` (or holds a value other than the managed `"1"`)
- **THEN** `config status` reports the client as drifted

#### Scenario: Reports drift for a missing or stale long-thinking timeout key

- **WHEN** a Claude Code config's base URL matches but its `env` block is missing `CLAUDE_STREAM_IDLE_TIMEOUT_MS` or `API_TIMEOUT_MS`, or holds a value other than the one the current bridge configuration would write (for example after the operator raised a `Pipeline:UpstreamTimeout` budget)
- **THEN** `config status` reports the client as drifted and shows both values

#### Scenario: Status never writes

- **WHEN** `config status` runs
- **THEN** no client config file is created or modified

#### Scenario: Status tolerates a malformed or oddly-typed file

- **WHEN** `config status` reads a client file that is malformed (Codex TOML with syntax errors) or has an unexpected value type (a Claude Code env value that is a number/boolean instead of a string), or that cannot be read (locked/permission error)
- **THEN** `config status` reports that client as unreadable/not-configured and continues reporting the other clients, rather than crashing

## ADDED Requirements

### Requirement: Codex discovery token command is isolated and non-secret

The bridge SHALL expose a noninteractive command suitable for `model_providers.<id>.auth.command`. It SHALL print only a stable, non-secret provider sentinel, complete without starting the web host or reading the token store, and work for both a native executable installation and the supported JIT development invocation.

#### Scenario: Native installation prints sentinel

- **WHEN** Codex invokes the configured native bridge executable and discovery-token arguments
- **THEN** the command prints the sentinel, exits zero, and performs no network or token-store access

#### Scenario: JIT development config remains executable

- **WHEN** `config codex` is run through the .NET host rather than a native published executable
- **THEN** the written command and arguments preserve the required DLL invocation so Codex can obtain the same sentinel
