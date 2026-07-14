## Context

Copilot Bridge is currently one .NET 10 Native AOT executable plus a required executable-adjacent `appsettings.json`. Parameterless invocation and the explicit `serve` command both enter `ServeCommand.RunAsync`, which immediately builds the ASP.NET host. Configuration is loaded from `AppContext.BaseDirectory`; authentication state (`github_token.dat`), rolling logs, and relative request traces may also live under that directory. There is no update client, updater executable, process-restart path, or existing release manifest.

The release workflow publishes four RID archives:

| RID | Archive | Executable |
|---|---|---|
| `win-x64` | ZIP | `copilot-bridge.exe` |
| `win-arm64` | ZIP | `copilot-bridge.exe` |
| `linux-x64` | TAR.GZ | `copilot-bridge` |
| `osx-arm64` | TAR.GZ | `copilot-bridge` |

It also publishes a macOS `.pkg`. Each current archive contains only the bridge and stock configuration. Once an archive is uploaded, GitHub's Release Asset API exposes its HTTPS download URL, size, and GitHub-computed `sha256:...` digest. Public release metadata is available anonymously, so runtime update discovery does not need a GitHub token or the `gh` CLI. Anonymous API failures and the 60-requests-per-IP/hour limit are acceptable because update discovery is explicitly fail-open.

A self-update spans two process lifetimes and mutates the executable that initiated it. The update therefore needs a clear ownership transfer and transaction boundary:

```text
old bridge process                         updater process
──────────────────                         ───────────────
load config
query public releases
show notes + ask y/N
launch temp updater ─────────────────────► validate plan + lock
                                           download + hash + stage
                                           merge config + back up
                  ◄─────────────────────── PREPARED
authorize cutover
exit before host startup ────────────────► verify/wait for exact parent exit
                                           install staged release
                                           launch new bridge
                                                     │
                                                     ▼
                                           wait for authenticated READY
                                             │                    │
                                           ready                fail/timeout
                                             │                    │
                                           commit        restore old release/config
                                                                  │
                                                                  ▼
                                                        launch old bridge + wait READY
```

The update checks and download are intentionally synchronous before proxy startup because that is the requested product behavior. This trades startup latency for a simple guarantee that there is never an old proxy serving while an accepted update is being prepared.

## Goals / Non-Goals

**Goals:**

- Check once only on parameterless/explicit `serve`, before host construction and listener startup.
- Default update checking on, with stable-only selection unless prereleases are explicitly allowed.
- Use anonymous GitHub REST/HTTPS and GitHub Release Asset digest metadata; require no runtime `gh` or external extraction/download tools.
- Keep every release/channel/prompt/asset decision in the bridge and keep the updater policy-free.
- Make all network, parsing, staging, migration, backup, and writability failures fail before stopping the initiating bridge.
- Generate a new configuration from the new template while retaining old values for surviving keys, treating arrays atomically, and dropping removed keys.
- Commit only after the replacement proxy is genuinely listening and has completed startup; otherwise restore the old managed files and exact original configuration.
- Preserve invocation arguments, working directory, user/environment, tokens, logs, traces, and unknown files.
- Remain Native AOT, trimming-safe, and self-contained on every supported RID.

**Non-Goals:**

- No periodic update checks while the proxy is running.
- No background, silent, or consent-free installation.
- No `--yes`, scheduled install, ignored-version state, or separate manual `update` command in v1.
- No authentication to GitHub's Releases API and no use of the persisted GitHub/Copilot tokens for update discovery.
- No updater-side release discovery, channel selection, prompts, or configuration policy.
- No privilege elevation, `sudo`, UAC prompt, package-manager invocation, or automatic `.pkg` installation.
- No independent code-signing trust root in this change. GitHub Release Asset digest verification detects corruption/substitution relative to fetched GitHub metadata but does not defend against compromised repository release authority.
- No crash-proof guarantee against power loss, OS termination of the updater, or storage-device failure in the cutover window. The journal and retained backups make those cases diagnosable/recoverable, but v1 does not add a third watchdog process or system service.
- No application configuration migration beyond the specified JSON template overlay.

## Decisions

### D1 — Put a pre-host update gate inside the serve path

Both root/default invocation and the explicit `serve` command already converge on `ServeCommand.RunAsync`. Insert a small pre-host phase there, before `WebApplication.CreateSlimBuilder()`:

```text
ServeCommand.RunAsync
  ├─ validate CLI port
  ├─ load executable-adjacent appsettings through AddBridgeAppSettings
  ├─ bind AutoUpdateOptions (code defaults: enabled=true, allowBeta=false)
  ├─ if valid one-launch updater context: skip discovery
  ├─ run StartupUpdateGate
  │    ├─ ContinueCurrentVersion → build/run host
  │    └─ HandoffAccepted       → return without building host
  └─ existing host construction and RunAsync
```

The gate is not registered in `AddBridgeServer` and is not a hosted service. This structurally prevents help/version/auth/debug/config from checking and guarantees the check happens before Kestrel or authentication startup. Configuration is loaded a second time by the eventual host; both reads use the same existing source helper and no mutable in-memory configuration crosses the boundary.

**Alternatives considered:**

- *Hosted service update check* — rejected because hosted services run during host startup and make listener/readiness ordering harder; an accepted update would also have to tear down a partially constructed host.
- *Root-level middleware around every command* — rejected because it risks checking on maintenance/help paths.
- *Periodic background service* — rejected by scope and would require unattended-install policy.

### D2 — Add exactly two code defaults under `AutoUpdate`

Add a strongly typed options type:

```text
AutoUpdate
  EnableAutoUpdate  = true
  AllowBetaUpdates  = false
```

The stock `appsettings.json` contains both properties for discoverability, but the POCO defaults are authoritative for upgraded installations that lack the section. Timeouts remain internal bounded constants copied into each update plan; they are not additional user-facing settings in v1.

A parsed prerelease identifier equal to `dev` (or containing `dev` as an identifier) disables self-update for local builds such as `0.1.0-dev`. This prevents `dotnet run`/developer publish directories from being replaced by a release archive.

### D3 — Use an anonymous, in-process GitHub Releases client

The bridge uses a dedicated `HttpClient` and source-generated GitHub response DTOs to call:

```text
GET https://api.github.com/repos/hooyao/copilot-bridge/releases?per_page=100&page=N
Accept: application/vnd.github+json
X-GitHub-Api-Version: 2022-11-28
User-Agent: copilot-bridge/<installed-version>
```

It follows pagination until exhaustion is proven, filters drafts defensively, and does not send authorization. A monotonic wall-clock deadline bounds the complete traversal in addition to each request timeout; visited next-target/page tracking rejects cycles, and a defensive maximum page count prevents an endless sequence of full pages. If exhaustion is not proven within those bounds, every partial result is discarded and discovery fails open. It parses release tags itself and selects the highest eligible SemVer. It does not call `/releases/latest`, because that endpoint excludes prereleases and cannot implement the beta channel. HTTP/API/schema/rate-limit/request-timeout/overall-deadline/pagination-bound failures become one Warning and `ContinueCurrentVersion`; user/application-shutdown cancellation remains cancellation.

No runtime component invokes `gh`. The existing release workflow may continue using `gh` inside GitHub-hosted CI because that is a build-time environment, not a user-machine dependency.

**Alternatives considered:**

- *Use `gh release` at runtime* — rejected because installed machines cannot be assumed to have or authenticate `gh`.
- *Use GitHub's “latest” endpoint* — rejected because prerelease selection and complete semantic ordering are required.
- *Use persisted OAuth token to raise rate limits* — rejected because update discovery is public, auth is encapsulated, and exposing the token to this subsystem adds unnecessary privilege.
- *Cache release responses/ETags on disk* — deferred. It reduces anonymous quota pressure but adds stale-state and cleanup behavior; fail-open startup is sufficient for v1.

### D4 — Implement a small internal SemVer 2.0 value type

Avoid adding a package solely for version comparison. A small immutable parser/value type handles:

- optional `v` stripping at the release-tag boundary;
- three numeric core components;
- dot-separated prerelease identifiers with numeric-vs-text precedence;
- ignored build metadata;
- stable > prerelease at the same core version;
- ordinal ASCII comparison for nonnumeric identifiers.

Invalid/overflowing release tags are skipped. If the installed product version cannot be parsed, discovery warns and starts the current build rather than guessing. Publication timestamp never participates in precedence.

### D5 — Select one exact RID asset and trust GitHub metadata directly

Map `OperatingSystem` plus `RuntimeInformation.ProcessArchitecture` to the four published RIDs. Asset names are deterministic from selected release version and RID. A candidate is installable only if exactly one matching archive is in `uploaded` state and supplies:

- an HTTPS `browser_download_url`;
- a positive `size`;
- `digest` in `sha256:<64 hex>` form.

The CLI records those values in the immutable plan. The updater computes SHA-256 while downloading and compares both size and digest before extraction. There is no separately published `.sha256` sidecar: a sidecar in the same GitHub Release would share the same trust boundary and add no independent authenticity. Independent signatures/Authenticode/notarization can be a later change.

The downloader disables blind automatic redirect following and follows a small bounded redirect chain manually, requiring HTTPS on the initial and every redirected URL.

### D6 — Present untrusted release text safely and require explicit consent

Version facts and the complete release body are written to the operator console before `Install this update now? [y/N]`. Release title/body are untrusted remote data: retain ordinary line breaks/tabs needed for notes, but strip or visibly encode escape and other terminal-control characters. Only trimmed case-insensitive `y`/`yes` accepts.

`Console.IsInputRedirected`, read failure, or unavailable input means “do not install.” There is no timeout for an attached interactive console—the operator was explicitly asked—but a noninteractive process never blocks on input. Decline state is not persisted.

### D7 — Introduce one minimal updater project and share only the wire contract as source

A second executable is unavoidable because a running Windows executable cannot replace itself. Add `src/CopilotBridge.Updater/CopilotBridge.Updater.csproj` with:

- `Microsoft.NET.Sdk`, `net10.0`, `PublishAot`, `SelfContained`, size optimization;
- no ASP.NET framework reference;
- no third-party package unless an AOT/size review proves it necessary;
- framework APIs for HTTP, cryptography, ZIP, GZip/TAR, JSON, files, and processes.

To avoid a third assembly/project or a reference from updater to the full CLI, keep the small versioned update-plan/control DTOs in a shared source folder compiled into both executables. The wire DTOs freeze every JSON property name explicitly with `[JsonPropertyName]`; wire-facing roles, states, and archive kinds use validated stable literal strings rather than inheriting either application's enum policy. The bridge registers those types in its existing sole application `Models/JsonContext.cs`; the updater has its own source-generated context because it is a separate application. Explicit wire names override the bridge context's unrelated global `SnakeCaseLower` policy, so changing an application serializer policy cannot silently change the updater protocol.

Protocol compatibility is cross-release, not merely same-build: updater N launches bridge N+1. Keep protocol v1 readable/writable across that boundary, require explicit negotiation or retained previous-protocol support before an incompatible change, and maintain golden bytes for CLI N → updater N plus updater N → candidate bridge N+1 Ready. Reflection serialization is forbidden in both executables.

The installed executable names are `copilot-bridge(.exe)` and `copilot-updater(.exe)`. Before launch, the bridge copies the updater plus the immutable plan into a private per-attempt directory and executes that copy, allowing the installed updater itself to be replaced on Windows.

**Alternative considered:** reuse the bridge executable in a hidden updater mode. Rejected because the helper would load/link the full server and dependencies, be much larger, blur the policy/executor boundary, and make self-replacement/process ownership harder to audit.

### D8 — Use a versioned, immutable, allowlisted update plan

The bridge captures the original argument vector before System.CommandLine loses its distinction, the original working directory, `Environment.ProcessPath`, `AppContext.BaseDirectory`, current PID/start identity, versions, selected asset metadata, phase timeouts, and private attempt/control paths. It writes a versioned JSON plan using source generation, flushes it, then makes it read-only where practical.

The plan lists only these managed install targets for the RID:

```text
copilot-bridge(.exe)
copilot-updater(.exe)
appsettings.json       (special transactional handling)
```

The updater canonicalizes all paths and rejects any managed target outside the canonical install root. Release-derived names never become arbitrary target paths. The plan contains no GitHub OAuth/Copilot token and no full configuration content. Its sole random IPC capability is for the parent/updater `Prepared`–`CutoverAuthorized` handoff. The updater creates a different capability for target Ready and another different capability for rollback Ready; readiness capabilities are never serialized into the original plan or reused across roles.

The updater validates the complete plan and installed state; it never fills missing data or makes a policy choice.

### D9 — Use owner-private per-attempt directories and an install-scoped lock

Create an update root under the current user's local application-data/state directory, not the installation directory or a shared working directory:

```text
<user-state>/copilot-bridge/updates/<attempt-id>/
  plan.json
  updater copy
  transaction.log
  archive
  staging/
  backup/
```

On Unix, directories/files use owner-only modes; on Windows, inherited user-profile ACLs are retained and tightened where available. The attempt ID is cryptographically random.

An exclusive file-handle lock keyed by the canonical install path prevents two updater transactions for one installation. Hold it from preflight through commit/rollback. The lock is process-owned, so a stale path does not permanently block after process death. Before authorizing cutover, the updater verifies that the plan PID still matches start identity and executable/install path; it never kills by process name.

### D10 — Make preparation complete before ownership transfer

The updater performs all operations that can be completed while the parent is alive:

1. validate plan, acquire lock, and open transaction journal;
2. download over HTTPS with a finite timeout and bounded expected size;
3. stream-count and SHA-256-verify against GitHub metadata;
4. extract manually into fresh staging;
5. validate exact required root files and archive entry safety;
6. read the installed configuration once into an immutable byte snapshot, hash it, parse those exact bytes, and generate the staged merged config;
7. copy that same configuration snapshot and every existing managed binary to private backup, recording hashes plus identity-relevant metadata;
8. create/flush same-volume install-temporary files and prove required install-directory writes are possible;
9. revalidate parent identity and every installed managed/config snapshot immediately before handoff;
10. send authenticated `Prepared` to the parent.

Preparation does **not** rename or replace installed managed targets. If any snapshot drift or other step fails, the updater discards stale merged output, sends `PreflightFailed`, releases the lock, and exits; the initiating bridge logs a Warning and continues into normal host startup. A later attempt must start from the then-current installed bytes.

Archive extraction uses `ZipArchive` and `TarReader`/`GZipStream` entry-by-entry instead of convenience `ExtractToDirectory` calls. It rejects absolute/traversing paths, alternate separator escapes, links, devices, duplicate normalized names, and unexpected managed-file placement before writing an entry.

### D11 — Use an authenticated two-phase local control protocol

Use .NET named pipes as the cross-platform local IPC primitive. Pipe names are random per exchange; use current-user-only pipe options where supported. Three independent 256-bit capabilities exist: one CLI-created parent/updater handoff capability, one updater-created target-Ready capability, and a different updater-created rollback-Ready capability. No capability is reused across roles. Messages are small, length-bounded, source-generated JSON records using the frozen wire contract and carrying protocol version, attempt ID, token, sender PID, role, expected version, and state.

**Parent/updater handoff:**

```text
updater ── Prepared ──► parent
updater ◄─ CutoverAuthorized ── parent
parent exits cleanly
updater verifies/waits for exact parent exit
```

The updater SHALL NOT cut over merely because it sent `Prepared`; it needs `CutoverAuthorized`. Therefore if the bridge's bounded wait expires, the pipe breaks, or the parent chooses current-version startup, the updater cannot later surprise a serving process with a cutover. After authorization, the parent returns without constructing Kestrel. The updater first waits for cooperative exit so `ProcessExit` flushes logs, then may force-stop only the same revalidated process after a bounded timeout.

**Replacement/updater readiness:** updater creates a fresh readiness pipe/token before each new or rollback launch and passes the context in child-only environment variables, not persisted configuration and not a shell command. The child reports only once.

A file-marker-only protocol was rejected because polling and stale-file races make ownership/acknowledgment ambiguous. Loopback TCP was rejected because it consumes a port and broadens exposure. Named pipes provide local identity scoping and bidirectional acknowledgment on all supported .NET platforms.

### D12 — Migrate configuration by walking the new template

Read the installed file once into the immutable old-config byte snapshot described in D10 and parse only those bytes; the private rollback copy is made from that same snapshot. Parse both inputs with the same JSON allowances as the configuration provider (including its accepted comments/trailing-comma behavior), but first materialize object-property lists so case-insensitive duplicates can be detected instead of silently overwritten.

The merge walks **only the new tree**, preserving its property spelling/order:

```text
Merge(newNode, oldNode):
  if old node is absent:                 clone new node
  if both nodes are JSON objects:        for each new property,
                                           find old property OrdinalIgnoreCase
                                           recursively Merge(new, old)
  otherwise:                             structural clone of complete old node
```

Consequences:

- new-only property → new default;
- old-only property at an object/object merge frontier → omitted;
- object/object → recursive merge;
- array/array → complete old array (no per-index work);
- any type mismatch → complete old value, including nested old-only content inside that atomic subtree, allowing new startup validation to decide compatibility;
- old `null` → retained `null`.

The result is written to a new staging file and reparsed before preparation succeeds. It is not an edit of the old file. Formatting/comments need not survive migration; values and array structure do. The exact original bytes are separately backed up for rollback.

At cutover, after the parent has exited, first revalidate every managed executable against its prepared snapshot, atomically rename the installed original to `appsettings.json.bak.<attempt-id>`, and hash the renamed file before installing anything. Only a hash match authorizes installation of the separately generated merged file and new managed binaries. On config drift, restore the original name and relaunch the unchanged old bridge; on managed-binary drift, preserve the external change and recovery material and require manual recovery rather than overwriting or executing an unplanned file. Never overwrite a pre-existing unrelated backup name.

### D13 — Treat replacement startup readiness as the commit point

The updater installs the allowlisted bridge/updater and merged configuration, restores Unix executable modes, and launches the bridge with `ProcessStartInfo.ArgumentList` so arguments are not shell-parsed. It sets the exact original working directory, inherits the updater's environment/console handles, and adds one-launch-only attempt/pipe/token/expected-role variables.

Add an update readiness reporter to the bridge host. It does nothing on ordinary launches. On a valid updater launch it registers for `IHostApplicationLifetime.ApplicationStarted`; only then does it send:

```text
Ready(attemptId, token, role, pid, ProductInfo.Version)
```

`ApplicationStarted` occurs after hosted-service startup and Kestrel server start, so route validation, auth setup, and listener binding have completed. This corrects the existing startup banner's limitation: its “listening” log is produced inside a hosted service before it can serve as a transactional readiness proof.

The updater verifies the token, attempt/role, launched PID, expected target version, and continued process liveness. Only a valid replacement Ready commits. Process creation, a log line, elapsed time, or an open PID alone is never sufficient.

### D14 — Roll back exact old files and require old Ready

Every post-cutover failure enters one rollback path:

1. stop only the launched failed replacement and wait for exit — this includes the case where the replacement is *still running* but reported an invalid/wrong-version/wrong-role Ready (a live process holds an image lock on its own executable on Windows, so it MUST be terminated before its file can be restored). The launch/readiness step terminates its own non-successful child rather than leaving it for a later step;
2. restore old bridge/updater from verified private backups;
3. remove the merged installed config;
4. hash `appsettings.json.bak.<attempt-id>` and rename it back only if it matches the immutable old-config snapshot; otherwise restore the separately verified private exact snapshot and retain the suspect sibling for diagnostics;
5. restore executable modes;
6. launch old bridge with original arguments/working directory and a newly generated rollback-Ready capability (distinct from handoff and target Ready) plus one-launch update suppression;
7. require role/version/PID-bound Ready from the expected old version using only that rollback capability.

The restored configuration is byte-for-byte the original, including fields deliberately dropped by a successful migration. A successful rollback is still an update failure (diagnostic/nonzero updater outcome), but service recovery is reported distinctly. If rollback cannot restore files or obtain old Ready, preserve the complete attempt directory and sibling config backup, and print exact manual restoration commands/paths.

The updater writes and flushes a phase journal before and after each destructive transition. This supports diagnosis/manual recovery after abrupt updater death; it does not claim automatic recovery from power loss without a running coordinator.

### D15 — Confine mutation and cleanup

The updater never recursively replaces the installation directory. It touches only:

- fixed managed targets;
- `appsettings.json.bak.<this-attempt>`;
- exact same-directory temporary files created by this attempt;
- its owner-private attempt directory and lock.

It does not enumerate-and-delete unknown install contents. Consequently `github_token.dat`, `log/`, request traces, custom files, and unrelated `.bak` files survive success and rollback. Successful-commit cleanup is best effort and cannot stop the ready replacement; failed transactions retain recovery material.

### D16 — Extend release CI without checksum sidecars

For each RID, CI publishes and smoke-runs both projects, then packages:

```text
copilot-bridge(.exe)
copilot-updater(.exe)
appsettings.json
```

The macOS `.pkg` installs both executables beside the configuration, but auto-update always consumes the TAR.GZ asset and never invokes the package manager. After `gh release create` uploads assets in CI, run a bounded retry/poll against the public GitHub Releases REST endpoint using ordinary HTTP with **no `Authorization` header**. Fail the release job unless every update archive becomes visible anonymously with `uploaded` state, positive size, HTTPS URL, and a usable `sha256:` digest. `gh` remains only the authenticated CI publishing mechanism; neither this anonymous verification nor released executables depend on it.

Track both AOT executable sizes and build times. The updater's narrow dependency graph is an acceptance constraint, not just an aesthetic preference.

### D17 — Test from contracts and prove the process boundary

Use pure seams for clock/console/HTTP/filesystem/process/control-channel interactions so policy and state-machine tests do not mutate a real installation. Tests are derived from the specifications before implementation and mutation-checked as required by project policy.

Verification layers:

1. **Unit contracts:** option defaults, SemVer precedence, channel filtering, pagination, exact asset selection, prompt/noninteractive behavior, plan validation, configuration merge including atomic arrays/null/removed keys, safe-path rules, transaction state transitions, and secret-free diagnostics.
2. **Malicious archive corpus:** ZIP/TAR traversal, absolute names, mixed separators, duplicate normalized paths, symlinks/hard links/devices, oversized/mismatched downloads.
3. **Real-process disposable install:** old and candidate AOT binaries in a non-8765 temporary installation; prove preflight fail-open, successful handoff/Ready/commit, candidate startup failure/timeout, exact config rollback, original argument/cwd preservation, and install lock behavior.
4. **Release matrix:** both executables launch on each supported runner and archive/package shape is correct.
5. **Real client acceptance:** after the replacement Ready, drive a real headless client through the replacement bridge on a complex path-exercising task and read the client's own log/transcript for the verdict, per the repository's mandatory real-client directive.

## Risks / Trade-offs

- **Startup latency and GitHub anonymous quota:** Every normal serve startup makes a synchronous public API request; repeated restarts behind one IP can exhaust 60 requests/hour. → Fail open with a Warning and bounded timeout. Add ETag/cache behavior only if real usage demonstrates a problem.
- **GitHub is the trust root:** Asset digest and bytes come from the same release authority; a compromised repository publisher can replace both metadata and asset. → State the boundary honestly, require HTTPS/digest, and leave independent signing as a future change.
- **Updater crash/power loss after parent exit:** No coordinator can guarantee rollback if it is itself forcibly terminated. → Flush backups and a phase journal before authorization; preserve sibling/private backups and print deterministic recovery paths. A watchdog/service is intentionally outside v1.
- **Port becomes occupied during cutover:** Another process can claim the port after the old process exits, making the replacement fail. → Ready timeout detects this and restores/restarts old Bridge; if the competing process still owns the port, rollback Ready also fails and diagnostics identify the port/startup failure.
- **Configuration type drift:** Preserving an old value whose type no longer matches the new template can make the replacement fail. → This is intentional user-value precedence; replacement validation triggers exact rollback rather than silently discarding the value.
- **Configuration comments/formatting are not preserved after successful migration:** A generated file uses the new template's structure but not necessarily old lexical trivia. → Preserve semantic values/arrays and exact original bytes until commit; document this behavior. Rollback is byte-exact.
- **macOS package permissions:** `/usr/local/copilot-bridge` is commonly not writable by the user who runs the bridge. → Detect before cutover, never elevate, warn with the Release URL, and continue the old proxy; manual package/archive update remains available.
- **Second AOT project increases release time/size:** Each RID compiles twice. → Keep updater framework-only and small; build in the existing per-RID matrix and record binary sizes.
- **Named-pipe/platform differences:** Current-user semantics differ between Windows and Unix implementations. → Combine owner-private state paths, unpredictable names/tokens, strict bounded messages, and process identity checks; cover Windows/Linux/macOS with real-process CI.
- **Original console lifetime:** A parent launched by double-click may cause console behavior to differ when it exits and the child remains. → Launch without shell/new window and inherit handles where the OS allows; verify Windows console behavior in real-process acceptance.

## Migration Plan

1. Add options, pure SemVer/release-selection/config-merge/plan contracts, and contract tests without activating startup behavior.
2. Add the updater project, secure staging/archive handling, transaction journal/lock, and simulated state-machine tests.
3. Add parent/updater and readiness IPC plus disposable real-process success/rollback tests.
4. Wire the pre-host gate into `ServeCommand` and add stock configuration. Confirm all non-serve command tests observe zero update traffic.
5. Update release CI to build/smoke/package both executables and validate GitHub Release Asset digest metadata.
6. AOT-publish locally on Windows, record bridge/updater sizes, then exercise disposable-install update and rollback on CI's remaining OS/RIDs.
7. Run the required real headless client acceptance through a successfully replaced bridge and inspect the client's own evidence.
8. Update README and durable architecture/build documentation; mirror any substantive `CLAUDE.md` build guidance in `AGENTS.md`.

Existing installations without `AutoUpdate` begin checking stable releases on their next new-version startup because the code default is enabled. They retain all old values for keys still present in the new stock configuration. No wire/API migration occurs.

**Feature rollback:** set `AutoUpdate:EnableAutoUpdate=false` to stop checks. Reverting the bridge code/release packaging leaves the extra updater file harmless. A transaction already in progress follows its immutable plan through commit or rollback.

## Open Questions

None remain for the initial specification. Timeout constants and owner-private state-root spelling are implementation constants to choose consistently and cover with tests; they do not add user-facing configuration in v1.
