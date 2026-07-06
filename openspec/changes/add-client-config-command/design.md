## Context

Connecting a client to the bridge is currently a manual file edit (see README
"Point Claude Code / Codex at the bridge"). Two very different files are
involved:

- **Claude Code** — `~/.claude/settings.json` (global) or a per-repo
  `.claude/settings.local.json`. Pure JSON, layered (global → project →
  project-local, most specific wins). On a real machine it also holds
  `statusLine`, `enabledPlugins`, `effortLevel`, etc. — none of which the bridge
  may disturb.
- **Codex** — `$CODEX_HOME/config.toml` (Windows default
  `C:\Users\<user>\.codex`). TOML, and on a real machine densely populated with
  unrelated content: `[marketplaces.*]`, `[plugins.*]`,
  `[mcp_servers.node_repl.env]` (dozens of keys), single-quoted Windows-path
  literals, a multi-line `notify` array, `[windows]`, `[desktop]`. Codex honors
  **global scope only**.

The CLI already has a clean architectural split we must respect and extend:

```
serve  ──► WebApplication.CreateSlimBuilder() ─► AddBridgeServer(...)
           AuthService · CopilotClient · pipeline · 5 detectors · Kestrel ·
           2 HostedServices                                    = full runtime

auth   ──► plain static command classes, `new AuthService(http)`      = no host,
debug      no web host, no Kestrel, no hosted service                   no pipeline
```

`auth` / `debug` are the precedent for "needs a little, boots no web host." The
new `config` command must live on that side. The connection details it writes
(port, and the `CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK` env) must be **derived
from the same `appsettings.json` the server reads**, so a configured client never
drifts from how the bridge actually runs.

Relevant current wiring (read during exploration):
- `Server:Port` → `BridgeServerOptions.Port` (default 8765);
  `services.Configure<BridgeServerOptions>(config.GetSection("Server"))`.
- `Pipeline:Detectors:ResponseLeakGuard` and
  `Pipeline:Detectors:ToolInputValidation` each carry `Enabled` and
  `PreserveStream` (both `true` by default), bound to `ResponseLeakGuardOptions`
  / `ToolInputValidationOptions`.
- `AddBridgeConfiguration` loads `appsettings.json` from `AppContext.BaseDirectory`
  — but only as an extension on `WebApplicationBuilder`, coupling it to the web
  host.
- Claude Code's real check (from `docs/claude-code-preserve-stream-retry.md`, a
  decompiled source excerpt): `disableFallback = isEnvTruthy(ENV) || <flag>`.
  `isEnvTruthy("1")` is true → writing `"1"` **disables** the silent
  non-streaming fallback, forcing the streaming error onto the whole-turn
  `withRetry` path. This is the intended behavior (per the change owner).

## Goals / Non-Goals

**Goals:**
- One command per client that writes a bridge-pointing config into the client's
  real file, surgically (every unrelated key/table/comment/literal preserved).
- Port and fallback-env derived from `appsettings.json` via the *same*
  strongly-typed `Options` binding the server uses — no magic-string re-reads.
- An architecture that is (a) **extensible** — a new client is a new class + one
  registration line; (b) **not sticking-plaster** — no `if (client == …)` chains,
  no flags bolted onto `AddBridgeServer`; (c) **cleanly isolated** — the command's
  DI graph is disjoint from the proxy's startup path and shares no runtime
  service; (d) **.NET-idiomatic** — DI, an interface seam, `IOptions`, and a
  Plan/Apply split.
- Idempotent, safe writes: back up before writing, byte-identical on re-run,
  `--dry-run` that touches nothing.

**Non-Goals:**
- No HTML config page (that is M2, a separate concern).
- No new *runtime* behavior — the request pipeline, detectors, auth, and the
  parameterless `serve` path are untouched.
- No auto-detection of installed clients or auto-running config on `serve`.
- No editing of shared/committed repo config for Claude Code (repo scope targets
  the personal `.local.json` only).
- Not a general TOML/JSON formatter — only the bridge's own keys are written; we
  do not normalize or reflow the rest of the file.

## Decisions

### D1 — A dedicated, web-host-free composition root, disjoint from `serve`

`config` builds its own minimal `ServiceCollection` (or resolves via a small
`AddClientConfiguration(services)` extension) containing exactly: the loaded
`IConfiguration`, the bound `Options`, and `IEnumerable<IClientConfigurator>`.
It never calls `AddBridgeServer`, never constructs a `WebApplication`, starts no
Kestrel and no hosted service.

- **Why:** The isolation is then *structural*, not by convention — the config
  container physically cannot resolve `AuthService`, the pipeline, or Kestrel
  because they were never registered. This is exactly the guarantee the change
  owner asked for ("cleanly isolated from the proxy server initialization").
- **Alternatives considered:**
  - *Add a `config` mode branch inside `AddBridgeServer` / `ServeCommand`* —
    rejected: this is the sticking-plaster the owner explicitly ruled out; it
    couples config to the full runtime graph and risks booting host machinery.
  - *Follow `auth`'s pattern of `new`-ing everything statically with zero DI* —
    viable and lighter, but gives up the `IClientConfigurator` enumeration and
    `IOptions` binding that make the design extensible and drift-proof. We keep a
    *tiny* DI root as the middle ground: idiomatic, still boots no host.

### D2 — `IClientConfigurator` seam (one implementation per client)

```csharp
interface IClientConfigurator
{
    string ClientId { get; }                              // "claude-code" | "codex"
    IReadOnlyList<ConfigScope> SupportedScopes { get; }   // [Global,Repo] | [Global]
    ConfigPlan Plan(BridgeConnection conn, ConfigScope scope); // pure, no I/O
    void Apply(ConfigPlan plan);                           // backup → merge → write
    ConfigState Read(ConfigScope scope);                  // for `config status`
}
```

- **Why:** Adding a client (e.g. a future Gemini CLI) is a new implementation +
  one `AddSingleton<IClientConfigurator, …>` line — zero edits to existing code
  or the command tree, which dispatches by `ClientId` / declared scopes. This is
  the extensibility requirement.
- The `config` subcommands are thin: resolve the connection facts once, pick the
  configurator by id, validate the requested scope against `SupportedScopes`
  (Codex + `--scope repo` → a clear error), then `Plan` → (`--dry-run` prints, or
  `Apply` writes).
- **Alternatives considered:** a `switch` over client name inside one big command
  handler — rejected as the same sticking-plaster in a different spot.

### D3 — Plan / Apply / Read split (pure core, effectful edge)

`Plan` computes the full intended file content (or a structured diff) as a pure
function of `(BridgeConnection, scope, current-file-bytes)`; `Apply` performs
backup + write; `Read` reports current state.

- **Why:** `--dry-run` is then free (print the Plan, never Apply); idempotence is
  testable (same inputs → same Plan → same bytes); `config status` reuses `Read`.
  This is the standard command/effect separation and keeps the surgical-merge
  logic unit-testable without touching the filesystem.

### D4 — Surgical, format-preserving merges — deliberately asymmetric

| | Claude Code (JSON) | Codex (TOML) |
|---|---|---|
| Parser | `System.Text.Json.Nodes.JsonNode` | Tomlyn `SyntaxParser` → `DocumentSyntax` |
| Edits | set `["env"][key]` on the DOM | mutate only the target key-value / named table nodes |
| Preserves | all keys, ordering | all trivia: comments, whitespace, literals, table order |
| New dep | none (STJ already in-tree) | **Tomlyn** |

- **Why JSON is lighter:** the real `settings.json` is plain JSON with no
  comments, so `JsonNode` (which may reorder/​drop JSONC comments) is acceptable
  and needs no dependency.
- **Why TOML needs the syntax tree, NOT the model DOM:** Tomlyn has two layers.
  The **model DOM** (`TomlTable`/`TomlArray`, `TomlSerializer`) is documented to
  **not** preserve comments or formatting — round-tripping the real
  `config.toml` through it would rewrite the `notify` array, the single-quoted
  path literals, and the `[mcp_servers.node_repl.env]` block. The **low-level
  `DocumentSyntax`** is a *lossless, trivia-preserving* tree — the only correct
  choice. **This distinction is load-bearing; using `TomlTable` here silently
  corrupts user config and must be called out in tasks/tests.**
- **TOML edit shape (bounds the surface):** TOML requires top-level keys to
  appear before the first `[table]`. So the edit is two clean regions:
  (1) upsert the top-level `model_provider` pointer (region before the first
  table); (2) create-or-replace the single named table
  `[model_providers.copilot-bridge]` at end-of-file, matched by name on re-run.
  All other tables are untouched. The pre-existing provider block (e.g.
  `[model_providers.agent-maestro]`) is **kept** — switching back is a one-line
  pointer change (additive, not subtractive).
- **Alternatives considered:** hand-rolled TOML string editing (zero dep, minimal
  size) — kept as the documented fallback if Tomlyn's size cost is unacceptable
  (see R1); rejected as the primary because correctness would rest entirely on
  tests for escaping/multiline/array edge cases.

### D5 — Derive connection facts from `appsettings.json` via bound Options

`config` loads `appsettings.json` (D6) and binds `BridgeServerOptions`,
`ResponseLeakGuardOptions`, `ToolInputValidationOptions`, then computes a
`BridgeConnection`:

```
Port          = --port ?? BridgeServerOptions.Port        (default 8765)
BaseUrl(cc)   = http://localhost:{Port}/cc
BaseUrl(codex)= http://localhost:{Port}/codex
NeedFallback  = (ResponseLeakGuard.Enabled && ResponseLeakGuard.PreserveStream)
             || (ToolInputValidation.Enabled && ToolInputValidation.PreserveStream)
```

- **Env derivation (Claude Code only):**
  `NeedFallback == true` → `env["CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"] = "1"`
  (whole-turn retry, per D-owner + `isEnvTruthy` semantics);
  `NeedFallback == false` → **remove** that env key (self-heal to appsettings).
- **Auth token:** `env["ANTHROPIC_AUTH_TOKEN"]` filled with `"copilot-bridge"`
  **only if absent**; an existing value is preserved (the bridge ignores it).
- **Why bound Options over `GetValue<>("Pipeline:Detectors:…")`:** reuses the
  server's exact section→type mapping, so the two code paths cannot disagree on
  key names or defaults, and stays AOT-safe on the already-proven path.

### D6 — Extract web-host-neutral config-source loading

Refactor the `appsettings.json` source setup out of
`AddBridgeConfiguration(WebApplicationBuilder)` into a helper over a plain
`IConfigurationBuilder` (`SetBasePath(AppContext.BaseDirectory)` +
`AddJsonFile("appsettings.json", optional:false, reloadOnChange:false)`). `serve`
keeps its current behavior by delegating to it; `config` calls the same helper.

- **Why:** one definition of "where/how appsettings is loaded," shared by both
  roots — the enabling seam for D5 without duplicating load logic. Minimal,
  behavior-preserving for `serve`.

### D7 — Command surface & scope landing

```
config claude-code --scope global   → ~/.claude/settings.json          (env block)
config claude-code --scope repo     → ./.claude/settings.local.json    (env block)
config codex                        → $CODEX_HOME/config.toml          (no --scope)
config status                       → read all, report target + drift
shared flags: --port <n>   --dry-run
```

- Repo scope writes `.local.json` (Claude Code auto-gitignores it) so a shared
  `.claude/settings.json` is never clobbered — the personal `localhost` endpoint
  stays out of team config. Layering means a machine can point globally at one
  backend and override per-repo to the bridge.
- `$CODEX_HOME` resolves from the env var, falling back to `~/.codex`.

## Risks / Trade-offs

- **R1 — Tomlyn inflates the AOT `.exe`.** The project tracks size in
  `docs/size-history.md` and small binary size is a stated project value.
  → *Mitigation:* measure the published-exe delta as an explicit task
  (capture-before/after mtime + size per CLAUDE.md build steps). Docs confirm
  Tomlyn disables reflection serialization under `PublishAot=true`, and we only
  touch `DocumentSyntax` (no reflection path), so it should trim well. If the
  delta is unacceptable, fall back to the hand-rolled TOML editor from D4 — cheap
  because the edit surface is tiny (a few top-level keys + one named table).
- **R2 — Using the wrong Tomlyn layer silently corrupts config.** `TomlTable`
  drops comments/formatting. → *Mitigation:* D4 mandates `DocumentSyntax`; a
  contract test round-trips the real dense `config.toml` shape and asserts every
  unrelated table/comment/literal is byte-preserved (mutation-checked).
- **R3 — Writing a real editor config file is destructive if it goes wrong.**
  → *Mitigation:* back up to `.bak` before writing (Codex itself does this,
  cf. `.codex-global-state.json.bak`); `--dry-run`; idempotent by construction
  (D3); never delete a foreign key (only the bridge's own keys are managed).
- **R4 — Derived env/port is a snapshot; editing `appsettings.json` later and
  restarting `serve` without re-running `config` leaves the client stale.**
  → *Mitigation:* `config status` reports drift (client value vs current
  appsettings) so the staleness is visible and one re-run fixes it. Documented,
  not silently corrected.
- **R5 — README currently shows `=1` while a doc argued `false`.** The owner
  chose `"1"` (whole-turn retry); README and source comments already say `=1`.
  → *Mitigation:* no README value change needed; add a task to confirm the two
  in-repo docs are consistent with the chosen `"1"`.
- **Trade-off — a tiny DI root instead of `auth`'s zero-DI static style.** Costs
  a few lines of registration; buys `IClientConfigurator` enumeration and
  `IOptions` binding (extensibility + drift-proofing). Judged worth it.

## Migration Plan

Purely additive; no runtime behavior changes, nothing to roll back at the
protocol level.
1. D6 refactor (behavior-preserving for `serve`), covered by existing/serve
   smoke.
2. Add Tomlyn; measure size (R1 gate).
3. Land the composition root + `IClientConfigurator` + two configurators + merge
   helpers + command tree.
4. Update README client-setup sections; fold the isolation note into `docs/`.
- **Rollback:** revert the change; no persisted state or wire-format migration is
  involved. User files already written keep working (they're plain client config).

## Open Questions

- **Backup retention:** single `.bak` (overwrite on each run) vs timestamped? Lean
  single `.bak` for simplicity, matching Codex's own convention. (Confirm during
  specs.)
- **`config status` output format:** human table now; a `--json` mode is a
  possible later add, not in this change.
