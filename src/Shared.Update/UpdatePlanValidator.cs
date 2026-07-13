namespace CopilotBridge.Update.Wire;

/// <summary>
/// Validates an <see cref="UpdatePlan"/> the updater received before it acts on
/// it. The updater never fills missing data or makes a policy choice — an
/// incomplete or path-escaping plan is a hard failure, and (because it fails
/// before cutover) the initiating bridge simply continues serving.
/// </summary>
internal static class UpdatePlanValidator
{
    /// <summary>Validate schema/version, required fields, and path confinement.</summary>
    public static UpdateStepResult Validate(UpdatePlan plan)
    {
        if (plan.ProtocolVersion != UpdateWire.ProtocolVersion)
        {
            return UpdateStepResult.Fail($"unsupported plan protocol version {plan.ProtocolVersion}");
        }

        if (string.IsNullOrEmpty(plan.AttemptId)
            || string.IsNullOrEmpty(plan.InstallDir)
            || string.IsNullOrEmpty(plan.BridgeExePath)
            || string.IsNullOrEmpty(plan.UpdaterExePath)
            || string.IsNullOrEmpty(plan.ConfigPath)
            || string.IsNullOrEmpty(plan.AssetName)
            || string.IsNullOrEmpty(plan.AssetUrl)
            || string.IsNullOrEmpty(plan.AssetSha256)
            || string.IsNullOrEmpty(plan.CurrentVersion)
            || string.IsNullOrEmpty(plan.TargetVersion)
            || string.IsNullOrEmpty(plan.StagingDir)
            || string.IsNullOrEmpty(plan.BackupDir)
            || string.IsNullOrEmpty(plan.ArchivePath)
            || string.IsNullOrEmpty(plan.JournalPath)
            || string.IsNullOrEmpty(plan.WorkingDirectory)
            || string.IsNullOrEmpty(plan.HandoffPipe)
            || string.IsNullOrEmpty(plan.HandoffToken)
            || plan.ManagedFiles is null or { Count: 0 }
            || plan.OriginalArgs is null)
        {
            return UpdateStepResult.Fail("plan is missing required fields");
        }

        if (plan.AssetSize <= 0)
        {
            return UpdateStepResult.Fail("plan asset size is not positive");
        }

        // Finite, positive phase budgets — a 0-ms timeout would make a phase
        // insta-fail, which is a malformed plan, not a legitimate instruction.
        if (plan.DownloadTimeoutMs <= 0 || plan.ParentExitTimeoutMs <= 0 || plan.ReadyTimeoutMs <= 0)
        {
            return UpdateStepResult.Fail("plan has a non-positive phase timeout");
        }

        if (!plan.AssetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateStepResult.Fail("plan asset URL is not HTTPS");
        }

        if (plan.ArchiveKind is not (UpdateWire.ArchiveZip or UpdateWire.ArchiveTarGz))
        {
            return UpdateStepResult.Fail("plan archive kind is unsupported");
        }

        // Managed files are an EXACT allowlist, not "any relative path": exactly
        // the RID-specific bridge exe, the updater exe, and appsettings.json. A
        // modified plan must not be able to add arbitrary files that cutover would
        // then overwrite from the archive.
        var bridgeName = Path.GetFileName(plan.BridgeExePath);
        var updaterName = Path.GetFileName(plan.UpdaterExePath);
        var configName = Path.GetFileName(plan.ConfigPath);
        var expectedManaged = new HashSet<string>(StringComparer.Ordinal)
        {
            bridgeName, updaterName, configName,
        };
        if (plan.ManagedFiles.Count != expectedManaged.Count
            || !plan.ManagedFiles.All(expectedManaged.Contains))
        {
            return UpdateStepResult.Fail("managed files are not the exact bridge/updater/config allowlist");
        }
        // Each managed name must resolve to its canonical install-root target.
        foreach (var name in plan.ManagedFiles)
        {
            var resolved = UpdatePaths.ResolveContained(plan.InstallDir, name);
            if (resolved is null)
            {
                return UpdateStepResult.Fail("managed file escapes install root");
            }
        }

        // The bridge/updater/config paths named in the plan must also be inside
        // the install root (the config's ".bak" sibling lands next to it), and
        // must be the direct install-dir children matching the managed names.
        if (!UpdatePaths.IsInside(plan.InstallDir, plan.BridgeExePath)
            || !UpdatePaths.IsInside(plan.InstallDir, plan.UpdaterExePath)
            || !UpdatePaths.IsInside(plan.InstallDir, plan.ConfigPath))
        {
            return UpdateStepResult.Fail("managed path escapes install root");
        }

        // Confine every per-attempt temporary path to ONE owner-private attempt
        // root (the staging dir's parent). Later failure cleanup recursively
        // deletes StagingDir/BackupDir, so a malformed plan must not be able to
        // point those at an unrelated directory.
        var attemptRoot = Path.GetDirectoryName(Path.GetFullPath(plan.StagingDir));
        if (string.IsNullOrEmpty(attemptRoot))
        {
            return UpdateStepResult.Fail("plan staging path has no parent");
        }
        foreach (var tmp in new[] { plan.StagingDir, plan.BackupDir, plan.ArchivePath, plan.JournalPath })
        {
            if (!UpdatePaths.IsInside(attemptRoot, tmp))
            {
                return UpdateStepResult.Fail("temporary path escapes the attempt root");
            }
        }
        // The attempt root must NOT be the install dir (temporaries live off to
        // the side, never among managed files).
        if (UpdatePaths.IsInside(plan.InstallDir, attemptRoot)
            || UpdatePaths.IsInside(attemptRoot, plan.InstallDir))
        {
            return UpdateStepResult.Fail("attempt root overlaps the install directory");
        }

        return UpdateStepResult.Success();
    }
}
