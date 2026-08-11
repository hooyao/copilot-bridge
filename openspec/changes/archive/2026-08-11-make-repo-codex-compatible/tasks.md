## 1. Compatibility Contracts

- [x] 1.1 Write failing CI-safe contracts for the `CLAUDE.md` canonical import, required Codex skill discovery, mirrored skill parity after root normalization, and the local `.codex/config.toml` ignore rule.
- [x] 1.2 Mutation-check the mirror contract by introducing a semantic difference and confirming the focused test fails.

## 2. Canonical Instructions and Secret Safety

- [x] 2.1 Replace duplicated `CLAUDE.md` instructions with an `AGENTS.md` import and document the two supported project skill roots in the canonical guidance.
- [x] 2.2 Ignore the credential-bearing `.codex/config.toml` while adding credential-free `.codex/README.md` repository guidance.

## 3. Codex Skill Discovery

- [x] 3.1 Restore `.agents/skills/copilot-model-sync` from the tracked source without semantic word replacement.
- [x] 3.2 Mirror `real-client-verify` and `ship-pr` into `.agents/skills`, changing only their self-referential skill-root paths.
- [x] 3.3 Track the generated OpenSpec and `/opsx:*` Codex skills required for fresh-clone workflows.

## 4. Verification

- [x] 4.1 Run the focused compatibility contracts and the complete CI-safe unit suite.
- [x] 4.2 Audit the intended Git diff for plaintext credentials, local `.codex/config.toml`, unrelated untracked files, and residual semantic drift between mirrored skills.

## 5. PR Review Follow-ups

- [x] 5.1 Keep the mirrored real-client SQLite reader on a supported .NET 10 Core provider plus SQLitePCLRaw 3.x bundle; verify the reader runs without the vulnerable 2.1.x package warning.
- [x] 5.2 Paginate the ship-pr review-thread safety query and aggregate unresolved threads across every GraphQL page before authorizing merge.
- [x] 5.3 Paginate reply-resolve thread lookup as well, so a later-page unresolved comment can be addressed and resolved after the status gate finds it.
- [x] 5.4 Exclude crash-left credential atomic-temp files from real-client scratch copies as part of the credential boundary.
