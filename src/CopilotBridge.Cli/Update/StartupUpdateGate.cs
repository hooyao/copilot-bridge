using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.Options;
using Serilog;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// The decision the serve gate returns: either the current bridge should build
/// its host and serve (the common case, including every fail-open path), or the
/// updater has taken ownership and this process must exit cleanly WITHOUT
/// starting Kestrel.
/// </summary>
internal enum UpdateGateDecision
{
    ContinueCurrentVersion,
    HandedOffToUpdater,
}

/// <summary>
/// The serve-only startup update gate. Runs once, synchronously, BEFORE the
/// proxy host is constructed, and only for <c>serve</c> (explicit or the
/// parameterless default action). Every network/parse/policy failure is
/// fail-open: it logs a Warning and returns
/// <see cref="UpdateGateDecision.ContinueCurrentVersion"/>. It owns every
/// decision and hands the updater a complete immutable plan; the updater makes
/// no policy choice.
/// </summary>
internal sealed class StartupUpdateGate
{
    // Internal bounded timeouts (not user-facing settings in v1).
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(45);
    private const int DownloadTimeoutMs = 120_000;
    private const int ParentExitTimeoutMs = 30_000;
    private const int ReadyTimeoutMs = 120_000;

    private readonly AutoUpdateOptions _options;
    private readonly IReadOnlyList<string> _originalArgs;
    private readonly IUpdateConsole _console;

    public StartupUpdateGate(
        IOptions<AutoUpdateOptions> options,
        IReadOnlyList<string> originalArgs,
        IUpdateConsole? console = null)
    {
        _options = options.Value;
        _originalArgs = originalArgs;
        _console = console ?? new SystemUpdateConsole();
    }

    /// <summary>Run the gate. Never throws for a discovery/policy problem — fail-open.</summary>
    public async Task<UpdateGateDecision> RunAsync(CancellationToken ct)
    {
        // A one-launch replacement/rollback context suppresses the check entirely.
        if (UpdateLaunchContext.FromEnvironment(Environment.GetEnvironmentVariable) is not null)
        {
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        if (!_options.EnableAutoUpdate)
        {
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        if (!SemanticVersion.TryParse(ProductInfo.Version, out var installed))
        {
            Log.Warning("Auto-update: cannot parse installed version {Version}; starting current version.",
                ProductInfo.Version);
            return UpdateGateDecision.ContinueCurrentVersion;
        }
        if (installed.IsDevBuild)
        {
            Log.Information("Auto-update: skipped for development build {Version}.", ProductInfo.Version);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        // Discover releases (anonymous, bounded).
        ReleaseDiscoveryResult discovery;
        try
        {
            using var handler = UpdateDownloader.CreateDefaultHandler();
            using var http = new HttpClient(handler);
            var client = new GitHubReleaseClient(http, PerRequestTimeout, OverallDeadline);
            discovery = await client.DiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown stays shutdown
        }

        if (!discovery.Succeeded)
        {
            Log.Warning("Auto-update check failed ({Reason}); copilot-bridge will continue with the current version.",
                discovery.FailureReason);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        // Select the target.
        var candidates = discovery.Releases
            .Select(r => new ReleaseCandidate(r.TagName, r.Draft, r.Prerelease))
            .ToList();
        var selected = ReleaseSelector.Select(installed, candidates, _options.AllowBetaUpdates);
        if (selected is null)
        {
            Log.Information("Auto-update: already on the latest {Channel} version {Version}.",
                _options.AllowBetaUpdates ? "eligible" : "stable", ProductInfo.Version);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        var release = discovery.Releases.First(r =>
            string.Equals(r.TagName, selected.Candidate.Tag, StringComparison.Ordinal));
        var targetVersion = selected.Version.ToString();
        var rid = UpdateAssetSelector.CurrentRid();
        if (rid is null)
        {
            Log.Warning("Auto-update: no update package for this platform; continuing with the current version.");
            return UpdateGateDecision.ContinueCurrentVersion;
        }
        var asset = UpdateAssetSelector.Resolve(release, targetVersion, rid);
        if (asset is null)
        {
            Log.Warning("Auto-update: release {Tag} has no usable {Rid} asset; continuing with the current version.",
                selected.Candidate.Tag, rid);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        // Announce + consent.
        var consented = UpdatePrompt.Announce(
            _console,
            ProductInfo.Version,
            targetVersion,
            selected.Candidate.IsPreRelease,
            release.Name,
            release.PublishedAt,
            release.Body,
            release.HtmlUrl ?? $"https://github.com/hooyao/copilot-bridge/releases/tag/{release.TagName}");
        if (!consented)
        {
            Log.Information("Auto-update: user declined or non-interactive; continuing with the current version.");
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        // Build the plan + launch the updater. Any failure here is still fail-open.
        try
        {
            return await HandoffAsync(installed, selected, asset, targetVersion, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning("Auto-update handoff failed ({Error}); continuing with the current version.",
                ex.GetType().Name);
            return UpdateGateDecision.ContinueCurrentVersion;
        }
    }

    private async Task<UpdateGateDecision> HandoffAsync(
        SemanticVersion installed, SelectedRelease selected, ResolvedAsset asset, string targetVersion, CancellationToken ct)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("no process path");
        var updaterName = OperatingSystem.IsWindows() ? "copilot-updater.exe" : "copilot-updater";
        var installedUpdater = Path.Combine(installDir, updaterName);
        if (!File.Exists(installedUpdater))
        {
            Log.Warning("Auto-update: updater executable not found next to the bridge; continuing with the current version.");
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        var attemptId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var attemptRoot = Path.Combine(UpdateRoot(), attemptId);
        CreateOwnerOnlyDirectory(attemptRoot);

        // Copy the updater to the private attempt dir so Windows can replace the
        // installed updater during cutover.
        var updaterCopy = Path.Combine(attemptRoot, updaterName);
        File.Copy(installedUpdater, updaterCopy, overwrite: true);

        var handoff = UpdateCapability.Create(attemptId, "handoff");
        var configName = Path.GetFileName(BridgeConfigurationExtensions.ConfigFileName);
        var managed = new List<string>
        {
            OperatingSystem.IsWindows() ? "copilot-bridge.exe" : "copilot-bridge",
            updaterName,
            configName,
        };

        var plan = new UpdatePlan
        {
            AttemptId = attemptId,
            ParentPid = Environment.ProcessId,
            ParentStartTicks = ProcessIdentity.CurrentStartTicks(),
            InstallDir = installDir,
            BridgeExePath = exePath,
            UpdaterExePath = installedUpdater,
            ConfigPath = Path.Combine(installDir, configName),
            CurrentVersion = ProductInfo.Version,
            TargetVersion = targetVersion,
            AssetName = asset.AssetName,
            AssetUrl = asset.DownloadUrl,
            AssetSize = asset.Size,
            AssetSha256 = asset.Sha256Hex,
            ArchiveKind = asset.Kind.ToWire(),
            StagingDir = Path.Combine(attemptRoot, "staging"),
            BackupDir = Path.Combine(attemptRoot, "backup"),
            ArchivePath = Path.Combine(attemptRoot, asset.AssetName),
            JournalPath = Path.Combine(attemptRoot, "transaction.log"),
            ManagedFiles = managed,
            OriginalArgs = _originalArgs.ToList(),
            WorkingDirectory = Environment.CurrentDirectory,
            DownloadTimeoutMs = DownloadTimeoutMs,
            ParentExitTimeoutMs = ParentExitTimeoutMs,
            ReadyTimeoutMs = ReadyTimeoutMs,
            HandoffPipe = handoff.PipeName,
            HandoffToken = handoff.Token,
        };

        var planPath = Path.Combine(attemptRoot, "plan.json");
        await File.WriteAllTextAsync(
            planPath, JsonSerializer.Serialize(plan, UpdateWireJsonContext.Default.UpdatePlan), ct)
            .ConfigureAwait(false);
        // The plan carries the handoff capability token — restrict it to the owner
        // and make it read-only after flushing so another local account can't read
        // or tamper with it.
        HardenPlanFile(planPath);

        // Launch the updater copy and wait (bounded) for its Prepared message.
        var psi = new ProcessStartInfo
        {
            FileName = updaterCopy,
            UseShellExecute = false,
            WorkingDirectory = attemptRoot,
        };
        psi.ArgumentList.Add(planPath);

        Process updater;
        try
        {
            updater = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Log.Warning("Auto-update: could not start the updater ({Error}); continuing with the current version.",
                ex.GetType().Name);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        Log.Information("Auto-update: preparing {Version} in the background…", targetVersion);

        // The updater owns the handoff pipe SERVER; the parent is the CLIENT. In
        // one connection: read the updater's control line, and (only for a valid
        // Prepared) reply with CutoverAuthorized on the same connection.
        UpdateControlMessage? prepared = null;
        var received = await UpdatePipeTransport.ClientExchangeAsync(
            handoff.PipeName,
            makeReply: line =>
            {
                var msg = UpdatePipeCodec.DecodeControl(line);
                prepared = msg;
                var valid = msg is not null
                    && msg.ProtocolVersion == UpdateWire.ProtocolVersion
                    && msg.Kind == UpdateWire.MsgPrepared
                    && string.Equals(msg.AttemptId, attemptId, StringComparison.Ordinal)
                    && string.Equals(msg.Token, handoff.Token, StringComparison.Ordinal)
                    // Bind to the exact updater process we launched — a message from
                    // any other process holding copied capability material is rejected.
                    && msg.SenderPid == updater.Id;
                if (!valid)
                {
                    return null; // no authorization
                }
                var authorize = new UpdateControlMessage
                {
                    Kind = UpdateWire.MsgCutoverAuthorized,
                    AttemptId = attemptId,
                    Token = handoff.Token,
                    SenderPid = Environment.ProcessId,
                };
                return UpdatePipeCodec.EncodeControl(authorize);
            },
            TimeSpan.FromMilliseconds(DownloadTimeoutMs + 60_000),
            ct).ConfigureAwait(false);

        var authorized = received is not null
            && prepared is not null
            && prepared.ProtocolVersion == UpdateWire.ProtocolVersion
            && prepared.Kind == UpdateWire.MsgPrepared
            && string.Equals(prepared.AttemptId, attemptId, StringComparison.Ordinal)
            && string.Equals(prepared.Token, handoff.Token, StringComparison.Ordinal)
            && prepared.SenderPid == updater.Id;

        if (!authorized)
        {
            // Preflight failed / updater exited / timeout — stay on current version.
            var detail = prepared?.Kind == UpdateWire.MsgPreflightFailed ? prepared.Detail : "no cutover-ready signal";
            Log.Warning("Auto-update did not reach cutover ({Reason}); continuing with the current version.", detail);
            return UpdateGateDecision.ContinueCurrentVersion;
        }

        Log.Information("Auto-update: installing {Version}. copilot-bridge will restart automatically.", targetVersion);
        return UpdateGateDecision.HandedOffToUpdater;
    }

    private static string UpdateRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.GetTempPath();
        }
        return Path.Combine(baseDir, "copilot-bridge", "updates");
    }

    // Create a directory owner-only where the OS supports it. On Unix that is
    // 0700 (the attempt dir + plan hold the handoff capability, so they must not
    // be world-readable under a typical 0022 umask); on Windows the inherited
    // user-profile ACLs already scope LocalApplicationData to the user.
    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // A filesystem without mode support keeps its defaults; the plan file
            // hardening below is the second layer.
        }
    }

    private static void HardenPlanFile(string planPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(planPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            // Read-only after flush on every platform (best-effort).
            File.SetAttributes(planPath, File.GetAttributes(planPath) | FileAttributes.ReadOnly);
        }
        catch
        {
            // Best-effort hardening — a failure here does not abort the update.
        }
    }
}
