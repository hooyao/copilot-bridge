## ADDED Requirements

### Requirement: Minimal self-contained updater artifact

Every auto-update archive SHALL contain the bridge executable, a RID-matched Native AOT self-contained `copilot-updater` executable, and the release's default `appsettings.json` at the archive root. The bridge SHALL copy the installed updater to a private temporary attempt directory and execute that copy so Windows can replace the installed updater. The updater SHALL use only platform/.NET APIs and ordinary HTTPS; it SHALL NOT require `gh`, PowerShell, a shell, `curl`, `tar`, `unzip`, or an installed .NET runtime. The macOS `.pkg` SHALL also install the updater beside the bridge so package-based installations have the same future archive-update capability, but the updater SHALL never invoke or modify the package manager or request elevation.

#### Scenario: Release archive contains all managed files
- **WHEN** a supported RID release archive is inspected
- **THEN** its root contains the correctly named bridge, updater, and default `appsettings.json`

#### Scenario: Bare machine can run updater
- **WHEN** the target machine has none of `gh`, PowerShell, curl, tar, unzip, or a .NET runtime installed
- **THEN** the RID-matched updater can download, inspect, and install through its own Native AOT binary and framework APIs

#### Scenario: Installed updater can be replaced on Windows
- **WHEN** an update runs on Windows
- **THEN** the updater executes from the private attempt directory rather than the installed path and can replace the installed updater file

### Requirement: Immutable plan and policy-free executor

The CLI SHALL make every policy decision and serialize a source-generated, versioned update plan before starting the updater. The plan SHALL contain the attempt ID, parent PID and process identity, install/executable/config/updater paths, current and target versions, selected HTTPS asset URL/name/size/SHA-256 digest/archive kind, private staging/backup/control paths, exact managed-file allowlist, original argument vector, original working directory, finite phase timeouts, and only the parent/updater handoff endpoint and capability. Target-Ready and rollback-Ready capabilities SHALL NOT be pre-created or reused from the plan. The updater SHALL validate plan schema, path confinement, current state, and internal consistency but SHALL NOT query releases, choose a channel/version/asset, prompt the user, or infer missing instructions. The plan SHALL contain no GitHub OAuth token, Copilot token, or other application secret.

#### Scenario: Plan contains execution facts but no product policy
- **WHEN** the CLI creates an accepted update plan
- **THEN** the plan fully identifies one already-selected asset and transaction but contains no instruction to discover or choose releases

#### Scenario: Updater receives an incomplete plan
- **WHEN** a required plan field or recognized schema version is absent
- **THEN** the updater fails before cutover and the initiating bridge can continue unchanged

#### Scenario: Plan attempts to escape installation root
- **WHEN** any managed target path resolves outside the canonical installation directory
- **THEN** the updater rejects the plan before downloading or stopping the bridge

#### Scenario: Plan contains no authentication token
- **WHEN** a plan and updater diagnostics are inspected
- **THEN** they contain neither the persisted GitHub token nor a Copilot bearer token

#### Scenario: Readiness capabilities are role-specific
- **WHEN** one accepted update proceeds from parent handoff to target launch and then requires rollback
- **THEN** the parent/updater handoff, target-Ready, and rollback-Ready exchanges use three independently generated capabilities and no capability is reused across roles

### Requirement: Versioned cross-executable wire contract

The CLI, updater, replacement bridge, and rollback bridge SHALL exchange plans and control messages through a frozen, versioned JSON wire contract with explicit property names independent of either executable's unrelated global serializer naming policy. Both Native AOT executables SHALL use source-generated metadata only. A released updater SHALL be able to consume the plan emitted by its same-release CLI and consume Ready from the next candidate bridge; the next candidate bridge SHALL accept the one-launch readiness context emitted by the previous released updater. Future incompatible wire changes SHALL require explicit protocol negotiation or continued support for the previous protocol and SHALL NOT silently change property names or enum representations.

#### Scenario: CLI plan bytes are accepted by updater
- **WHEN** the CLI serializes a valid plan using the frozen wire contract
- **THEN** the same-release updater deserializes the exact bytes under Native AOT without reflection

#### Scenario: Previous updater accepts candidate Ready
- **WHEN** the updater from the installed release launches the next candidate bridge
- **THEN** the candidate accepts the previous updater's one-launch context and emits Ready bytes that the previous updater can deserialize and validate

#### Scenario: Unrelated serializer policy changes
- **WHEN** the bridge application JSON context changes its global naming policy
- **THEN** update-plan and control-message wire names remain unchanged

### Requirement: Single installation transaction and parent identity

The updater SHALL acquire an exclusive lock scoped to the canonical installation directory before preparation and hold it through commit or rollback. It SHALL verify immediately before cutover that the recorded PID still identifies the initiating bridge by matching process start identity and expected executable/install path. It SHALL stop only that process, never processes selected solely by name. Lock contention or identity mismatch SHALL fail before any managed target is replaced.

#### Scenario: Concurrent updater loses the lock
- **WHEN** another update transaction already holds the same installation lock
- **THEN** the second updater fails before cutover and its initiating bridge continues normally

#### Scenario: PID has been reused
- **WHEN** the recorded PID now belongs to a process with a different start identity or executable path
- **THEN** the updater refuses to terminate it and performs no cutover

#### Scenario: Other bridge installation remains untouched
- **WHEN** another `copilot-bridge` process runs from a different installation directory
- **THEN** the updater neither terminates nor modifies that process or directory

### Requirement: Secure download and archive staging

The updater SHALL download exactly the plan URL over HTTPS into the private staging directory with a finite timeout and bounded size, count the received bytes, compute SHA-256 while streaming, and require both the GitHub-reported size and digest to match before extraction. It SHALL extract ZIP or TAR.GZ itself into a fresh staging subtree and accept only the expected regular root files. Absolute paths, parent traversal, alternate path separators used to escape, links/reparse targets, duplicate normalized names, device/special entries, unexpected required-file types, or writes outside staging SHALL be rejected before the parent bridge is stopped.

#### Scenario: Download matches GitHub metadata
- **WHEN** the downloaded byte count and computed SHA-256 equal the immutable plan values
- **THEN** extraction and remaining preflight may proceed

#### Scenario: Digest mismatch aborts safely
- **WHEN** the downloaded archive's computed SHA-256 differs from the plan digest
- **THEN** the updater deletes or quarantines staging data, does not stop the old bridge, and reports pre-cutover failure

#### Scenario: Archive attempts traversal
- **WHEN** an archive entry is absolute or normalizes through `..` outside staging
- **THEN** extraction is rejected and no path outside the private staging directory is written

#### Scenario: Archive contains a symbolic link
- **WHEN** a TAR archive contains a symbolic link, hard link, device, or other non-regular managed entry
- **THEN** the updater rejects the archive before cutover

#### Scenario: Archive has duplicate normalized managed names
- **WHEN** two entries normalize case-insensitively to the same managed path on a case-insensitive platform
- **THEN** the updater rejects the archive rather than choosing one

### Requirement: Template-based configuration migration

Before cutover, the updater SHALL read the installed `appsettings.json` bytes once as an immutable old-config snapshot, hash them, use those exact bytes both for parsing and for the verified private rollback copy, and parse the staged release default as `NewConfig`. It SHALL create a distinct staged `MergedConfig` using `NewConfig` as the complete template. Object properties SHALL match using .NET Configuration's case-insensitive key semantics. At each object/object merge frontier, for every property in `NewConfig`: if no old match exists, the new value SHALL remain; if both values are objects, their properties SHALL merge recursively; otherwise the complete matched old value SHALL replace the new value. Consequently arrays SHALL be atomic, including empty arrays: the complete old array replaces the new array without element/index merge, append, sorting, or deduplication. An old explicit JSON `null` SHALL be preserved as a value. Properties present only in `OldConfig` at an object/object merge frontier SHALL be omitted. When matched nodes have different types, the complete old node SHALL remain atomic, including any nested content inside it, and replacement startup validation SHALL decide whether that value remains acceptable. Duplicate or otherwise ambiguous case-insensitive keys at any object level in either input SHALL fail migration before cutover. The output property spelling and structure SHALL originate from the new template.

#### Scenario: Existing scalar setting preserves operator value
- **WHEN** old `Server.Port` is `19000` and new default `Server.Port` is `8765`
- **THEN** merged `Server.Port` is `19000`

#### Scenario: New setting receives new default
- **WHEN** `Server.RequestTimeoutSeconds` exists only in the new template
- **THEN** the merged configuration contains its new default value

#### Scenario: Removed old setting is dropped
- **WHEN** `RemovedLegacyOption` exists only in the old configuration
- **THEN** it is absent from the merged configuration after a successful update

#### Scenario: Objects merge recursively
- **WHEN** old `Tracing` defines only `Enabled` and new `Tracing` defines `Enabled` plus `Directory`
- **THEN** merged `Tracing.Enabled` uses the old value and `Tracing.Directory` uses the new default

#### Scenario: Locations array is replaced atomically
- **WHEN** both files define `Routing.Locations` with different arrays
- **THEN** the merged configuration contains a structural clone of the entire old `Locations` array and no element from the new array is appended or merged

#### Scenario: Empty old array remains empty
- **WHEN** the old value for an array path is `[]` and the new default array is non-empty
- **THEN** the merged value is `[]`

#### Scenario: Old null overrides new default
- **WHEN** a path exists in both files and the old value is JSON `null`
- **THEN** the merged value is JSON `null`

#### Scenario: Type-mismatched old subtree remains atomic
- **WHEN** the new template defines `Feature` as a scalar but the old configuration defines `Feature` as an object containing an old-only `Legacy` property
- **THEN** the complete old `Feature` object, including `Legacy`, replaces the new scalar because old-only deletion applies only at object/object merge frontiers

#### Scenario: Case-insensitive duplicate is ambiguous
- **WHEN** one input object contains keys such as `Server` and `server`
- **THEN** configuration migration fails before the old bridge or installed configuration is changed

### Requirement: Complete pre-cutover safety

Before declaring cutover-ready, the updater SHALL finish download, integrity validation, extraction, package-shape validation, configuration migration, install-root writability checks, disk-space-relevant writes, private backups of every existing managed binary, an exact byte backup of the immutable old-config snapshot, and creation/validation of all replacement temporary files. The updater SHALL record hashes plus identity-relevant metadata for the old configuration and every managed installed binary, then immediately before reporting cutover-ready revalidate that the installed files still match those snapshots. Any drift SHALL fail preparation and require a new update attempt; partial merged output SHALL never overwrite a later operator edit. No failure in this phase SHALL stop the initiating bridge or replace/rename a managed installed target. Temporary transaction files SHALL be cleaned up best-effort without deleting user files.

#### Scenario: Existing configuration is malformed
- **WHEN** the installed or new default `appsettings.json` cannot be parsed under the bridge's accepted JSON rules
- **THEN** preparation fails, the installed config remains at its original name and bytes, and the initiating bridge continues startup

#### Scenario: Installation directory is not writable
- **WHEN** the updater cannot create and flush its required transaction files in the install directory
- **THEN** it does not request elevation, does not stop the bridge, and reports that manual update is required

#### Scenario: Managed backup cannot be verified
- **WHEN** any existing managed binary or original configuration cannot be copied and read back for rollback
- **THEN** the updater fails before cutover

#### Scenario: Configuration changes during preparation
- **WHEN** the installed `appsettings.json` changes after its immutable snapshot was taken but before the updater reports cutover-ready
- **THEN** the updater discards the stale merged output, performs no cutover, and requires a new update attempt based on the latest configuration

#### Scenario: Managed binary changes during preparation
- **WHEN** an installed managed executable no longer matches its recorded preflight snapshot before cutover-ready
- **THEN** the updater performs no cutover and does not overwrite or restore that externally changed executable

### Requirement: Coordinated cutover and invocation preservation

After preparation succeeds, the updater SHALL publish a per-attempt cutover-ready signal. The initiating bridge SHALL then terminate cleanly before host startup; the updater SHALL wait a bounded interval for the exact parent identity to exit and MAY force-terminate only that verified parent if cooperative exit does not complete. After the parent exits and before installing any new managed binary, the updater SHALL revalidate every managed installed binary against its prepared snapshot, atomically rename the original `appsettings.json` to the unique sibling `appsettings.json.bak.<attempt-id>`, and verify that the renamed file still matches the immutable old-config hash. Only then SHALL it install the allowlisted managed release files and staged merged config, preserve/restore required Unix executable modes, and launch the new bridge as the same user with the original argument vector and working directory. If the renamed configuration does not match, the updater SHALL restore its original name, install no new file, and relaunch the unchanged old bridge when its managed binaries still match their verified snapshots. If a managed binary drifted after ownership transfer, the updater SHALL not overwrite or execute that unplanned file; it SHALL retain recovery material and report manual recovery rather than destroying the external change. Arguments SHALL be passed structurally, not through a shell command string. The updater SHALL inherit the existing environment and console/standard handles as applicable, adding only one-launch update context.

#### Scenario: No-argument invocation remains no-argument
- **WHEN** the original bridge was launched without a subcommand
- **THEN** the replacement is launched with the same empty argument vector and same working directory

#### Scenario: Explicit serve arguments are preserved
- **WHEN** the original invocation was `serve --port 18765`
- **THEN** the replacement receives distinct arguments `serve`, `--port`, and `18765` in that order

#### Scenario: Original configuration is renamed before merged install
- **WHEN** cutover starts and the atomically renamed original still matches the immutable old-config snapshot
- **THEN** the original config remains as `appsettings.json.bak.<attempt-id>` and the separately generated merged file becomes `appsettings.json`

#### Scenario: Configuration changes after Prepared
- **WHEN** the operator changes `appsettings.json` after Prepared but before the parent exits and cutover revalidation completes
- **THEN** the updater restores the changed file to its original name, installs no new managed file, and relaunches the unchanged old bridge rather than committing stale merged output

#### Scenario: Managed binary changes after ownership transfer
- **WHEN** an installed managed executable no longer matches its prepared snapshot after the parent exits
- **THEN** the updater does not overwrite or execute that unplanned file, retains recovery material, and reports manual recovery

#### Scenario: Unknown install file is untouched
- **WHEN** the install directory contains a user-created file not in the managed allowlist
- **THEN** cutover neither deletes nor modifies that file

### Requirement: Ready-authenticated commit

Starting a replacement process SHALL NOT by itself commit the update. For each replacement or rollback launch, the updater SHALL create private one-launch readiness context containing an unpredictable token and attempt identity. The bridge SHALL report Ready only after configuration and route validation, authentication setup, hosted-service startup, and successful proxy listener startup, using a local per-attempt signal that includes the expected token, PID, product version, and attempt ID. The updater SHALL use a finite readiness timeout, verify that the signal belongs to the process it launched and that the process remains alive, and commit only when the new target version reports Ready. It SHALL keep all rollback material until commit.

#### Scenario: New process starts and immediately exits
- **WHEN** process creation succeeds but the new bridge exits before reporting Ready
- **THEN** the update is not committed and rollback begins

#### Scenario: New bridge cannot bind the configured port
- **WHEN** the replacement fails listener startup
- **THEN** no valid Ready signal is emitted and the updater rolls back

#### Scenario: Stale or forged readiness marker is present
- **WHEN** a readiness signal has the wrong token, attempt ID, PID, or version
- **THEN** the updater ignores or rejects it and does not commit

#### Scenario: Replacement becomes ready
- **WHEN** the launched target version reports a valid Ready signal and remains alive
- **THEN** the updater commits, removes transaction backups best-effort, and leaves the new bridge serving

#### Scenario: Readiness deadline expires
- **WHEN** the replacement remains alive but does not report Ready before the finite deadline
- **THEN** the updater terminates that replacement and rolls back

### Requirement: Full rollback to exact old installation

Any post-cutover installation error, new-process creation error, premature exit, invalid Ready signal, or readiness timeout SHALL trigger rollback. The updater SHALL stop only the failed replacement, restore every old managed binary from the verified backup, remove the merged installed configuration, verify `appsettings.json.bak.<attempt-id>` against the immutable old-config hash, and rename it back to `appsettings.json`. If that sibling backup is missing or no longer hash-equivalent, the updater SHALL restore the separately verified private exact snapshot instead and retain the suspect sibling for diagnostics. It SHALL restore required modes and relaunch the old version with the original arguments and working directory plus one-launch update suppression and the independently generated rollback-Ready capability. The old configuration after restoration SHALL be byte-for-byte identical to its pre-update content, including old-only fields that a successful migration would remove. Rollback success SHALL require a valid role/version/PID-bound Ready signal from the restored old version using only the rollback capability; rollback failure SHALL preserve all recoverable backups and diagnostics.

#### Scenario: Merged configuration is valid JSON but rejected by new bridge
- **WHEN** the new bridge fails startup validation with the merged configuration
- **THEN** rollback restores the exact original configuration and old binaries before relaunching the old bridge

#### Scenario: Old-only fields return on rollback
- **WHEN** migration omitted an old-only field and replacement startup then fails
- **THEN** the restored old `appsettings.json` contains that field exactly as before

#### Scenario: Rollback launch skips repeated update
- **WHEN** the updater relaunches the restored old bridge
- **THEN** that launch skips update discovery once and cannot immediately retry the failed release

#### Scenario: Restored old bridge becomes ready
- **WHEN** the old bridge reports a valid rollback Ready signal
- **THEN** the updater reports that update failed but service recovery succeeded and then exits non-successfully for the update transaction

#### Scenario: Rollback also fails
- **WHEN** old files cannot be restored or the restored old bridge does not become Ready
- **THEN** the updater leaves backups and the transaction log intact and prints explicit manual recovery paths and steps

### Requirement: Unmanaged state preservation

The update transaction SHALL modify only the managed bridge executable, managed updater executable, and transactional `appsettings.json` paths named in the validated allowlist. It SHALL preserve `github_token.dat`, `log/`, the configured/default request-trace directories, unrelated `.bak` files, and every unknown user file or directory. Cleanup SHALL be confined to the private attempt directory and exact transaction-temporary sibling paths created by that attempt.

#### Scenario: Token and telemetry state survive success
- **WHEN** an update succeeds in an installation containing `github_token.dat`, logs, and request traces
- **THEN** those paths and their contents are unchanged by the updater

#### Scenario: Token and telemetry state survive rollback
- **WHEN** an update cuts over and then rolls back
- **THEN** token, log, trace, and unknown user paths remain unchanged

#### Scenario: Pre-existing backup is not overwritten
- **WHEN** the install directory already contains unrelated `.bak` files
- **THEN** the unique attempt backup name does not overwrite or delete them

### Requirement: Durable transaction diagnostics and bounded cleanup

The updater SHALL write an owner-private per-attempt transaction log outside the managed install files, recording phase transitions and concise failures without authentication secrets or full configuration contents. Before commit, diagnostics and backups SHALL remain available. After successful commit, temporary plan, archive, staging, and backups SHALL be deleted best-effort; cleanup failure SHALL not stop the ready new bridge. After update or rollback failure, the updater SHALL retain recovery material and print current/target versions, install path, backup path, original-config backup path, failure phase/reason, and manual restoration steps.

#### Scenario: Successful update cleanup fails
- **WHEN** the new bridge is Ready but one temporary file cannot be deleted
- **THEN** the update remains committed, the new bridge keeps serving, and cleanup failure is logged as non-fatal

#### Scenario: Recovery needs operator action
- **WHEN** automatic rollback cannot restore service
- **THEN** stderr and the transaction log identify the exact retained backup/config paths and ordered manual recovery procedure

### Requirement: Release workflow and real-process acceptance

Release automation SHALL AOT-publish both executables for `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64`; smoke both; package both with stock configuration in each ZIP/TAR.GZ update archive; and attach those archives to the GitHub Release so GitHub supplies Release Asset size/digest metadata. No checksum sidecar SHALL be required. Acceptance SHALL include contract-derived unit tests, malicious-archive tests, real-process prepare/cutover/Ready/rollback tests against disposable installations, Native AOT execution on each release OS/RID, and a real headless client completing a path-exercising task through the replacement bridge with the client's own evidence confirming successful operation.

#### Scenario: Release assets expose GitHub digest anonymously
- **WHEN** a release archive is uploaded as a GitHub Release Asset
- **THEN** a bounded public REST poll sent without an `Authorization` header observes uploaded state, archive name, positive size, HTTPS download URL, and usable SHA-256 digest consumed by the CLI

#### Scenario: Successful update serves a real client
- **WHEN** a disposable old installation updates to the candidate release and reports Ready
- **THEN** a real headless client completes a complex task through that replacement and its own log/transcript shows successful execution

#### Scenario: Broken candidate proves rollback
- **WHEN** a disposable release candidate intentionally fails before Ready
- **THEN** the real updater restores the old executable and exact original config and the restored proxy becomes Ready
