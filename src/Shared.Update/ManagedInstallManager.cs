namespace CopilotBridge.Update.Wire;

/// <summary>
/// The file-level mechanics of the install transaction: snapshot + back up the
/// existing managed binaries, verify the config snapshot, perform the cutover
/// (rename original config to a unique <c>.bak</c>, install staged files), and
/// roll everything back to the exact pre-update state. Purely filesystem — no
/// process launch or IPC — so it is fully unit-testable against a disposable
/// directory. Callers own ordering and the Ready handshake.
/// </summary>
/// <remarks>
/// Drift safety: every managed binary and the config are hashed at preflight;
/// cutover revalidates those hashes after the parent exits and refuses to install
/// over a file that changed out from under the transaction. The config's exact
/// pre-update bytes are preserved twice — as the renamed <c>.bak</c> sibling and
/// as a verified private copy — so rollback restores byte-for-byte, including
/// old-only keys a successful migration would have removed.
/// </remarks>
internal sealed class ManagedInstallManager
{
    private readonly UpdatePlan _plan;
    private readonly TransactionJournal _journal;

    // Hashes recorded at preflight, checked again at cutover.
    private readonly Dictionary<string, string> _managedHashes = new(StringComparer.Ordinal);
    private ConfigSnapshot? _configSnapshot;
    private string _configBakPath = string.Empty;
    private string _privateConfigCopy = string.Empty;

    public ManagedInstallManager(UpdatePlan plan, TransactionJournal journal)
    {
        _plan = plan;
        _journal = journal;
    }

    /// <summary>Absolute path of each managed file inside the install dir.</summary>
    private IEnumerable<(string Name, string InstallPath, string StagingPath, string BackupPath)> ManagedFiles()
    {
        foreach (var name in _plan.ManagedFiles)
        {
            yield return (
                name,
                Path.Combine(_plan.InstallDir, name),
                Path.Combine(_plan.StagingDir, name),
                Path.Combine(_plan.BackupDir, name));
        }
    }

    /// <summary>
    /// Preflight: snapshot the config once, verify the staged files exist,
    /// back up every existing managed binary, and record hashes. The installed
    /// config and binaries are NOT modified. Returns a fail reason on any problem.
    /// </summary>
    public UpdateStepResult Prepare()
    {
        Directory.CreateDirectory(_plan.BackupDir);

        // 1. Config snapshot (single read) + verified private rollback copy.
        _journal.Write("prepare.config-snapshot");
        try
        {
            _configSnapshot = ConfigSnapshot.Read(_plan.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Fail("cannot read installed config");
        }

        _privateConfigCopy = Path.Combine(_plan.BackupDir, "appsettings.json.orig");
        if (!_configSnapshot.WriteVerifiedCopy(_privateConfigCopy))
        {
            return UpdateStepResult.Fail("cannot verify private config backup");
        }

        // 2. Every managed file must be present in staging.
        foreach (var f in ManagedFiles())
        {
            if (string.Equals(f.Name, Path.GetFileName(_plan.ConfigPath), StringComparison.OrdinalIgnoreCase))
            {
                continue; // config is handled by the merge/snapshot path, not staging
            }
            if (!File.Exists(f.StagingPath))
            {
                return UpdateStepResult.Fail($"staged file missing: {f.Name}");
            }
        }

        // 3. Back up + hash every existing managed binary.
        foreach (var f in ManagedFiles())
        {
            if (string.Equals(f.Name, Path.GetFileName(_plan.ConfigPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!File.Exists(f.InstallPath))
            {
                // A managed binary not yet present (first install of updater) is
                // fine — nothing to back up, nothing to drift-check.
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.BackupPath)!);
                File.Copy(f.InstallPath, f.BackupPath, overwrite: true);
                var backHash = HashFile(f.BackupPath);
                if (backHash != HashFile(f.InstallPath))
                {
                    return UpdateStepResult.Fail($"backup verify failed: {f.Name}");
                }
                _managedHashes[f.Name] = backHash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return UpdateStepResult.Fail($"cannot back up {f.Name}");
            }
        }

        // 4. Prove the install directory is writable WITHOUT overwriting or
        //    renaming any managed target: create and flush a throwaway probe
        //    file in the install dir, then delete it. A read-only install (e.g.
        //    a macOS .pkg under /usr/local) fails here — before the old bridge is
        //    ever stopped — so the transaction aborts fail-open.
        var probe = Path.Combine(_plan.InstallDir, $".copilot-update-probe.{_plan.AttemptId}");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
                fs.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Fail("installation directory is not writable");
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
        }

        _journal.Write("prepare.done");
        return UpdateStepResult.Success();
    }

    /// <summary>The merged config text to install, produced from the snapshot + template.</summary>
    public string BuildMergedConfig(string newDefaultConfigText)
    {
        if (_configSnapshot is null)
        {
            throw new InvalidOperationException("Prepare must run before BuildMergedConfig.");
        }
        return ConfigMerger.Merge(_configSnapshot.Text, newDefaultConfigText);
    }

    /// <summary>
    /// True when every installed managed binary and the installed config still
    /// match the hashes recorded at preflight (no external drift). Call
    /// immediately before cutover, after the parent exits.
    /// </summary>
    public bool RevalidateNoDrift()
    {
        if (_configSnapshot is null || !_configSnapshot.MatchesFile(_plan.ConfigPath))
        {
            return false;
        }
        foreach (var (name, hash) in _managedHashes)
        {
            var installPath = Path.Combine(_plan.InstallDir, name);
            if (!File.Exists(installPath) || HashFile(installPath) != hash)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Cutover: rename the original config to a unique <c>.bak</c>, verify the
    /// renamed bytes still match the snapshot, then install the staged managed
    /// binaries and write the merged config. Assumes the parent has exited and
    /// <see cref="RevalidateNoDrift"/> already passed. Returns a fail reason
    /// without leaving a half-open state the rollback path can't recover.
    /// </summary>
    public UpdateStepResult Cutover(string mergedConfigText)
    {
        if (_configSnapshot is null)
        {
            return UpdateStepResult.Fail("cutover before prepare");
        }

        _configBakPath = $"{_plan.ConfigPath}.bak.{_plan.AttemptId}";
        _journal.Write("cutover.rename-config");
        try
        {
            if (File.Exists(_configBakPath))
            {
                return UpdateStepResult.Fail("transaction backup name already exists");
            }
            File.Move(_plan.ConfigPath, _configBakPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Fail("cannot rename original config");
        }

        // The renamed file must still be the exact pre-update bytes.
        if (!_configSnapshot.MatchesFile(_configBakPath))
        {
            // Restore the original name and abort — do not install anything.
            SafeMove(_configBakPath, _plan.ConfigPath);
            return UpdateStepResult.Fail("config drifted before cutover");
        }

        _journal.Write("cutover.install-files");
        try
        {
            foreach (var f in ManagedFiles())
            {
                if (string.Equals(f.Name, Path.GetFileName(_plan.ConfigPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!File.Exists(f.StagingPath))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(f.InstallPath)!);
                CopyOverManagedFile(f.StagingPath, f.InstallPath);
                RestoreExecutableMode(f.InstallPath);
            }

            File.WriteAllText(_plan.ConfigPath, mergedConfigText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Fail($"install failed: {ex.GetType().Name}");
        }

        _journal.Write("cutover.done");
        return UpdateStepResult.Success();
    }

    /// <summary>
    /// Roll back to the exact pre-update state: restore every managed binary from
    /// its verified backup, remove the merged config, and restore the original
    /// config (from the <c>.bak</c> sibling if its bytes still match, else from
    /// the verified private copy). Returns a fail reason if it cannot fully
    /// restore, in which case the caller keeps all backups for manual recovery.
    /// </summary>
    public UpdateStepResult Rollback()
    {
        _journal.Write("rollback.begin");

        // Restore managed binaries from verified backups.
        foreach (var (name, _) in _managedHashes)
        {
            var backupPath = Path.Combine(_plan.BackupDir, name);
            var installPath = Path.Combine(_plan.InstallDir, name);
            if (!File.Exists(backupPath))
            {
                return UpdateStepResult.Fail($"rollback backup missing: {name}");
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
                File.Copy(backupPath, installPath, overwrite: true);
                RestoreExecutableMode(installPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return UpdateStepResult.Fail($"rollback restore failed: {name}");
            }
        }

        // Restore config exactly.
        try
        {
            if (File.Exists(_plan.ConfigPath))
            {
                File.Delete(_plan.ConfigPath);
            }

            if (!string.IsNullOrEmpty(_configBakPath)
                && File.Exists(_configBakPath)
                && _configSnapshot is not null
                && _configSnapshot.MatchesFile(_configBakPath))
            {
                File.Move(_configBakPath, _plan.ConfigPath);
            }
            else if (File.Exists(_privateConfigCopy))
            {
                File.Copy(_privateConfigCopy, _plan.ConfigPath, overwrite: true);
            }
            else
            {
                return UpdateStepResult.Fail("no verified config backup to restore");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Fail("rollback config restore failed");
        }

        _journal.Write("rollback.done");
        return UpdateStepResult.Success();
    }

    /// <summary>Best-effort cleanup of transaction temporaries after a committed update.</summary>
    public void CleanupAfterCommit()
    {
        TryDelete(_configBakPath);
        TryDeleteDirectory(_plan.BackupDir);
        TryDeleteDirectory(_plan.StagingDir);
        TryDelete(_plan.ArchivePath);
    }

    public string ConfigBackupPath => _configBakPath;
    public string PrivateConfigCopyPath => _privateConfigCopy;

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    // Replace a managed binary, tolerating the brief post-exit image lock Windows
    // keeps on a just-terminated executable. The parent has already exited and
    // been verified; a short bounded retry (with a rename-aside fallback) makes
    // the overwrite reliable without weakening any safety check.
    private static void CopyOverManagedFile(string stagingPath, string installPath)
    {
        const int attempts = 20;
        for (var i = 0; ; i++)
        {
            try
            {
                File.Copy(stagingPath, installPath, overwrite: true);
                return;
            }
            catch (IOException) when (i < attempts)
            {
                // First fall back to moving the locked file aside, then copy.
                if (i == attempts / 2)
                {
                    TryRenameAside(installPath);
                }
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void TryRenameAside(string installPath)
    {
        try
        {
            if (File.Exists(installPath))
            {
                // A locked-but-renamable image: move it to a sibling the OS can
                // release lazily, freeing the name for the new file.
                var aside = installPath + ".old." + Guid.NewGuid().ToString("N")[..6];
                File.Move(installPath, aside);
            }
        }
        catch
        {
            // If even the rename fails, the retry loop keeps trying the copy.
        }
    }

    private static void RestoreExecutableMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            // rwxr-xr-x for the bridge/updater binaries; harmless on the config
            // (callers only invoke this for the managed executables).
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Non-fatal: a filesystem without mode support just keeps defaults.
        }
    }

    private static void SafeMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
