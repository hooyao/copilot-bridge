# startup-update-discovery Specification

## Purpose

Decide, at bridge startup and only for the proxy (`serve`), whether a newer
release should be installed — safely and without ever blocking service. This
capability owns the serve-only synchronous update gate, its configuration
defaults (`AutoUpdate:EnableAutoUpdate`, `AutoUpdate:AllowBetaUpdates`),
anonymous GitHub Releases discovery under finite bounds, SemVer/channel
selection, exact per-RID asset selection, release-note presentation, interactive
consent, and the fail-open guarantee that any discovery/policy failure logs a
warning and continues the current version. It hands an accepted update off to
`transactional-self-update`; it never installs anything itself.

## Requirements
### Requirement: Serve-only startup update gate

The bridge SHALL run one synchronous update gate before proxy-host construction only for the explicit `serve` command and the parameterless root action that defaults to `serve`. `AutoUpdate:EnableAutoUpdate` SHALL default to `true` when the section or property is absent, and `AutoUpdate:AllowBetaUpdates` SHALL default to `false`. Help, version, auth, debug, config, and every other non-serve action SHALL perform no update request. A one-launch internal recovery/replacement context SHALL suppress the gate without changing persisted configuration. Development builds whose parsed prerelease identifiers include `dev` SHALL not self-update.

#### Scenario: Parameterless startup checks once
- **WHEN** the user launches `copilot-bridge` without a subcommand and auto-update is enabled
- **THEN** the bridge completes one update gate before constructing or starting the proxy host

#### Scenario: Explicit serve checks once
- **WHEN** the user launches `copilot-bridge serve --port 18765` and auto-update is enabled
- **THEN** the bridge completes one update gate before the proxy binds port 18765

#### Scenario: Missing configuration uses enabled stable defaults
- **WHEN** the installed configuration has no `AutoUpdate` section
- **THEN** update checking is enabled and prerelease releases are excluded

#### Scenario: Disabled update performs no network call
- **WHEN** `AutoUpdate:EnableAutoUpdate` is `false`
- **THEN** the bridge starts the proxy without making a GitHub update request

#### Scenario: Maintenance command is isolated
- **WHEN** the user invokes help, version, auth, debug, or config functionality
- **THEN** no update request, release prompt, or updater process is produced

#### Scenario: Replacement launch skips the gate once
- **WHEN** an updater starts a bridge with valid one-launch replacement or rollback context
- **THEN** that launch makes no update request and the next ordinary launch again follows persisted configuration

#### Scenario: Development build does not replace itself
- **WHEN** the running product version has a `dev` prerelease identifier
- **THEN** the bridge reports at Information or Debug level that self-update is skipped and continues startup

### Requirement: Anonymous GitHub release discovery

The bridge SHALL use its own AOT-compatible HTTP client to query the public GitHub Releases REST API anonymously. It SHALL send the GitHub-required user-agent/API headers and paginate until the published release set is proven exhausted. One monotonic wall-clock deadline SHALL bound the complete discovery traversal, in addition to finite per-request timeouts. The client SHALL detect repeated pagination targets/pages and enforce a defensive maximum page count; reaching any overall bound before proving exhaustion SHALL discard all partial release results and fail open. Neither the bridge nor updater SHALL invoke or depend on the `gh` executable or another external downloader. DNS, TLS, HTTP, API rate-limit, per-request timeout, overall deadline, pagination-cycle/page-limit, cancellation unrelated to application shutdown, or JSON/schema failures SHALL be fail-open update-check failures: the bridge SHALL log a Warning and continue starting the current proxy without modifying installation files.

#### Scenario: Public API works without GitHub CLI
- **WHEN** the machine has no `gh` executable and GitHub's public Releases API returns valid anonymous responses
- **THEN** the bridge discovers releases using ordinary HTTPS and continues the update decision normally

#### Scenario: More than one page is considered
- **WHEN** eligible releases extend beyond the first API page and traversal proves exhaustion within its bounds
- **THEN** the bridge follows pagination and compares all published releases rather than silently capping discovery

#### Scenario: Repeated pagination target fails open
- **WHEN** GitHub pagination repeats a previously visited next target or page
- **THEN** the bridge discards the partial release set, logs a Warning, and starts the current proxy

#### Scenario: Overall discovery deadline expires
- **WHEN** individual pages respond within their request timeouts but the complete traversal does not prove exhaustion before the overall deadline or defensive page limit
- **THEN** the bridge cancels traversal, discards all partial results, logs a Warning, and starts the current proxy

#### Scenario: Anonymous rate limit is exhausted
- **WHEN** GitHub returns a rate-limit response to the anonymous request
- **THEN** the bridge logs a Warning that update checking failed and starts the current proxy

#### Scenario: Discovery times out
- **WHEN** the release request does not finish within its finite timeout
- **THEN** the bridge cancels the request, logs a Warning, and starts the current proxy

#### Scenario: Application shutdown cancels discovery
- **WHEN** the user cancels startup while discovery is in progress
- **THEN** cancellation propagates as shutdown rather than being converted into a warning followed by proxy startup

### Requirement: Semantic version and channel selection

The bridge SHALL parse the installed product version and `v<semantic-version>` release tags using Semantic Versioning 2.0 precedence, ignoring build metadata for precedence. Draft releases and tags that are not valid supported semantic versions SHALL not be candidates. With `AllowBetaUpdates=false`, only non-prerelease releases SHALL be candidates. With `AllowBetaUpdates=true`, both stable and GitHub prerelease releases SHALL be candidates. The highest semantic version in the allowed set SHALL be selected, independent of publication order, and installation SHALL be offered only when that version is strictly greater than the installed version.

#### Scenario: Stable channel ignores a newer beta
- **WHEN** the installed version is `1.0.0`, releases include `v1.0.1` and prerelease `v1.1.0-beta.1`, and beta updates are disabled
- **THEN** `1.0.1` is selected

#### Scenario: Beta channel selects highest semantic version
- **WHEN** the installed version is `1.0.0`, releases include `v1.0.1` and prerelease `v1.1.0-beta.1`, and beta updates are allowed
- **THEN** `1.1.0-beta.1` is selected

#### Scenario: Stable supersedes its prerelease
- **WHEN** the installed version is `1.1.0-beta.2` and stable `v1.1.0` is allowed
- **THEN** stable `1.1.0` is considered newer and is selected

#### Scenario: No downgrade from a higher prerelease line
- **WHEN** the installed version is `2.0.0-beta.1`, beta updates are disabled, and the highest stable release is `1.9.9`
- **THEN** no update is offered

#### Scenario: Publication time does not override precedence
- **WHEN** a more recently published release has a lower semantic version than another allowed release
- **THEN** the higher semantic version is selected

#### Scenario: Current or newer installation is not reinstalled
- **WHEN** no allowed release has semantic precedence greater than the installed version
- **THEN** the bridge does not prompt or start the updater and continues proxy startup

### Requirement: Exact RID update asset selection

For an eligible release, the bridge SHALL derive one supported runtime identifier from the current OS and process architecture and select exactly one uploaded archive whose name matches `copilot-bridge-<version>-<rid>.zip` on Windows or `copilot-bridge-<version>-<rid>.tar.gz` on Linux/macOS. Supported identifiers SHALL be `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64`; macOS auto-update SHALL use the tar archive, never the `.pkg`. The selected asset SHALL have an HTTPS browser download URL, positive size, and a GitHub Release Asset digest in supported `sha256:<hex>` form. Missing, duplicate, incomplete, unsupported-platform, or unsafe asset metadata SHALL produce a Warning and current-version startup, not a partial update.

#### Scenario: Windows x64 selects the exact zip
- **WHEN** the bridge runs as a Windows x64 process and release `1.2.3` contains the normal multi-RID assets
- **THEN** it selects only `copilot-bridge-1.2.3-win-x64.zip`

#### Scenario: macOS selects archive rather than installer
- **WHEN** the bridge runs as a macOS ARM64 process and the release contains both `.tar.gz` and `.pkg` assets
- **THEN** it selects `copilot-bridge-<version>-osx-arm64.tar.gz` and never attempts to run the package installer

#### Scenario: Digest is taken from GitHub metadata
- **WHEN** the selected GitHub Release Asset has `digest="sha256:<64-hex-digits>"`
- **THEN** the bridge records that digest and the reported size in the immutable update plan without seeking a checksum sidecar

#### Scenario: Digest is missing or unsupported
- **WHEN** the matching asset has a null digest or a digest algorithm other than SHA-256
- **THEN** automatic installation is not offered and the current proxy starts after a Warning

#### Scenario: Runtime is unsupported
- **WHEN** the process OS/architecture has no published update RID
- **THEN** the bridge warns that automatic update is unavailable and continues with the installed version

### Requirement: Release presentation and explicit consent

When a newer eligible release and valid asset are selected, the bridge SHALL print the installed version, available version, stable/prerelease channel, release title, publication time, complete release body, and release page URL before prompting `Install this update now? [y/N]`. An empty release body SHALL be represented explicitly. Only case-insensitive `y` or `yes` after trimming SHALL grant consent; empty or any other answer SHALL decline. Declining SHALL change no installation file and SHALL continue current proxy startup. If standard input is redirected, unavailable, or fails while prompting, the bridge SHALL never install automatically or wait indefinitely; it SHALL warn and continue current startup.

#### Scenario: Full release notes precede prompt
- **WHEN** a newer eligible release contains a non-empty multi-line release body
- **THEN** the entire body and release URL are printed before the confirmation prompt

#### Scenario: Empty notes are explicit
- **WHEN** the selected release body is empty or null
- **THEN** the bridge prints `No release notes were provided for this release.` before prompting

#### Scenario: Explicit yes accepts
- **WHEN** an interactive user enters `y` or `yes` in any letter case
- **THEN** the bridge starts the planned updater handoff

#### Scenario: Default answer declines
- **WHEN** the interactive user presses Enter without text
- **THEN** no updater is started and the current proxy starts

#### Scenario: Noninteractive startup never installs
- **WHEN** standard input is redirected or unavailable and a newer release exists
- **THEN** release information is printed, installation is skipped with a Warning, and the current proxy starts without reading indefinitely

### Requirement: Fail-open updater handoff before cutover

After consent, the bridge SHALL create a private per-attempt immutable plan and start the RID-matched updater copy from a temporary directory. The bridge SHALL wait synchronously for either a bounded pre-cutover-ready signal or updater failure. If the updater cannot start, exits, times out, or reports failure before declaring cutover-ready, the initiating bridge SHALL remain unmodified and continue proxy startup. Once cutover-ready is authenticated for that attempt, the initiating bridge SHALL end its own startup cleanly so the updater can replace it; it SHALL not also start the old proxy.

#### Scenario: Updater executable cannot start
- **WHEN** consent was granted but the temporary updater process cannot be created
- **THEN** the bridge logs a Warning and starts the current proxy without modifying installation files

#### Scenario: Download fails before cutover
- **WHEN** the updater reports that archive download or validation failed before cutover-ready
- **THEN** the old bridge remains installed and the initiating process continues proxy startup

#### Scenario: Prepared updater takes ownership
- **WHEN** the updater authenticates a cutover-ready signal after all preflight work succeeds
- **THEN** the initiating bridge exits cleanly without starting Kestrel and the updater owns installation, restart, and rollback

### Requirement: Update startup observability

The update path SHALL emit operator-readable structured events for check start, disabled/skip decisions, current status, selected release, consent result, noninteractive skip, check failure, updater start, pre-cutover failure, and ownership handoff. Every fail-open Warning SHALL state that the current bridge will continue. Release-derived text SHALL be treated as data and terminal control characters other than normal line formatting SHALL not be executed or emitted verbatim.

#### Scenario: Check failure explains continuation
- **WHEN** any fail-open discovery error occurs
- **THEN** the Warning identifies the concise failure reason and states that the installed version will continue starting

#### Scenario: Release text contains control characters
- **WHEN** a release title or body contains terminal escape/control characters
- **THEN** the bridge renders a safe textual representation while preserving normal multi-line release-note content

