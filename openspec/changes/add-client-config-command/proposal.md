## Why

Pointing a client at the bridge today is a manual, error-prone edit of a real
editor config file: Claude Code needs an `env` block in `~/.claude/settings.json`
(or a per-repo `.claude/settings.local.json`), and Codex needs a provider block
plus a top-level `model_provider` pointer in `~/.codex/config.toml`. Users repeat
this across machines and repos, hand-copy the port, and must *know* to also set
`CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` when the response detectors run with
`PreserveStream=true`. A single `config` command removes the guesswork and
derives the port and that fallback env from the bridge's own `appsettings.json`,
so what gets written always matches how the bridge actually runs.

## What Changes

- Add a **`config` command family** to the CLI:
  - `config claude-code --scope global|repo` — write the `env` block into
    `~/.claude/settings.json` (global) or `./.claude/settings.local.json` (repo,
    the personal/gitignored file, so a shared repo config is never clobbered).
  - `config codex` — write the provider block + top-level pointer into
    `$CODEX_HOME/config.toml` (Codex honors **global scope only**; no `--scope`).
  - `config status` — read the current client configs and report where each
    points and whether it has drifted from the current `appsettings.json`
    (port / fallback-env).
  - Shared flags: `--port` (override the appsettings-derived port, symmetric with
    `serve`) and `--dry-run` (print the planned write, touch nothing).
- **Derive values from `appsettings.json`, not hard-coded strings:** the port
  comes from `Server:Port` (default 8765); when either `ResponseLeakGuard` or
  `ToolInputValidation` has `Enabled && PreserveStream`, the Claude Code `env`
  gains `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1` (and it is removed when
  neither does, so the written config self-heals to the appsettings state).
- **Surgical, format-preserving merges** — only the bridge's own keys are
  touched; every unrelated key, table, comment, and literal in the user's file is
  preserved. JSON via `System.Text.Json.Nodes` (`JsonNode`); TOML via Tomlyn's
  lossless syntax tree (`DocumentSyntax`), **not** the model-DOM round-trip.
- **Overwrite policy:** only `base_url` / the provider pointer are force-written;
  other existing keys (Codex `model`, `model_reasoning_effort`, the
  `ANTHROPIC_AUTH_TOKEN` placeholder) are preserved if present and only filled in
  when missing. The auth-token placeholder default is a `copilot-bridge` string
  (no competitor branding).
- **Extensible architecture:** an `IClientConfigurator` seam (one implementation
  per target client) composed in a **dedicated, web-host-free composition root**
  that is disjoint from `serve`'s DI graph — adding a future client is a new
  implementation plus one registration line, with no change to the serve path.
- Add a **Tomlyn** package dependency (AOT-clean via the syntax-tree API; a
  published-exe size measurement gates acceptance).
- Update **README** so the "Point Claude Code / Codex at the bridge" sections
  offer the `config` command as the one-step path alongside the manual edit.

## Capabilities

### New Capabilities
- `client-autoconfiguration`: A CLI command family that writes bridge-pointing
  configuration into the real config files of supported LLM clients (Claude Code,
  Codex), deriving connection details from `appsettings.json`, merging
  surgically to preserve all unrelated content, and running in a composition root
  isolated from the proxy server's startup path.

### Modified Capabilities
<!-- None. The command reads ResponseLeakGuard / ToolInputValidation options to
     derive an env var, but does not change any existing spec's required behavior.
     No requirement in observability, pipeline-request-isolation, or
     response-leak-guard changes. -->

## Impact

- **New code:** a `config` subcommand tree in `RootCli`; a new
  `Hosting/ClientConfig/` folder holding `IClientConfigurator`, the two client
  implementations, the JSON/TOML surgical merge helpers, and the isolated
  composition root (`AddClientConfiguration`).
- **Small enabling refactor:** extract the `appsettings.json` source-loading from
  `AddBridgeConfiguration` (currently bound to `WebApplicationBuilder`) into a
  web-host-neutral helper both `serve` and `config` call, so `config` reads
  `Server:Port` / detector options through the *same* strongly-typed `Options`
  binding the server uses (no new magic-string `GetValue` calls). The `serve`
  runtime path and pipeline are otherwise untouched.
- **New dependency:** `Tomlyn` `PackageReference` in `CopilotBridge.Cli.csproj`;
  binary-size delta measured against `docs/size-history.md`.
- **Docs:** README client-setup sections updated; a durable note on the
  composition-root isolation folded into `docs/` once implemented.
- **No impact** on the request pipeline, detectors, auth, or the parameterless
  `serve` startup — the new command boots no web host and shares no runtime
  services with the proxy.
