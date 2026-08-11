## ADDED Requirements

### Requirement: Repository instructions have one canonical source
The repository SHALL provide `AGENTS.md` as the canonical cross-agent
constitution, and Claude Code SHALL consume that same content through
`CLAUDE.md` without maintaining a duplicated instruction body.

#### Scenario: Codex opens a fresh clone
- **WHEN** Codex enters the repository
- **THEN** it discovers the complete project guidance from the tracked root `AGENTS.md`

#### Scenario: Claude Code opens a fresh clone
- **WHEN** Claude Code reads the tracked root `CLAUDE.md`
- **THEN** that file imports `AGENTS.md` as the single instruction source

### Requirement: Codex discovers the project workflows
The repository SHALL track Codex-compatible skill packages under
`.agents/skills` for model synchronization, real-client verification, PR
shipping, and OpenSpec workflows required by `AGENTS.md`.

#### Scenario: Codex is asked to ship a PR
- **WHEN** the operator invokes the repository ship workflow in a fresh clone
- **THEN** Codex can discover `ship-pr` and every referenced script and document under `.agents/skills/ship-pr`

#### Scenario: Codex verifies a real client
- **WHEN** `AGENTS.md` requires the `real-client-verify` workflow
- **THEN** Codex can discover the skill, its evidence references, and its SQLite reader under `.agents/skills/real-client-verify`

#### Scenario: Codex applies an OpenSpec change
- **WHEN** the operator invokes an `/opsx:*` workflow
- **THEN** the corresponding tracked OpenSpec skill is available under `.agents/skills`

### Requirement: Mirrored project skills retain semantic parity
Every hand-maintained project skill mirrored between `.claude/skills` and
`.agents/skills` SHALL have the same file set and content after normalizing only
the skill-root path. Agent compatibility MUST NOT change protocol facts, model
ids, verification gates, or task semantics.

#### Scenario: A mirrored skill is edited on one side
- **WHEN** CI compares the Claude and Codex copies after root-path normalization
- **THEN** any missing file or semantic content difference fails the contract

#### Scenario: A skill contains a self-reference
- **WHEN** a command in a Codex skill invokes one of its bundled scripts
- **THEN** the command resolves through `.agents/skills` rather than `.claude/skills`

### Requirement: Local Codex credentials never enter version control
The repository SHALL ignore `.codex/config.toml` and SHALL keep tracked Codex
documentation free of API keys, authentication headers, and other machine-local
credentials.

#### Scenario: A developer configures a local MCP server
- **WHEN** credentials are written to `.codex/config.toml`
- **THEN** Git ignores that file while continuing to track credential-free repository documentation under `.codex`

#### Scenario: Codex compatibility is reviewed for shipping
- **WHEN** the compatibility files are staged
- **THEN** no local `.codex/config.toml` or credential value is present in the staged diff
