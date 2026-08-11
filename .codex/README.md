# Codex repository configuration

Codex reads the repository constitution from [`../AGENTS.md`](../AGENTS.md) and
discovers repo-owned workflows under [`../.agents/skills/`](../.agents/skills/).

`config.toml` is intentionally ignored because it is machine-local and may
contain MCP endpoints, authentication headers, or other credentials. Keep those
values local; this repository requires no Codex MCP configuration to build,
test, or release.
