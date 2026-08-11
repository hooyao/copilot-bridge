## Context

The repository already tracks `AGENTS.md` as its intended cross-agent source of
truth and tracks three project workflows under `.claude/skills`. Codex discovers
repository skills under `.agents/skills`, but that directory is currently
untracked, lacks `real-client-verify` and `ship-pr`, and contains a corrupted
model-sync rewrite that changes protocol facts rather than only adapting paths.
`CLAUDE.md` also duplicates and drifts from `AGENTS.md` despite claiming the
latter is canonical. Finally, `.codex/config.toml` is a machine-local file and
currently contains a credential-bearing MCP header.

## Goals / Non-Goals

**Goals:**

- Make Codex discover the same repository-specific model-sync, real-client
  verification, and ship workflows as Claude Code.
- Keep `AGENTS.md` genuinely canonical while retaining Claude Code support.
- Prevent semantic drift between hand-maintained Claude and Codex skill mirrors.
- Keep local Codex credentials out of version control.
- Track Codex's generated OpenSpec skills so `/opsx:*` workflows are available
  after a fresh clone.

**Non-Goals:**

- Committing user- or machine-specific MCP endpoints, headers, or credentials.
- Rewriting Claude-oriented protocol concepts into Codex terminology when those
  concepts describe the bridge's Claude path.
- Changing bridge runtime behavior, client routing, or release packaging.
- Mirroring generated OpenSpec skills into `.claude`; Claude's command surface is
  managed independently.

## Decisions

### 1. `AGENTS.md` is canonical; `CLAUDE.md` imports it

Replace the duplicated `CLAUDE.md` body with Claude Code's `@AGENTS.md` import.
Both agents then consume one constitution and cannot disagree because one copy
was not updated. Keeping two full documents was rejected because the current
stale M1/M3 prose demonstrates that manual synchronization is not reliable.

### 2. Mirror hand-maintained skills without semantic translation

The three tracked `.claude/skills` packages are copied into `.agents/skills`.
Their file sets and contents remain byte-equivalent after normalizing only the
environment-local root (`.claude/skills` versus `.agents/skills`). Model ids,
Claude-path instructions, probe requirements, and verification gates are domain
facts and MUST NOT be word-replaced to sound more "Codex-like".

A CI-safe unit contract compares the three mirrors after path normalization.
This makes future one-sided edits fail loudly. Generated OpenSpec packages remain
Codex-only and are validated by presence rather than mirrored against Claude.

### 3. Treat `.codex/config.toml` as local secret-bearing state

Ignore exactly `.codex/config.toml`, not the whole directory, and track a
credential-free `.codex/README.md` explaining the canonical instruction and
skill locations. This permits future safe repository metadata under `.codex`
while preventing the known local configuration from accidental staging. A
checked-in example containing a fake header was rejected because it encourages
copying credentials into a tracked file and the repository requires no MCP to
build or test.

### 4. Validate repository wiring as observable file contracts

Unit tests locate the repository root and assert the `CLAUDE.md` import, mirrored
skill file sets/content, required Codex/OpenSpec skill presence, and the exact
local-config ignore rule. These are observable repository behaviors, require no
network, and run on every supported OS.

## Risks / Trade-offs

- **Two skill roots can drift** → Normalize only root paths and compare every
  mirrored file in CI.
- **A mechanical copy can overwrite intentional Codex semantics** → Define the
  contract as semantic parity; agent-specific behavior belongs in a distinct
  skill, not a rewritten mirror.
- **A local secret was already stored in plaintext** → Exclude it from Git and
  advise credential rotation; never echo or migrate its value.
- **Claude import support could change** → Use Claude Code's supported `@file`
  instruction import and guard the exact pointer in tests.

## Migration Plan

1. Restore the corrupted model-sync mirror from the tracked Claude source.
2. Add the missing hand-maintained and generated Codex skills.
3. Replace `CLAUDE.md` with the canonical import and add the local-config guard.
4. Run mirror/wiring contracts and the full CI-safe unit suite.
5. Rollback is a normal Git revert; local `.codex/config.toml` is never modified.

## Open Questions

None.
