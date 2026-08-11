## Why

Codex already reads the repository's canonical `AGENTS.md`, but the tracked
project skills exist only under `.claude/skills`; the untracked `.agents` copy is
incomplete and one mirrored skill has been semantically corrupted by mechanical
Claude-to-Codex word replacement. As a result, Codex cannot reliably discover
the same ship, live-verification, and model-sync workflows, while a local
`.codex/config.toml` containing credentials is at risk of accidental commit.

## What Changes

- Make `AGENTS.md` the actual single source of repository instructions and reduce
  `CLAUDE.md` to a supported import of that canonical file.
- Track Codex-discoverable project skills under `.agents/skills`, preserving the
  semantics of the tracked `.claude/skills` versions and changing only
  environment-local path references.
- Track the generated OpenSpec skills that Codex uses for `/opsx:*` workflows.
- Keep `.codex/config.toml` local-only and document where repository instructions
  and skills live without committing credentials.
- Add a CI-safe parity contract so future edits cannot silently drift or corrupt
  the mirrored Claude/Codex project skills.

## Capabilities

### New Capabilities

- `agent-repository-compatibility`: Defines canonical repository instructions,
  cross-agent skill discovery/parity, and secret-safe local Codex configuration.

### Modified Capabilities

None.

## Impact

- Repository agent surfaces: `AGENTS.md`, `CLAUDE.md`, `.agents/skills/`,
  `.claude/skills/`, `.codex/`, and `.gitignore`.
- CI-safe unit tests that validate mirror parity and canonical instruction wiring.
- No bridge runtime, API, dependency, or Native AOT behavior changes.
