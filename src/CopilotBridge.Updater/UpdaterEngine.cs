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
    private readonly string _trustedRoot;
    private ManagedInstallManager? _install;

    public UpdaterEngine(UpdatePlan plan, TransactionJournal journal, TextWriter stderr, string trustedRoot)
    {
        _plan = plan;
        _journal = journal;
        _stderr = stderr;
        _trustedRoot = trustedRoot;
    }

    public async Task<UpdaterExit> RunAsync(CancellationToken ct)
    {
        // 1. Validate plan (against the trusted root) + acquire the install lock.
        var validate = UpdatePlanValidator.Validate(_plan, _trustedRoot);
        if (!validate.Ok)
        {
            return await FailPreflightAsync($"invalid plan: {validate.Reason}", ct).ConfigureAwait(false);
        }

        // The install lock file must live in a directory SHARED by all attempts
        // for the same installation, not the per-attempt trusted root (which is
        // unique per attempt and would let two concurrent updates each acquire
        // their own lock). Use the parent of the attempt root — the common
        // updates directory — falling back to the trusted root only if it has no
        // parent (defensive).
        var lockRoot = Path.GetDirectoryName(Path.GetFullPath(_trustedRoot));
        if (string.IsNullOrEmpty(lockRoot))
        {
            lockRoot = _trustedRoot;
        }
        using var installLock = InstallationLock.TryAcquire(_plan.InstallDir, lockRoot);
        if (installLock is null)
        {
            return await FailPreflightAsync("another update is in progress", ct).ConfigureAwait(false);
        }

        var install = new ManagedInstallManager(_plan, _journal);
        _install = install;

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

        // 3b. Materialize + flush + verify every replacement as a same-directory
        //     temporary file (in the install dir, so on the same volume) DURING
        //     preparation. A disk-full/write failure therefore surfaces here,
        //     BEFORE the parent is stopped — cutover then does only bounded atomic
        //     renames, which can't leave a half-written managed file.
        var stage = install.StageReplacements(mergedConfig);
        if (!stage.Ok)
        {
            return await FailPreflightAsync(stage.Reason!, ct).ConfigureAwait(false);
        }

        // 4. Revalidate parent identity, then request cutover authorization.
        if (ProcessIdentity.Check(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath) != IdentityCheck.Matched)
        {
            return await FailPreflightAsync("parent identity changed before cutover", ct).ConfigureAwait(false);
        }

        _journal.Write("handoff.prepared");
        if (!await SendPreparedAndAwaitAuthorizationAsync(ct).ConfigureAwait(false))
        {
            // Parent chose to keep serving / disconnected — nothing installed and
            // the old bridge is still up, so a plain fail-open is correct.
            return await FailPreflightAsync("cutover not authorized", ct).ConfigureAwait(false);
        }

        // ---- OWNERSHIP-TRANSFER WINDOW ----------------------------------------
        // Authorization has been granted: the parent has ALREADY returned without
        // constructing Kestrel and is exiting (or gone), so from here there is soon
        // NO bridge serving. EVERY outcome past this point must RECOVER service or
        // report an explicit unrecovered state — never a plain fail-open, which
        // would exit the updater and relaunch nothing.
        //
        // The engine only classifies WHAT happened (an OwnershipOutcome) and whether
        // the transaction has begun MUTATING the install; OwnershipWindowRouter owns
        // the policy of which service-restoring action each outcome maps to. That
        // routing table is the load-bearing contract (a fail-open here strands the
        // user; recovering without config restores a bridge with no appsettings),
        // so it lives as a pure, unit-tested function rather than inline branches.
        //
        // `transactionMutating` flips true immediately BEFORE Cutover(), which turns
        // destructive the instant it renames the live appsettings.json — so a throw
        // *inside* Cutover (e.g. an I/O race hashing the renamed .bak) routes to
        // rollback (config restored), not recover-without-config.
        var transactionMutating = false;
        try
        {
            // 5. Wait for the exact parent to positively exit. A tri-state check
            //    means a transient inspection failure is treated as "unknown", not
            //    "gone": we do NOT cut over unless the parent is confirmed absent.
            var exitState = await WaitForParentExitAsync(ct).ConfigureAwait(false);
            if (exitState != IdentityCheck.AbsentOrReused)
            {
                _journal.Write("parent-exit.unconfirmed", exitState.ToString());
                return await ExecuteRecoveryAsync(
                    OwnershipOutcome.ParentExitUnconfirmed, transactionMutating, install,
                    exitState == IdentityCheck.InspectionFailed
                        ? "could not confirm parent exit before cutover"
                        : "parent did not exit before cutover").ConfigureAwait(false);
            }

            // Parent confirmed gone: service is DOWN. Any failure below recovers.
            if (!install.RevalidateNoDrift())
            {
                _journal.Write("drift.after-handoff");
                return await ExecuteRecoveryAsync(
                    OwnershipOutcome.DriftAfterHandoff, transactionMutating, install,
                    "install drifted after handoff").ConfigureAwait(false);
            }

            // 6. Cutover (rename original config to .bak, atomically move the
            //    pre-staged replacements into place — no writes here). Mark the
            //    transaction destructive BEFORE the call: Cutover mutates the
            //    install as soon as it renames the config, so a throw partway
            //    through must roll back, not recover-without-config.
            transactionMutating = true;
            var cutover = install.Cutover();
            if (!cutover.Ok)
            {
                _journal.Write("cutover.failed", cutover.Reason);
                return await ExecuteRecoveryAsync(
                    OwnershipOutcome.CutoverFailed, transactionMutating, install,
                    cutover.Reason!).ConfigureAwait(false);
            }

            // 7. Launch the replacement and wait for authenticated Ready.
            var ready = await LaunchAndAwaitReadyAsync(
                _plan.BridgeExePath, _plan.TargetVersion, UpdateWire.RoleTarget, ct).ConfigureAwait(false);
            if (ready)
            {
                _journal.Write("commit");
                // Move our working directory OUT of the attempt root before deleting
                // it. The gate launches this updater with its cwd set to the attempt
                // root, and on Windows a process's cwd is locked open — so the
                // recursive delete below would silently fail (and be swallowed),
                // leaving the capability-bearing plan + updater copy behind on every
                // successful update. The install dir is a stable location that always
                // exists and is never the delete target.
                TryChangeDirectory(_plan.InstallDir);
                install.CleanupAfterCommit(_trustedRoot);
                return UpdaterExit.Committed;
            }

            _journal.Write("target.not-ready");
            return await ExecuteRecoveryAsync(
                OwnershipOutcome.TargetNotReady, transactionMutating, install,
                "replacement did not report ready").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _journal.Write(transactionMutating ? "cancelled.during-cutover" : "cancelled.before-cutover");
            return await ExecuteRecoveryAsync(
                OwnershipOutcome.Cancelled, transactionMutating, install,
                "cancelled during ownership transfer").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _journal.Write("ownership-window.io-error", ex.GetType().Name);
            return await ExecuteRecoveryAsync(
                OwnershipOutcome.IoError, transactionMutating, install,
                $"I/O error during ownership transfer: {ex.GetType().Name}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Execute the service-restoring action <see cref="OwnershipWindowRouter"/>
    /// selects for an ownership-window outcome. The router owns the policy (never a
    /// fail-open after authorization; rollback whenever the transaction began
    /// mutating); this owns the async process/IPC work each action needs.
    /// </summary>
    private async Task<UpdaterExit> ExecuteRecoveryAsync(
        OwnershipOutcome outcome, bool transactionMutating, ManagedInstallManager install, string reason)
    {
        var action = OwnershipWindowRouter.Route(outcome, transactionMutating);
        return action switch
        {
            RecoveryAction.Commit => UpdaterExit.Committed,
            RecoveryAction.Rollback => await RollbackAndReportAsync(install).ConfigureAwait(false),
            _ => await RecoverOldBridgeAsync(install, reason).ConfigureAwait(false),
        };
    }

    private async Task<UpdaterExit> RollbackAndReportAsync(ManagedInstallManager install)
    {
        var rollback = install.Rollback();
        if (!rollback.Ok)
        {
            ReportUnrecovered(install, rollback.Reason!);
            return UpdaterExit.Unrecovered;
        }

        // Relaunch the OLD bridge and require its Ready before declaring recovery.
        // Recovery must run to completion even if the incoming operation was
        // cancelled, so it never observes the caller's token.
        var recovered = await LaunchAndAwaitReadyAsync(
            _plan.BridgeExePath, _plan.CurrentVersion, UpdateWire.RoleRollback, CancellationToken.None)
            .ConfigureAwait(false);
        if (recovered)
        {
            _journal.Write("rollback.recovered");
            // Sweep any *.old.<rand> siblings the cutover image-lock fallback
            // created (the old executable image lock has now released).
            install.SweepRenamedAside();
            return UpdaterExit.RolledBack;
        }

        ReportUnrecovered(install, "restored old bridge did not become ready");
        return UpdaterExit.Unrecovered;
    }

    /// <summary>
    /// Recover service when the authorizing parent has already exited but cutover
    /// has NOT installed anything yet (e.g. config/binary drift detected after
    /// handoff). No managed file was replaced, so there is nothing to restore —
    /// but there is also no bridge serving, so the old bridge must be relaunched
    /// and confirmed Ready. If the drift was a managed-BINARY change (someone
    /// replaced the installed exe out-of-band), do not execute that unplanned
    /// file: report unrecovered with manual-recovery guidance.
    /// </summary>
    private async Task<UpdaterExit> RecoverOldBridgeAsync(ManagedInstallManager install, string reason)
    {
        _journal.Write("recover.old-bridge", reason);

        if (install.ManagedBinaryDrifted())
        {
            // The installed executable is not the one we snapshotted; launching it
            // would run unknown code. Preserve everything and require manual action.
            ReportUnrecovered(install, $"{reason} (installed binary changed out-of-band)");
            return UpdaterExit.Unrecovered;
        }

        var recovered = await LaunchAndAwaitReadyAsync(
            _plan.BridgeExePath, _plan.CurrentVersion, UpdateWire.RoleRollback, CancellationToken.None)
            .ConfigureAwait(false);
        if (recovered)
        {
            _journal.Write("recover.recovered");
            return UpdaterExit.RolledBack;
        }

        ReportUnrecovered(install, $"{reason}; old bridge did not become ready");
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
            && msg.ProtocolVersion == UpdateWire.ProtocolVersion
            && msg.Kind == UpdateWire.MsgCutoverAuthorized
            && string.Equals(msg.AttemptId, _plan.AttemptId, StringComparison.Ordinal)
            && string.Equals(msg.Token, _plan.HandoffToken, StringComparison.Ordinal)
            // Bind to the exact parent identity encoded in the plan — a control
            // message from any other process is rejected before cutover.
            && msg.SenderPid == _plan.ParentPid;
    }

    /// <summary>
    /// Wait for the exact recorded parent to exit; if it overruns the bounded
    /// deadline, force-stop only that revalidated identity. Returns a tri-state:
    /// <see cref="IdentityCheck.AbsentOrReused"/> = positively gone (safe to cut
    /// over); <see cref="IdentityCheck.Matched"/> = still alive after the kill
    /// attempt; <see cref="IdentityCheck.InspectionFailed"/> = could not determine.
    /// The caller must only cut over on AbsentOrReused, so a transient inspection
    /// failure never lets it race a possibly-live parent.
    /// </summary>
    private async Task<IdentityCheck> WaitForParentExitAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(_plan.ParentExitTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = ProcessIdentity.Check(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath);
            if (state == IdentityCheck.AbsentOrReused)
            {
                return state; // exact parent has positively exited
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        // Force-stop ONLY the exact revalidated parent, never by name.
        if (ProcessIdentity.Check(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath) == IdentityCheck.Matched)
        {
            try
            {
                using var parent = Process.GetProcessById(_plan.ParentPid);
                parent.Kill();
                parent.WaitForExit(5000);
            }
            catch
            {
                // Already gone or unkillable — fall through to the confirmation
                // check below rather than assuming success.
            }
        }
        // Return the final positively-determined state.
        return ProcessIdentity.Check(_plan.ParentPid, _plan.ParentStartTicks, _plan.BridgeExePath);
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

    // Move the process's working directory to a stable location outside the
    // attempt root so the root can be deleted (Windows locks a live process's cwd).
    // Best-effort: if it fails, cleanup is simply best-effort too, never fatal.
    private static void TryChangeDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Environment.CurrentDirectory = dir;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Leaving the cwd where it is only means cleanup may not fully remove
            // the attempt root on Windows — a diagnostic-only shortfall, not a
            // correctness failure. Never let it derail a committed update.
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

        // Clean up on a preflight failure: the install was never changed, so remove
        // the staging tree, downloaded archive, any *.new.<attempt> replacement
        // temporaries, AND the managed-binary backups — the latter are full copies
        // of both executables that would otherwise accumulate one set per rejected
        // attempt (tens of MB). Nothing is installed to roll back to, so keeping the
        // backups serves no recovery purpose. (ManagedInstallManager owns the
        // filesystem policy; the engine just invokes it.)
        _install?.CleanupAfterPreflightFailure();
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
}
