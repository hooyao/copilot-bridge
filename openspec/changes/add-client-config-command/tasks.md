# Implementation Tasks

## 1. Dependency & size gate (do first — R1 blocks the rest)

- [x] 1.1 Add `Tomlyn` `PackageReference` to `src/CopilotBridge.Cli/CopilotBridge.Cli.csproj` (pin a version); ensure the version is added to `Directory.Packages.props` if central package management is in use.
- [x] 1.2 Capture the published-exe size BEFORE the dep (record from `docs/size-history.md` or a fresh AOT publish mtime+size per CLAUDE.md build steps).
- [x] 1.3 AOT-publish with Tomlyn referenced (even before use, to measure link cost) and record the exe size delta; append a row to `docs/size-history.md`. If the delta is unacceptable, STOP and switch to the hand-rolled TOML editor fallback (design D4) before continuing.

## 2. Web-host-neutral config loading (D6 enabling refactor)

- [x] 2.1 Extract the `appsettings.json` source setup from `AddBridgeConfiguration(WebApplicationBuilder)` into a helper over a plain `IConfigurationBuilder` (`SetBasePath(AppContext.BaseDirectory)` + `AddJsonFile("appsettings.json", optional:false, reloadOnChange:false)`).
- [x] 2.2 Make `AddBridgeConfiguration` delegate to the new helper so `serve` behavior is byte-identical.
- [x] 2.3 Verify existing serve/startup still binds `Server:Port` correctly (run the unit suite; no behavior change expected).

## 3. Config models & connection derivation (D3, D5)

- [x] 3.1 Add `ConfigScope` (Global/Repo) enum and a `BridgeConnection` record (Port, ClaudeCodeBaseUrl, CodexBaseUrl, NeedFallback) under `Hosting/ClientConfig/`.
- [x] 3.2 Implement connection derivation: load appsettings via the D6 helper, bind `BridgeServerOptions` + `ResponseLeakGuardOptions` + `ToolInputValidationOptions`, apply `--port` override, compute `NeedFallback = (LeakGuard.Enabled && LeakGuard.PreserveStream) || (ToolInputValidation.Enabled && ToolInputValidation.PreserveStream)`.
- [x] 3.3 Add `ConfigPlan` (intended file content / structured diff, pure) and `ConfigState` (for status) types.

## 4. IClientConfigurator seam & isolated composition root (D1, D2)

- [x] 4.1 Define `IClientConfigurator` (`ClientId`, `SupportedScopes`, `Plan`, `Apply`, `Read`) under `Hosting/ClientConfig/`.
- [x] 4.2 Add `AddClientConfiguration(IServiceCollection)` composition root registering only: the loaded `IConfiguration`, the three bound `Options`, and the `IClientConfigurator` implementations — NO `AddBridgeServer`, no web host, no hosted service, no pipeline/auth/Copilot client.
- [x] 4.3 Build the command dispatcher that resolves a configurator by `ClientId`, validates the requested scope against `SupportedScopes` (reject with a clear error otherwise), then runs `Plan` → (`--dry-run` prints | `Apply` writes).

## 5. Claude Code configurator — JSON surgical merge (D4, D5, overwrite policy)

- [x] 5.1 Implement `ClaudeCodeConfigurator : IClientConfigurator` with `SupportedScopes = [Global, Repo]`; resolve target path: Global → `~/.claude/settings.json`, Repo → `./.claude/settings.local.json`.
- [x] 5.2 Implement the JSON merge via `System.Text.Json.Nodes.JsonNode`: create/locate `env`, force-set `ANTHROPIC_BASE_URL`; set `ANTHROPIC_AUTH_TOKEN` to a `copilot-bridge` value only if absent; set `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1` when `NeedFallback`, else remove that key; preserve all other keys.
- [x] 5.3 Implement `Read` for `config status` (parse current `env`, report base URL / fallback env).

## 6. Codex configurator — TOML surgical merge via DocumentSyntax (D4)

- [x] 6.1 Implement `CodexConfigurator : IClientConfigurator` with `SupportedScopes = [Global]`; resolve `$CODEX_HOME` (fallback `~/.codex`) → `config.toml`; reject `--scope repo` at the dispatcher (task 4.3) with a Codex-global-only error.
- [x] 6.2 Implement the TOML edit using Tomlyn `SyntaxParser` → `DocumentSyntax` (trivia-preserving; NEVER `TomlTable`/`TomlSerializer`): upsert top-level `model_provider = "copilot-bridge"` before the first table; create-or-replace the named table `[model_providers.copilot-bridge]` (name/base_url/wire_api) at end-of-file, matched by name on re-run; leave every other table, comment, and literal untouched.
- [x] 6.3 Implement `Read` for `config status` (report current `model_provider` + the `[model_providers.copilot-bridge]` base_url).

## 7. Safe-write plumbing (D3, R3)

- [x] 7.1 Implement backup-before-write (write prior content to a `.bak` sibling) shared by both configurators' `Apply`.
- [x] 7.2 Wire `--dry-run` to print the `ConfigPlan` and skip all filesystem writes.
- [x] 7.3 Ensure atomic-ish write (write temp + move, or write-then-flush) so a failure mid-write does not truncate the user's file.

## 8. CLI wiring (D7)

- [x] 8.1 Add the `config` command group to `RootCli.Build()` with subcommands `claude-code` (with `--scope`), `codex`, `status`; add shared `--port` and `--dry-run` options.
- [x] 8.2 Route each subcommand to the dispatcher; return process exit codes consistent with the existing auth/debug commands (0 ok, non-zero on validation/IO error).
- [x] 8.3 Implement `config status` to iterate all configurators, call `Read`, and print target + drift (stored port/env vs current appsettings-derived).

## 9. Tests (contract-first per CLAUDE.md — assert the required behavior, mutation-check each)

- [x] 9.1 Connection derivation: port from appsettings default (8765); `--port` override wins; non-default appsettings port honored (spec: "Connection facts derived from appsettings").
- [x] 9.2 Fallback-env truth table: LeakGuard Enabled+PreserveStream → env `=1`; both detectors off/false + key present → key removed; Codex plan never contains the key (spec: "Non-streaming fallback env").
- [x] 9.3 Overwrite policy: existing `ANTHROPIC_AUTH_TOKEN` preserved; missing one filled with a `copilot-bridge` value (assert no competitor branding); Codex `model`/`model_reasoning_effort` preserved; prior `[model_providers.<other>]` kept (spec: "Overwrite policy preserves unmanaged values").
- [x] 9.4 TOML fidelity: round-trip a synthetic dense `config.toml` (marketplaces, plugins, mcp env table with single-quoted path literals, multi-line notify array, `[windows]`/`[desktop]`) through `CodexConfigurator` and assert every unrelated table/comment/literal is byte-preserved; mutation-check by temporarily swapping to `TomlTable` and confirming the test goes red (spec: "Surgical merge preserves all unrelated content", design R2).
- [x] 9.5 JSON fidelity: `settings.json` with statusLine/enabledPlugins/effortLevel keeps them unchanged; only `env` keys change (spec: same requirement).
- [x] 9.6 Idempotence: run `Plan`+`Apply` twice on the same inputs → byte-identical file on the second run (spec: "Safe and idempotent writes").
- [x] 9.7 Dry-run: `--dry-run` prints the plan and leaves the on-disk file unchanged; backup created on a real write (spec: same requirement).
- [x] 9.8 Scope handling: `config codex --scope repo` fails without writing; repo scope for claude-code targets `.local.json` not `.json` (spec: "Config command family").
- [x] 9.9 Isolation: assert the `AddClientConfiguration` container resolves the configurators but does NOT register/resolve the pipeline, `AuthService`, Copilot client, or any `IHostedService`; assert a `config` subcommand runs to completion even when the default port is already bound (spec: "Isolation from the proxy server startup path").
- [x] 9.10 `config status`: reports "configured, not drifted" when stored endpoint matches appsettings; reports drift (both values shown) when ports differ; status writes nothing (spec: "Config status reports current target and drift").

## 10. Docs & finalize

- [x] 10.1 Update README "Point Claude Code at the bridge" / "Point Codex at the bridge" to offer `config claude-code --scope ...` / `config codex` as the one-step path alongside the manual edit.
- [x] 10.2 Confirm the two in-repo docs (README `=1`, `docs/claude-code-preserve-stream-retry.md`) are consistent with the chosen `"1"` value (design R5); adjust wording if they conflict.
- [x] 10.3 Fold a durable note on the composition-root isolation into `docs/` (per CLAUDE.md: record the architectural fact, not the task state).
- [x] 10.4 Run `dotnet test --filter "Category!=Integration"` and `dotnet build`; then AOT-publish and record final exe size in `docs/size-history.md`.
- [x] 10.5 Manual smoke on a copy of the real config files (non-8765 port), verifying byte-preservation of unrelated content and a clean switch-back path for Codex.
