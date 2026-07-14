namespace CopilotBridge.Update.Wire;

/// <summary>
/// Validates an <see cref="UpdatePlan"/> the updater received before it acts on
/// it. The updater never fills missing data or makes a policy choice — an
/// incomplete or path-escaping plan is a hard failure, and (because it fails
/// before cutover) the initiating bridge simply continues serving.
/// </summary>
internal static partial class UpdatePlanValidator
{
    /// <summary>
    /// Validate schema/version, required fields, and path confinement.
    /// <paramref name="trustedRoot"/> is the directory the updater was handed
    /// (the one containing plan.json), established independently of any
    /// plan-supplied path; every per-attempt temporary path must be confined to
    /// it, so a tampered plan can never aim recursive cleanup at unrelated data.
    /// </summary>
    public static UpdateStepResult Validate(UpdatePlan plan, string trustedRoot)
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

        // AttemptId is later concatenated into install-dir sibling paths
        // (<managed>.new.<attempt>, appsettings.json.bak.<attempt>), so it must be a
        // bounded, filename-safe token — never able to carry a separator or `..`
        // traversal that would make preflight write, or cutover rename, outside the
        // install directory. The CLI mints it as lowercase hex; accept the slightly
        // wider alphanumeric set the tests use, but nothing path-significant.
        if (!AttemptIdShape().IsMatch(plan.AttemptId))
        {
            return UpdateStepResult.Fail("plan attempt id is not a bounded filename-safe token");
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
        // On Windows the filesystem is case-insensitive, so bridge.exe and
        // BRIDGE.EXE would target the SAME file — build the distinctness set with
        // the platform-correct comparer so aliased names are rejected.
        var nameComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var expectedManaged = new HashSet<string>(nameComparer)
        {
            bridgeName, updaterName, configName,
        };
        if (expectedManaged.Count != 3)
        {
            // The three names must be distinct (case-insensitively on Windows), or
            // the allowlist is ambiguous.
            return UpdateStepResult.Fail("managed target names are not distinct");
        }
        // Exact-set equality, not Count + All(contains): the latter accepts a list
        // like [bridge, bridge, updater] (a duplicate masking a MISSING config),
        // which would let cutover proceed without ever staging a required target.
        // Require the distinct set to equal the allowlist AND the raw count to be 3
        // (so a duplicate that collapses to the right set is still rejected).
        var providedManaged = new HashSet<string>(plan.ManagedFiles, nameComparer);
        if (plan.ManagedFiles.Count != expectedManaged.Count
            || !providedManaged.SetEquals(expectedManaged))
        {
            return UpdateStepResult.Fail("managed files are not the exact bridge/updater/config allowlist");
        }
        // Each managed name must be a plain file name (no directory component), so
        // it maps to a DIRECT child of the install dir — never a nested path.
        foreach (var name in plan.ManagedFiles)
        {
            if (name != Path.GetFileName(name)
                || UpdatePaths.ResolveContained(plan.InstallDir, name) is null)
            {
                return UpdateStepResult.Fail("managed file is not a direct install-dir child");
            }
        }

        // The explicit bridge/updater/config paths must equal their canonical
        // direct-child targets exactly — NOT merely be "somewhere under" the
        // install dir. Otherwise a plan could name <install>/sub/copilot-bridge.exe
        // (which the updater verifies/kills/launches) while file replacement still
        // targets <install>/copilot-bridge.exe — two different executables.
        if (!IsCanonicalChild(plan.InstallDir, plan.BridgeExePath, bridgeName)
            || !IsCanonicalChild(plan.InstallDir, plan.UpdaterExePath, updaterName)
            || !IsCanonicalChild(plan.InstallDir, plan.ConfigPath, configName))
        {
            return UpdateStepResult.Fail("managed path is not the canonical install-dir child");
        }

        // Confine every per-attempt temporary path to the TRUSTED root the updater
        // established from the plan-file location — never a plan-selected path.
        // Recursive failure cleanup deletes StagingDir/BackupDir, so a tampered
        // plan must not be able to point those at unrelated data.
        var fullTrusted = Path.GetFullPath(trustedRoot);
        foreach (var tmp in new[] { plan.StagingDir, plan.BackupDir, plan.ArchivePath, plan.JournalPath })
        {
            if (!UpdatePaths.IsInside(fullTrusted, tmp))
            {
                return UpdateStepResult.Fail("temporary path escapes the trusted attempt root");
            }
        }
        // The trusted root must NOT overlap the install dir (temporaries live off
        // to the side, never among managed files).
        if (UpdatePaths.IsInside(plan.InstallDir, fullTrusted)
            || UpdatePaths.IsInside(fullTrusted, plan.InstallDir))
        {
            return UpdateStepResult.Fail("attempt root overlaps the install directory");
        }

        return UpdateStepResult.Success();
    }

    // True when <root>/<name> (a direct child) is exactly the full path of
    // <candidate>, using the platform-correct path comparison.
    private static bool IsCanonicalChild(string root, string candidate, string name)
    {
        if (name != Path.GetFileName(name) || string.IsNullOrEmpty(name))
        {
            return false;
        }
        var expected = Path.GetFullPath(Path.Combine(root, name));
        var actual = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(expected, actual, comparison);
    }

    // A bounded, filename-safe attempt id: 1–64 ASCII alphanumerics. No separator,
    // no dot, no `..` — so it can never turn a sibling temp/backup path into a
    // traversal out of the install directory.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex AttemptIdShape();
}
