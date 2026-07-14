## Why

Copilot Bridge currently requires users to discover, download, and replace release files manually, which makes security and compatibility fixes easy to miss and makes preserving an operator-edited `appsettings.json` error-prone. A guarded startup update flow can keep installed releases current without making GitHub availability, a failed replacement, or a bad new build prevent the proxy from serving.

## What Changes

- Add `AutoUpdate:EnableAutoUpdate` (default `true`) and `AutoUpdate:AllowBetaUpdates` (default `false`) configuration. Only explicit `serve` and the parameterless default action check for updates, synchronously before proxy startup; maintenance commands never check.
- Query the project's public GitHub Releases with finite timeouts, apply SemVer precedence and the configured stable/prerelease channel, print the selected release's notes, and ask `Install this update now? [y/N]`. Check failures warn and fall through to the current proxy; redirected/noninteractive input never installs.
- Add one small Native AOT `copilot-updater` executable per supported RID and include it in each auto-update archive. The CLI owns release selection, user policy, asset selection, and the immutable update plan; the updater only executes that plan.
- Turn installation into a recoverable transaction: download and integrity-check the selected archive, reject unsafe paths, stage it outside the install directory, lock the installation, back up only managed release files, stop only the identified old bridge, install, preserve the original invocation context, and wait for a per-attempt authenticated Ready handshake from the replacement proxy before committing.
- Migrate `appsettings.json` into a newly generated staged file using the new release's file as the template. For keys present in both files, old operator values win; objects merge recursively; arrays are atomic and the entire old array wins; new-only keys keep new defaults; old-only keys are removed; and ambiguous case-insensitive keys fail before cutover. The original file is renamed to `appsettings.json.bak.<attempt-id>` during cutover.
- If installation or replacement startup fails, restore the old managed binaries and exact original configuration, then restart the old bridge once with update checking suppressed. Preserve `github_token.dat`, logs, traces, and every unknown/user-managed file throughout.
- Extend release automation to package the updater in every update archive. The CLI obtains each selected asset's `browser_download_url`, `size`, and GitHub-computed `digest` through the anonymous GitHub Releases REST API and records them in the update plan; the updater downloads with ordinary HTTPS and verifies the digest before cutover. No `gh` executable or separately published checksum file is required. The trust boundary remains GitHub Releases over HTTPS; this integrity check is not an independent code-signing system.
- Add contract-derived tests for command scoping, channel/version selection, prompts, configuration migration, archive safety, transaction boundaries, readiness, rollback, concurrency, and release layout, plus Native AOT and real-process update verification on supported platforms.

## Capabilities

### New Capabilities

- `startup-update-discovery`: Serve-only startup checks, configuration defaults, eligible-release and RID-asset selection, release-note presentation, interactive consent, and fail-open behavior.
- `transactional-self-update`: The CLI/updater execution boundary, secure staging and integrity validation, template-based configuration migration, coordinated process replacement, readiness commit, rollback, state preservation, and release packaging.

### Modified Capabilities

<!-- None. Existing proxy protocol, pipeline, and observability contracts are unchanged. -->

## Impact

- **CLI startup:** `RootCli`/`ServeCommand` gain a pre-host update gate shared by explicit and parameterless `serve`; auth, debug, config, help, and version paths remain isolated.
- **Configuration:** a new strongly typed `AutoUpdate` section is loaded from the executable-adjacent `appsettings.json`, with code defaults for older installations.
- **New executable:** a minimal `copilot-updater` Native AOT project/package artifact and a source-generated update-plan contract shared with the CLI.
- **Host lifecycle:** the replacement process can receive one-launch-only internal update context and report Ready only after startup validation/authentication and listener startup complete.
- **Filesystem/processes:** temporary staging and backup directories, an install-scoped lock, exact original argument/working-directory preservation, managed-file replacement, and rollback diagnostics.
- **Release workflow:** all four supported RID archives package Bridge, updater, and stock configuration; GitHub's Release Asset metadata supplies the archive digest. macOS archive updates remain non-elevating and the `.pkg` installer stays a manual installation path.
- **Security:** HTTPS-only release assets, digest verification before cutover, archive traversal/link rejection, install-root confinement, current-process identity checks, and no authentication secrets in plans or logs.
- **No wire impact:** Anthropic, OpenAI/Codex, routing, and detector behavior are unchanged.