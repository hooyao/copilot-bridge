using System.Diagnostics;
using System.Text.Json;
using CopilotBridge.Update.Wire;

namespace CopilotBridge.Updater;

/// <summary>
/// Stable process exit outcomes for the updater. The bridge and tests read these
/// to distinguish a fail-open (nothing changed), a committed update, a recovered
/// rollback (update failed but service restored), and an unrecovered failure
/// (manual recovery required).
/// </summary>
internal enum UpdaterExit
{
    PreflightFailed = 10,   // nothing installed; old bridge continues
    Committed = 0,          // new bridge is serving
    RolledBack = 20,        // update failed, old bridge restored and serving
    Unrecovered = 30,       // update failed and rollback could not restore service
}

/// <summary>
/// Drives one update transaction end to end. All external effects — HTTP,
/// filesystem, process launch, IPC — go through the shared modules, so the phase
/// ordering here is what disposable real-process tests exercise. The engine
/// never makes a product-policy decision; it only executes the immutable plan.
/// </summary>
internal sealed class UpdaterEngine
{
    private readonly UpdatePlan _plan;
    private readonly TransactionJournal _journal;
    private readonly TextWriter _stderr;

    public UpdaterEngine(UpdatePlan plan, TransactionJournal journal, TextWriter stderr)
    {
        _plan = plan;
        _journal = journal;
        _stderr = stderr;
    }

    public async Task<UpdaterExit> RunAsync(CancellationToken ct)
    {
        // 1. Validate plan + acquire the install lock.
        var validate = UpdatePlanValidator.Validate(_plan);
        if (!validate.Ok)
        {
            return await FailPreflightAsync($"invalid plan: {validate.Reason}", ct).ConfigureAwait(false);
        }

        var lockRoot = Path.GetDirectoryName(_plan.StagingDir) ?? _plan.StagingDir;
        using var installLock = InstallationLock.TryAcquire(_plan.InstallDir, lockRoot);
        if (installLock is null)
        {
            return await FailPreflightAsync("another update is in progress", ct).ConfigureAwait(false);
        }

        var install = new ManagedInstallManager(_plan, _journal);

        // 2. Download + verify + extract. If the archive is already present and
        //    already verifies against the plan's size+digest (e.g. a re-run after
        //    an interrupted transaction), skip re-downloading — the bytes are the
        //    exact same trusted asset.
        var alreadyVerified = File.Exists(_plan.ArchivePath)
            && (await ArchiveExtractor.VerifyAsync(
                    _plan.ArchivePath, _plan.AssetSize, _plan.AssetSha256, ct).ConfigureAwait(false)).Ok;

        if (!alreadyVerified)
        {
            _journal.Write("download.begin");
            using var handler = UpdateDownloader.CreateDefaultHandler();
            using var http = new HttpClient(handler);
            var downloader = new UpdateDownloader(http);
            var dl = await downloader.DownloadAsync(
                _plan.AssetUrl, _plan.ArchivePath,
                TimeSpan.FromMilliseconds(_plan.DownloadTimeoutMs), ct).ConfigureAwait(false);
            if (!dl.Ok)
            {
                return await FailPreflightAsync(dl.Reason!, ct).ConfigureAwait(false);
            }

            var verify = await ArchiveExtractor.VerifyAsync(
                _plan.ArchivePath, _plan.AssetSize, _plan.AssetSha256, ct).ConfigureAwait(false);
            if (!verify.Ok)
            {
                return await FailPreflightAsync(verify.Reason!, ct).ConfigureAwait(false);
            }
        }

        var extract = ArchiveExtractor.Extract(_plan.ArchivePath, _plan.ArchiveKind, _plan.StagingDir);
        if (!extract.Ok)
        {
            return await FailPreflightAsync(extract.Reason!, ct).ConfigureAwait(false);
        }

        // 3. Snapshot config, back up managed binaries, build merged config.
        var prepare = install.Prepare();
        if (!prepare.Ok)
        {
            return await FailPreflightAsync(prepare.Reason!, ct).ConfigureAwait(false);
        }

        string mergedConfig;
        try
        {
            var newDefault = File.ReadAllText(Path.Combine(_plan.StagingDir, "appsettings.json"));
            mergedConfig = install.BuildMergedConfig(newDefault);
        }
        catch (ConfigMergeException ex)
        {
            return await FailPreflightAsync($"config merge failed: {ex.Message}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return await FailPreflightAsync($"config prepare failed: {ex.GetType().Name}", ct).ConfigureAwait(false);
        }

        // 4. Revalidate parent identity, then request cutover authorization.
        if (!ProcessIdentity.Matches(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath))
        {
            return await FailPreflightAsync("parent identity changed before cutover", ct).ConfigureAwait(false);
        }

        _journal.Write("handoff.prepared");
        if (!await SendPreparedAndAwaitAuthorizationAsync(ct).ConfigureAwait(false))
        {
            // Parent chose to keep serving / disconnected — nothing installed.
            return await FailPreflightAsync("cutover not authorized", ct).ConfigureAwait(false);
        }

        // 5. Wait for the exact parent to exit, then final drift check.
        await WaitForParentExitAsync(ct).ConfigureAwait(false);
        if (!install.RevalidateNoDrift())
        {
            return await FailPreflightAsync("install drifted after handoff", ct).ConfigureAwait(false);
        }

        // 6. Cutover.
        var cutover = install.Cutover(mergedConfig);
        if (!cutover.Ok)
        {
            // Cutover aborts before installing on config drift — treat as preflight
            // fail if nothing was installed; otherwise roll back.
            _journal.Write("cutover.failed", cutover.Reason);
            return await RollbackAndReportAsync(install, ct).ConfigureAwait(false);
        }

        // 7. Launch the replacement and wait for authenticated Ready.
        var ready = await LaunchAndAwaitReadyAsync(
            _plan.BridgeExePath, _plan.TargetVersion, UpdateWire.RoleTarget, ct).ConfigureAwait(false);
        if (ready)
        {
            _journal.Write("commit");
            install.CleanupAfterCommit();
            return UpdaterExit.Committed;
        }

        // 8. Ready failed → full rollback.
        _journal.Write("target.not-ready");
        return await RollbackAndReportAsync(install, ct).ConfigureAwait(false);
    }

    private async Task<UpdaterExit> RollbackAndReportAsync(ManagedInstallManager install, CancellationToken ct)
    {
        var rollback = install.Rollback();
        if (!rollback.Ok)
        {
            ReportUnrecovered(install, rollback.Reason!);
            return UpdaterExit.Unrecovered;
        }

        // Relaunch the OLD bridge and require its Ready before declaring recovery.
        var recovered = await LaunchAndAwaitReadyAsync(
            _plan.BridgeExePath, _plan.CurrentVersion, UpdateWire.RoleRollback, ct).ConfigureAwait(false);
        if (recovered)
        {
            _journal.Write("rollback.recovered");
            return UpdaterExit.RolledBack;
        }

        ReportUnrecovered(install, "restored old bridge did not become ready");
        return UpdaterExit.Unrecovered;
    }

    private async Task<bool> SendPreparedAndAwaitAuthorizationAsync(CancellationToken ct)
    {
        var prepared = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgPrepared,
            AttemptId = _plan.AttemptId,
            Token = _plan.HandoffToken,
            SenderPid = Environment.ProcessId,
        };
        var reply = await UpdatePipeTransport.ServerSendLineAsync(
            _plan.HandoffPipe,
            UpdatePipeCodec.EncodeControl(prepared),
            expectReply: true,
            TimeSpan.FromMilliseconds(_plan.ParentExitTimeoutMs),
            ct).ConfigureAwait(false);

        if (reply is null)
        {
            return false;
        }
        var msg = UpdatePipeCodec.DecodeControl(reply);
        return msg is not null
            && msg.Kind == UpdateWire.MsgCutoverAuthorized
            && string.Equals(msg.AttemptId, _plan.AttemptId, StringComparison.Ordinal)
            && string.Equals(msg.Token, _plan.HandoffToken, StringComparison.Ordinal);
    }

    private async Task WaitForParentExitAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(_plan.ParentExitTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!ProcessIdentity.Matches(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath))
            {
                return; // exact parent has exited
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        // Force-stop ONLY the exact revalidated parent, never by name.
        if (ProcessIdentity.Matches(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath))
        {
            try
            {
                using var parent = Process.GetProcessById(_plan.ParentPid);
                parent.Kill();
                parent.WaitForExit(5000);
            }
            catch
            {
                // If it's already gone or unkillable, the drift check still guards us.
            }
        }
    }

    private async Task<bool> LaunchAndAwaitReadyAsync(
        string exePath, string expectedVersion, string role, CancellationToken ct)
    {
        var capability = UpdateCapability.Create(_plan.AttemptId, role);
        var launchCtx = new UpdateLaunchContext(
            _plan.AttemptId, role, capability.PipeName, capability.Token, expectedVersion);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = _plan.WorkingDirectory,
            UseShellExecute = false, // structural args, never a shell string
        };
        foreach (var arg in _plan.OriginalArgs)
        {
            psi.ArgumentList.Add(arg);
        }
        launchCtx.ApplyTo(psi.Environment);

        Process child;
        try
        {
            child = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _journal.Write("launch.failed", $"{role}:{ex.GetType().Name}");
            return false;
        }

        var success = false;
        try
        {
            // Race the readiness pipe against the child exiting early.
            var readyTask = UpdatePipeTransport.ServerReceiveLineAsync(
                capability.PipeName, TimeSpan.FromMilliseconds(_plan.ReadyTimeoutMs), ct);
            var exitTask = child.WaitForExitAsync(ct);

            var completed = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);
            if (completed == exitTask && !readyTask.IsCompleted)
            {
                _journal.Write("launch.exited-before-ready", role);
                return false;
            }

            var line = await readyTask.ConfigureAwait(false);
            var msg = UpdatePipeCodec.DecodeReady(line ?? string.Empty);
            var valid = UpdatePipeCodec.IsValidReady(
                msg, _plan.AttemptId, role, capability.Token, child.Id, expectedVersion);

            if (!valid)
            {
                _journal.Write("ready.invalid", role);
                return false;
            }
            if (child.HasExited)
            {
                _journal.Write("ready.but-exited", role);
                return false;
            }
            success = true;
            return true;
        }
        finally
        {
            // On any UNSUCCESSFUL outcome the launched process may still be alive
            // (e.g. a target that reported a wrong-version Ready but keeps
            // running, holding an image lock on its own .exe). It must be stopped
            // before rollback can replace its executable — the spec requires
            // rollback to "stop only the failed launched replacement". A
            // committed/recovered launch (success) is left serving and only its
            // handle is released.
            if (success)
            {
                child.Dispose();
            }
            else
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch
                {
                    // Already gone or unkillable — rollback's file-replace retry
                    // is the backstop.
                }
                finally
                {
                    child.Dispose();
                }
            }
        }
    }

    private async Task<UpdaterExit> FailPreflightAsync(string reason, CancellationToken ct)
    {
        _journal.Write("preflight.failed", reason);

        // Best-effort: tell the parent it can stop waiting and keep serving.
        var msg = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgPreflightFailed,
            AttemptId = _plan.AttemptId,
            Token = _plan.HandoffToken,
            SenderPid = Environment.ProcessId,
            Detail = reason,
        };
        try
        {
            await UpdatePipeTransport.ServerSendLineAsync(
                _plan.HandoffPipe, UpdatePipeCodec.EncodeControl(msg),
                expectReply: false, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch
        {
            // The parent may already be serving; that's fine.
        }

        // Clean up private temporaries (never the install dir).
        TryDeleteDirectory(_plan.StagingDir);
        TryDelete(_plan.ArchivePath);
        return UpdaterExit.PreflightFailed;
    }

    private void ReportUnrecovered(ManagedInstallManager install, string reason)
    {
        _journal.Write("unrecovered", reason);
        _stderr.WriteLine("copilot-bridge update failed AND automatic rollback did not restore service.");
        _stderr.WriteLine($"  reason:            {reason}");
        _stderr.WriteLine($"  current version:   {_plan.CurrentVersion}");
        _stderr.WriteLine($"  target version:    {_plan.TargetVersion}");
        _stderr.WriteLine($"  install directory: {_plan.InstallDir}");
        _stderr.WriteLine($"  backup directory:  {_plan.BackupDir}");
        _stderr.WriteLine($"  original config:   {install.PrivateConfigCopyPath}");
        _stderr.WriteLine($"  transaction log:   {_plan.JournalPath}");
        _stderr.WriteLine("Manual recovery:");
        _stderr.WriteLine("  1. Stop any running copilot-bridge from this installation.");
        _stderr.WriteLine("  2. Copy the backed-up executables and original config back into the install directory.");
        _stderr.WriteLine("  3. Start copilot-bridge with your original command.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
