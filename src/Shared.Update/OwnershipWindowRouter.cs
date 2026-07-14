namespace CopilotBridge.Update.Wire;

/// <summary>
/// What happened inside the ownership-transfer window — the span after the parent
/// bridge has authorized cutover (and therefore returned WITHOUT constructing its
/// listener, so it is exiting/gone) until the updater has committed or restored
/// service. Every value here means "there is soon, or already, NO bridge serving",
/// so none of them may resolve to a plain fail-open.
/// </summary>
internal enum OwnershipOutcome
{
    /// <summary>The exact parent could not be confirmed gone before cutover
    /// (inspection failed, or it was still Matched after the kill attempt).
    /// Nothing was installed, but the parent is on its way out.</summary>
    ParentExitUnconfirmed,

    /// <summary>A managed file drifted after handoff; cutover was refused. Nothing
    /// was installed.</summary>
    DriftAfterHandoff,

    /// <summary><see cref="ManagedInstallManager.Cutover"/> returned a failure
    /// (e.g. config drifted at the rename boundary). It may have partially replaced
    /// binaries.</summary>
    CutoverFailed,

    /// <summary>The replacement launched but never reported (or wrongly reported)
    /// Ready. The new files are installed.</summary>
    TargetNotReady,

    /// <summary>The replacement reported a valid, version-matched Ready. Commit.</summary>
    TargetReady,

    /// <summary>The operation was cancelled (Ctrl+C) inside the window.</summary>
    Cancelled,

    /// <summary>An I/O error escaped inside the window — including a throw from
    /// partway through <see cref="ManagedInstallManager.Cutover"/> AFTER it has
    /// renamed the live config to its <c>.bak</c> (e.g. hashing the renamed file
    /// fails). This is the case that makes flag timing load-bearing.</summary>
    IoError,
}

/// <summary>The service-restoring action the updater must take for an outcome.</summary>
internal enum RecoveryAction
{
    /// <summary>Nothing was installed; relaunch the OLD bridge (config untouched)
    /// and require its Ready. Never a plain process exit — that would leave zero
    /// bridges serving after the parent has already gone.</summary>
    RecoverOldBridge,

    /// <summary>The transaction began mutating the install (config renamed and/or
    /// binaries replaced); restore every managed file and the exact original config
    /// from backups, then relaunch the old bridge.</summary>
    Rollback,

    /// <summary>The replacement is up and healthy; finalize.</summary>
    Commit,
}

/// <summary>
/// Pure decision table for the ownership-transfer window. Extracted from the
/// updater engine so the exact routing — which every service-restoring path
/// depends on — is a named, unit-testable contract rather than inline control
/// flow. The engine builds an <see cref="OwnershipOutcome"/> and a
/// <paramref name="transactionMutating"/> flag and executes whatever action this
/// returns; it owns the async process/IPC work, this owns the policy.
/// </summary>
internal static class OwnershipWindowRouter
{
    /// <summary>
    /// Map an ownership-window outcome to its recovery action.
    /// </summary>
    /// <param name="outcome">What happened in the window.</param>
    /// <param name="transactionMutating">
    /// True once the transaction has begun mutating the install — set immediately
    /// BEFORE <see cref="ManagedInstallManager.Cutover"/>, because Cutover becomes
    /// destructive the instant it renames the live config, and a throw partway
    /// through must therefore roll back (restoring the config) rather than merely
    /// relaunch the old bridge without it.
    /// </param>
    public static RecoveryAction Route(OwnershipOutcome outcome, bool transactionMutating) => outcome switch
    {
        // Nothing installed yet → relaunch the old bridge; its config is untouched.
        // This is NOT a fail-open: after authorization there is no serving bridge,
        // so exiting the updater without relaunching would strand the user.
        OwnershipOutcome.ParentExitUnconfirmed => RecoveryAction.RecoverOldBridge,
        OwnershipOutcome.DriftAfterHandoff => RecoveryAction.RecoverOldBridge,

        // Cutover started mutating (config renamed, maybe binaries replaced) →
        // rollback restores everything, including the exact original config.
        OwnershipOutcome.CutoverFailed => RecoveryAction.Rollback,
        OwnershipOutcome.TargetNotReady => RecoveryAction.Rollback,

        OwnershipOutcome.TargetReady => RecoveryAction.Commit,

        // Cancellation / I/O error: the branch that hinges on flag timing. If the
        // transaction had begun mutating (true throughout Cutover, including a
        // throw after the config rename), we MUST rollback so the config is
        // restored; only when nothing was mutated is relaunch-old-bridge correct.
        OwnershipOutcome.Cancelled or OwnershipOutcome.IoError =>
            transactionMutating ? RecoveryAction.Rollback : RecoveryAction.RecoverOldBridge,

        _ => RecoveryAction.Rollback, // conservative default: prefer full restore
    };
}
