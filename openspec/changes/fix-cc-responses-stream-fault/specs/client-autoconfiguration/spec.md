## MODIFIED Requirements

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
