## REMOVED Requirements

### Requirement: Non-streaming fallback env derived from detector options

The bridge SHALL derive the Claude Code non-streaming fallback env from the
detector options. When any response detector can inject an error mid-stream — the
ResponseLeakGuard or ToolInputValidation detector with Enabled true and
PreserveStream true, or the RunawayGuard detector with Enabled true (it has no
PreserveStream toggle and always aborts mid-stream) — the bridge SHALL set the env
key CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK to the value 1. When no detector
meets that condition, the bridge SHALL remove that env key so the written config
reflects the current appsettings state. The bridge SHALL NOT write that env key
for Codex.

#### Scenario: PreserveStream on one detector sets the env to 1

- **WHEN** ResponseLeakGuard has `Enabled=true` and `PreserveStream=true`
- **THEN** the Claude Code `env` contains
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` = `"1"`

#### Scenario: RunawayGuard enabled alone sets the env to 1

- **WHEN** ResponseLeakGuard and ToolInputValidation are both disabled but
  RunawayGuard has `Enabled=true`
- **THEN** the Claude Code `env` contains
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` = `"1"`

#### Scenario: Neither detector preserves the stream removes the env

- **WHEN** ResponseLeakGuard and ToolInputValidation have either `Enabled=false`
  or `PreserveStream=false`, and RunawayGuard has `Enabled=false`
- **AND** the target file already contains
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK`
- **THEN** the bridge removes that env key from the written config

#### Scenario: Codex config never carries the fallback env

- **WHEN** `config codex` runs under any detector configuration
- **THEN** the written `config.toml` contains no
  `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` key

## ADDED Requirements

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
